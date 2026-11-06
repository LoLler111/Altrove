package com.altrove.app.update

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/** A GitHub release newer than this app, with the APK attached to it. */
data class AppRelease(val version: String, val apkUrl: String, val apkSize: Long)

/**
 * Finds the latest release of the public repository and downloads its APK. The Windows app updates
 * from the same releases, and both carry the same version number (version.txt).
 */
class AppUpdater(private val latestReleaseUrl: String) {
    /** The latest release when it is newer than [currentVersion] and has an APK; otherwise null. */
    suspend fun findUpdate(currentVersion: String): AppRelease? = withContext(Dispatchers.IO) {
        val connection = open(latestReleaseUrl).apply { setRequestProperty("Accept", "application/vnd.github+json") }
        try {
            // 404: no release published yet.
            if (connection.responseCode == 404) return@withContext null
            if (connection.responseCode != 200) throw IOException("GitHub ha risposto ${connection.responseCode}.")
            parseLatest(connection.inputStream.bufferedReader().use { it.readText() }, currentVersion)
        } finally {
            connection.disconnect()
        }
    }

    /** Downloads the APK into [folder] (emptied first), reporting progress from 0 to 1. */
    suspend fun download(release: AppRelease, folder: File, onProgress: (Float) -> Unit): File = withContext(Dispatchers.IO) {
        folder.deleteRecursively()
        folder.mkdirs()
        val target = File(folder, "Altrove-${release.version}.apk")
        val partial = File(folder, target.name + ".part")
        // Release files redirect to GitHub's file host; HttpURLConnection follows https -> https redirects.
        val connection = open(release.apkUrl)
        try {
            if (connection.responseCode != 200) throw IOException("Download non riuscito (${connection.responseCode}).")
            val total = connection.contentLengthLong.takeIf { it > 0 } ?: release.apkSize
            connection.inputStream.use { input ->
                partial.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        done += read
                        if (total > 0) onProgress((done.toFloat() / total).coerceIn(0f, 1f))
                    }
                }
            }
        } finally {
            connection.disconnect()
        }
        if (!partial.renameTo(target)) throw IOException("Impossibile salvare l'aggiornamento.")
        target
    }

    private fun open(url: String) = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = 15_000
        readTimeout = 60_000
        instanceFollowRedirects = true
        setRequestProperty("User-Agent", "Altrove-Android")
    }

    @Serializable
    private data class GithubRelease(
        @SerialName("tag_name") val tagName: String,
        val draft: Boolean = false,
        val prerelease: Boolean = false,
        val assets: List<GithubAsset> = emptyList(),
    )

    @Serializable
    private data class GithubAsset(
        val name: String,
        val size: Long = 0,
        @SerialName("browser_download_url") val downloadUrl: String,
    )

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        /** The release in GitHub's "latest release" answer, if it is newer and carries an APK. */
        fun parseLatest(text: String, currentVersion: String): AppRelease? {
            val release = json.decodeFromString(GithubRelease.serializer(), text)
            val version = release.tagName.removePrefix("v")
            val apk = release.assets.firstOrNull { it.name.endsWith(".apk", ignoreCase = true) }
            return if (release.draft || release.prerelease || apk == null || !isNewer(version, currentVersion)) null
            else AppRelease(version, apk.downloadUrl, apk.size)
        }

        /** Compares dotted versions number by number: "0.10.0" is newer than "0.9.3". */
        fun isNewer(candidate: String, current: String): Boolean {
            val a = parts(candidate)
            val b = parts(current)
            for (i in 0 until maxOf(a.size, b.size)) {
                val diff = a.getOrElse(i) { 0 } - b.getOrElse(i) { 0 }
                if (diff != 0) return diff > 0
            }
            return false
        }

        // "1.2.3-beta" -> [1, 2, 3]; anything after a '-' or '+' is ignored.
        private fun parts(version: String): List<Int> =
            version.substringBefore('-').substringBefore('+').split('.').map { it.toIntOrNull() ?: 0 }
    }
}
