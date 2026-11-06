package com.altrove.app.update

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import com.altrove.app.BuildConfig
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import java.io.File
import java.io.IOException

sealed interface UpdateState {
    data object None : UpdateState
    data class Available(val version: String) : UpdateState
    data class Downloading(val version: String, val progress: Float) : UpdateState
    /** Downloaded; Android's installer still needs this app's permission to install apps. */
    data class NeedsPermission(val version: String, val apk: File) : UpdateState
    data class Ready(val version: String, val apk: File) : UpdateState
    data class Failed(val message: String) : UpdateState
}

/**
 * The in-app update: looks for a newer GitHub release when the app starts, downloads it on request
 * and hands it to Android's package installer (which always asks the user to confirm).
 */
class UpdateController(private val context: Context) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val updater = AppUpdater(BuildConfig.LATEST_RELEASE_URL)
    private val _state = MutableStateFlow<UpdateState>(UpdateState.None)
    private var checked = false
    private var release: AppRelease? = null

    val state: StateFlow<UpdateState> = _state

    /** Once per process: a failure (offline, GitHub busy) just shows nothing. */
    fun checkOnce() {
        if (checked) return
        checked = true
        scope.launch {
            try {
                val found = updater.findUpdate(BuildConfig.VERSION_NAME) ?: return@launch
                release = found
                _state.value = UpdateState.Available(found.version)
            } catch (_: IOException) {
            } catch (_: kotlinx.serialization.SerializationException) {
            }
        }
    }

    fun download() {
        val target = release ?: return
        scope.launch {
            try {
                _state.value = UpdateState.Downloading(target.version, 0f)
                val apk = updater.download(target, updatesFolder()) { progress ->
                    _state.value = UpdateState.Downloading(target.version, progress)
                }
                _state.value = UpdateState.Ready(target.version, apk)
                install()
            } catch (e: IOException) {
                _state.value = UpdateState.Failed("Aggiornamento non scaricato: ${e.message}")
            }
        }
    }

    /** Opens Android's installer on the downloaded APK, or first the page that allows it. */
    fun install() {
        val (version, apk) = when (val s = _state.value) {
            is UpdateState.Ready -> s.version to s.apk
            is UpdateState.NeedsPermission -> s.version to s.apk
            else -> return
        }
        if (!context.packageManager.canRequestPackageInstalls()) {
            _state.value = UpdateState.NeedsPermission(version, apk)
            context.startActivity(
                Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
            return
        }
        _state.value = UpdateState.Ready(version, apk)
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", apk)
        try {
            context.startActivity(
                Intent(Intent.ACTION_VIEW)
                    .setDataAndType(uri, "application/vnd.android.package-archive")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        } catch (_: ActivityNotFoundException) {
            _state.value = UpdateState.Failed("Questo dispositivo non ha un programma per installare app.")
        }
    }

    /** Where the APK goes; must match res/xml/update_paths.xml. */
    private fun updatesFolder() = File(context.cacheDir, "updates")
}
