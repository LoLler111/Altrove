package io.github.loller111.altrove

import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import io.github.loller111.altrove.protocol.DisplayInfo
import io.github.loller111.altrove.receiver.ReceiverController
import io.github.loller111.altrove.receiver.ReceiverState
import io.github.loller111.altrove.ui.AltroveTheme
import io.github.loller111.altrove.ui.ReceiverScreen
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private lateinit var receiver: ReceiverController

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        receiver = ReceiverController(applicationContext)
        setContent {
            AltroveTheme {
                ReceiverScreen(receiver)
            }
        }
        // While a picture is on screen: no sleeping, no system bars over it.
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
    }

    override fun onStop() {
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
