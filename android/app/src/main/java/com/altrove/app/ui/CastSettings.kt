package com.altrove.app.ui

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.Settings
import android.widget.Toast

/**
 * Android's own screen casting (Miracast: Smart View on Samsung, "Trasmetti" or "Duplica schermo" elsewhere).
 * An app cannot open a Miracast link itself (the Wi-Fi Display calls are system-only): it opens the page
 * where the user picks the TV, and once linked Altrove puts the PC's picture there as a second screen.
 */
object CastSettings {
    private val actions = listOf(Settings.ACTION_CAST_SETTINGS, "android.settings.WIFI_DISPLAY_SETTINGS")

    /** A TV, a Fire TV or a TV box: it is the screen others cast to, not one that casts. */
    fun isTvDevice(context: Context) = context.packageManager.hasSystemFeature(PackageManager.FEATURE_LEANBACK)

    fun open(context: Context) {
        val opened = actions.any { action ->
            runCatching { context.startActivity(Intent(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }.isSuccess
        }
        if (!opened) {
            Toast.makeText(context, "Apri la proiezione dalla tendina rapida: Smart View, Trasmetti o Duplica schermo.", Toast.LENGTH_LONG).show()
        }
    }
}
