package com.altrove.app

import android.os.Build
import android.Manifest
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.enableEdgeToEdge
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import com.altrove.app.protocol.DisplayInfo
import com.altrove.app.receiver.ReceiverController
import com.altrove.app.receiver.ReceiverState
import com.altrove.app.ui.AltroveTheme
import com.altrove.app.ui.ReceiverScreen
import com.altrove.app.ui.TvOutput
import com.altrove.app.update.UpdateController
import com.altrove.app.update.UpdateState
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private lateinit var receiver: ReceiverController
    private lateinit var updates: UpdateController
    private lateinit var tv: TvOutput

    // Asked when the PC turns "Microfono Altrove" on and Android has not allowed the microphone yet.
    private val micPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        receiver.onMicPermission(granted)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        receiver = ReceiverController(applicationContext)
        updates = UpdateController(applicationContext)
        tv = TvOutput(this, receiver)
        setContent {
            AltroveTheme {
                ReceiverScreen(receiver, updates)
            }
        }
        // While a picture is on screen: no sleeping, no system bars over it.
        lifecycleScope.launch {
            receiver.micPermissionNeeded.collect { needed ->
                if (needed) micPermission.launch(Manifest.permission.RECORD_AUDIO)
            }
        }
        // A TV linked with Smart View or Trasmetti takes the picture while the PC streams.
        lifecycleScope.launch {
            combine(receiver.state, receiver.useTv) { state, _ -> state }.collect(tv::update)
        }
        lifecycleScope.launch {
            receiver.state.collect { state ->
                val streaming = state is ReceiverState.Streaming
                if (streaming) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                val controller = WindowCompat.getInsetsController(window, window.decorView)
                if (streaming) {
                    controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                    controller.hide(WindowInsetsCompat.Type.systemBars())
                } else {
                    controller.show(WindowInsetsCompat.Type.systemBars())
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        receiver.start(displayInfo())
        tv.start()
        updates.checkOnce()
    }

    override fun onResume() {
        super.onResume()
        // Back from the page that allows installing apps: carry on with the downloaded update.
        if (updates.state.value is UpdateState.NeedsPermission && packageManager.canRequestPackageInstalls()) updates.install()
    }

    override fun onStop() {
        tv.stop()
        receiver.stop()
        super.onStop()
    }

    /** The physical screen in pixels, landscape or portrait as it is now. */
    @Suppress("DEPRECATION")
    private fun displayInfo(): DisplayInfo {
        val refresh = (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) display else windowManager.defaultDisplay)?.refreshRate ?: 60f
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = windowManager.maximumWindowMetrics.bounds
            return DisplayInfo(bounds.width(), bounds.height(), refresh.toDouble())
        }
        val metrics = DisplayMetrics()
        windowManager.defaultDisplay.getRealMetrics(metrics)
        return DisplayInfo(metrics.widthPixels, metrics.heightPixels, refresh.toDouble())
    }
}
