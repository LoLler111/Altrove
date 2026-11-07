package com.altrove.app.ui

import android.app.Activity
import android.app.Presentation
import android.graphics.Color
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Display
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import com.altrove.app.protocol.DisplayInfo
import com.altrove.app.receiver.Letterbox
import com.altrove.app.receiver.ReceiverController
import com.altrove.app.receiver.ReceiverState

/**
 * The PC's picture on a TV linked through Android's own casting. A Miracast screen (Smart View, Trasmetti)
 * is offered to apps as a presentation display: while the PC streams, the decoder draws there full screen
 * and this device keeps the panel and the fingers. Without one, or with "Sul tablet" chosen, nothing changes
 * and Android mirrors this screen as usual. Main thread only.
 */
class TvOutput(private val activity: Activity, private val receiver: ReceiverController) {
    private val displays = activity.getSystemService(DisplayManager::class.java)
    private val handler = Handler(Looper.getMainLooper())
    private var tv: Display? = null
    private var stream: ReceiverState.Streaming? = null
    private var presentation: TvPresentation? = null
    private var started = false

    private val listener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = refresh()
        override fun onDisplayRemoved(displayId: Int) = refresh()
        override fun onDisplayChanged(displayId: Int) = refresh()
    }

    fun start() {
        if (started) return
        started = true
        displays.registerDisplayListener(listener, handler)
        refresh()
    }

    fun stop() {
        if (!started) return
        started = false
        displays.unregisterDisplayListener(listener)
        presentation?.dismiss()
        presentation = null
        receiver.onTvShowing(false)
    }

    /** Every receiver state and every change of the user's choice ends up here. */
    fun update(state: ReceiverState) {
        stream = state as? ReceiverState.Streaming
        show()
    }

    private fun refresh() {
        tv = displays.getDisplays(DisplayManager.DISPLAY_CATEGORY_PRESENTATION)
            .firstOrNull { it.displayId != Display.DEFAULT_DISPLAY && it.isValid }
        val tv = tv
        receiver.onTvDisplay(tv?.name, tv?.let { DisplayInfo(it.mode.physicalWidth, it.mode.physicalHeight, it.refreshRate.toDouble()) })
        show()
    }

    private fun show() {
        val tv = tv
        val stream = stream
        val wanted = started && tv != null && stream != null && receiver.useTv.value
        presentation?.let { current ->
            if (!wanted || current.display.displayId != tv?.displayId) {
                presentation = null
                current.dismiss()
            }
        }
        if (wanted && presentation == null) {
            presentation = runCatching {
                TvPresentation(activity, tv!!, receiver).also { created ->
                    created.setOnDismissListener {
                        // Also when Android closes it itself: the TV went away or the link dropped.
                        if (presentation === created) presentation = null
                        receiver.onTvShowing(presentation != null)
                    }
                    created.show()
                }
            }.onFailure { Log.w(TAG, "Cannot show on ${tv?.name}", it) }.getOrNull()
        }
        if (stream != null) presentation?.setStreamSize(stream.width, stream.height)
        receiver.onTvShowing(presentation != null)
    }

    private companion object {
        const val TAG = "AltroveTv"
    }
}

/** The TV's window: black, with the PC's picture as big as fits. */
private class TvPresentation(
    activity: Activity,
    display: Display,
    private val receiver: ReceiverController,
) : Presentation(activity, display, android.R.style.Theme_DeviceDefault_NoActionBar_Fullscreen) {
    private var frame: LetterboxFrame? = null
    private var streamWidth = 16
    private var streamHeight = 9

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val video = SurfaceView(context).apply {
            holder.addCallback(object : SurfaceHolder.Callback {
                override fun surfaceCreated(holder: SurfaceHolder) = receiver.attachSurface(holder.surface)
                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                override fun surfaceDestroyed(holder: SurfaceHolder) = receiver.detachSurface(holder.surface)
            })
        }
        frame = LetterboxFrame(context, video).apply {
            setBackgroundColor(Color.BLACK)
            keepScreenOn = true
            setPicture(streamWidth, streamHeight)
        }
        setContentView(frame!!)
    }

    fun setStreamSize(width: Int, height: Int) {
        streamWidth = width
        streamHeight = height
        frame?.setPicture(width, height)
    }
}

/** One child, centered, in the picture's shape. */
private class LetterboxFrame(context: android.content.Context, private val child: View) : ViewGroup(context) {
    private var pictureWidth = 16
    private var pictureHeight = 9

    init {
        addView(child)
    }

    fun setPicture(width: Int, height: Int) {
        if (width == pictureWidth && height == pictureHeight) return
        pictureWidth = width
        pictureHeight = height
        requestLayout()
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = MeasureSpec.getSize(heightMeasureSpec)
        val (w, h) = Letterbox.fit(pictureWidth, pictureHeight, width, height)
        child.measure(MeasureSpec.makeMeasureSpec(w, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(h, MeasureSpec.EXACTLY))
        setMeasuredDimension(width, height)
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        val x = (r - l - child.measuredWidth) / 2
        val y = (b - t - child.measuredHeight) / 2
        child.layout(x, y, x + child.measuredWidth, y + child.measuredHeight)
    }
}
