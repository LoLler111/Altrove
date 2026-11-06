package com.altrove.app

import com.altrove.app.update.AppRelease
import com.altrove.app.update.AppUpdater
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AppUpdaterTest {
    private fun release(tag: String, draft: Boolean = false, apk: Boolean = true) = """
        {"tag_name":"$tag","draft":$draft,"prerelease":false,"name":"Altrove","assets":[
          {"name":"Altrove-win-Setup.exe","size":1,"browser_download_url":"https://example/setup"}
          ${if (apk) """,{"name":"Altrove-android.apk","size":12345,"browser_download_url":"https://example/apk"}""" else ""}
        ]}
    """.trimIndent()

    @Test
    fun newer_release_with_an_apk_is_an_update() {
        assertEquals(AppRelease("0.3.0", "https://example/apk", 12345), AppUpdater.parseLatest(release("v0.3.0"), "0.2.0"))
    }

    @Test
    fun same_older_draft_or_apk_less_releases_are_not() {
        assertNull(AppUpdater.parseLatest(release("v0.2.0"), "0.2.0"))
        assertNull(AppUpdater.parseLatest(release("v0.1.9"), "0.2.0"))
        assertNull(AppUpdater.parseLatest(release("v0.3.0", draft = true), "0.2.0"))
        assertNull(AppUpdater.parseLatest(release("v0.3.0", apk = false), "0.2.0"))
    }

    @Test
    fun versions_compare_by_number() {
        assertTrue(AppUpdater.isNewer("0.10.0", "0.9.3"))
        assertTrue(AppUpdater.isNewer("1.0", "0.99.99"))
        assertFalse(AppUpdater.isNewer("0.2.0-beta", "0.2.0"))
        assertFalse(AppUpdater.isNewer("0.2", "0.2.0"))
    }
}
