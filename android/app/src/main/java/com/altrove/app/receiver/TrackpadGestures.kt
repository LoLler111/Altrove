package com.altrove.app.receiver

import android.view.MotionEvent
import com.altrove.app.protocol.Mouse
import com.altrove.app.protocol.MouseActions
import com.altrove.app.protocol.MouseButtons
import kotlin.math.hypot

/**
 * The video as a laptop trackpad (docs/PROTOCOLLO.md, "mouse"): one finger moves the pointer, a tap clicks,
 * a two-finger tap right-clicks, two fingers scroll, and tap then press-and-drag drags. A single tap waits
 * [doubleTapMs] before clicking, to tell it from the start of a drag or a double click: whoever feeds the
 * events calls [onTimeout] at [deadline].
 */
class TrackpadGestures(
    private val slopPx: Float,
    private val notchPx: Float,
    private val sensitivity: Double = 1.5,
    private val tapMs: Long = 250,
    private val doubleTapMs: Long = 300,
) {
    private var downTime = 0L
    private var startX = 0f
    private var startY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var moved = false
    private var maxPointers = 0
    private var dragging = false
    private var secondTap = false
    private var lastTapUp = 0L

    /** When [onTimeout] must run to deliver a waiting click; null when nothing waits. */
    var deadline: Long? = null
        private set

    fun onEvent(event: MotionEvent, width: Int, height: Int): List<Mouse> {
        val count = event.pointerCount
        return onEvent(
            event.actionMasked, event.actionIndex,
            FloatArray(count) { event.getX(it) }, FloatArray(count) { event.getY(it) },
            event.eventTime, width, height,
        )
    }

    fun onEvent(action: Int, actionIndex: Int, xs: FloatArray, ys: FloatArray, time: Long, width: Int, height: Int): List<Mouse> {
        if (width <= 0 || height <= 0 || xs.isEmpty()) return emptyList()
        val out = ArrayList<Mouse>()
        when (action) {
            MotionEvent.ACTION_DOWN -> {
                if (deadline != null && time - lastTapUp <= doubleTapMs) {
                    // Second touch soon after a tap: a drag or a double click, decided when it moves or lifts.
                    secondTap = true
                    deadline = null
                } else {
                    flushPendingClick(out)
                    secondTap = false
                }
                downTime = time
                startX = xs[0]; startY = ys[0]; lastX = xs[0]; lastY = ys[0]
                moved = false
                maxPointers = 1
                dragging = false
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                maxPointers = maxOf(maxPointers, xs.size)
                val (x, y) = average(xs, ys)
                startX = x; startY = y; lastX = x; lastY = y
            }
            MotionEvent.ACTION_MOVE -> {
                val (x, y) = if (xs.size >= 2) average(xs, ys) else xs[0] to ys[0]
                if (!moved && hypot(x - startX, y - startY) > slopPx) {
                    moved = true
                    if (secondTap && xs.size == 1 && maxPointers == 1) {
                        dragging = true
                        out += Mouse(MouseActions.DOWN, button = MouseButtons.LEFT)
                    }
                }
                if (moved) {
                    val dx = x - lastX
                    val dy = y - lastY
                    if (xs.size >= 2) {
                        // Natural scrolling: the content follows the fingers.
                        if (dx != 0f || dy != 0f) out += Mouse(MouseActions.SCROLL, dx = round(-dx / notchPx), dy = round(dy / notchPx))
                    } else if (maxPointers == 1 && (dx != 0f || dy != 0f)) {
                        out += Mouse(MouseActions.MOVE, dx = round(dx / width * sensitivity), dy = round(dy / height * sensitivity))
                    }
                }
                lastX = x; lastY = y
            }
            MotionEvent.ACTION_POINTER_UP -> {
                // The fingers left keep going from where they are, without a jump.
                val left = xs.indices.filter { it != actionIndex }
                if (left.isNotEmpty()) {
                    lastX = left.map { xs[it] }.average().toFloat()
                    lastY = left.map { ys[it] }.average().toFloat()
                }
            }
            MotionEvent.ACTION_UP -> {
                val quick = time - downTime < tapMs
                when {
                    dragging -> out += Mouse(MouseActions.UP, button = MouseButtons.LEFT)
                    maxPointers >= 2 -> if (!moved && time - downTime < 2 * tapMs) out += Mouse(MouseActions.CLICK, button = MouseButtons.RIGHT)
                    !moved && quick && secondTap -> {
                        out += Mouse(MouseActions.CLICK, button = MouseButtons.LEFT)
                        out += Mouse(MouseActions.CLICK, button = MouseButtons.LEFT)
                    }
                    !moved && quick -> {
                        lastTapUp = time
                        deadline = time + doubleTapMs
                    }
                    secondTap -> out += Mouse(MouseActions.CLICK, button = MouseButtons.LEFT) // the first tap, still owed
                }
                dragging = false
                secondTap = false
            }
            MotionEvent.ACTION_CANCEL -> {
                if (dragging) out += Mouse(MouseActions.UP, button = MouseButtons.LEFT)
                dragging = false
                secondTap = false
                deadline = null
            }
        }
        return out
    }

    /** The waiting single click, once its time has come. */
    fun onTimeout(time: Long): List<Mouse> {
        val due = deadline ?: return emptyList()
        if (time < due) return emptyList()
        return ArrayList<Mouse>().also { flushPendingClick(it) }
    }

    private fun flushPendingClick(out: MutableList<Mouse>) {
        if (deadline != null) {
            deadline = null
            out += Mouse(MouseActions.CLICK, button = MouseButtons.LEFT)
        }
    }

    private fun average(xs: FloatArray, ys: FloatArray) = xs.average().toFloat() to ys.average().toFloat()

    // 5 decimals keep the JSON short and still move by a tenth of a pixel on a 4K screen.
    private fun round(value: Double) = Math.round(value * 100000.0) / 100000.0
    private fun round(value: Float) = round(value.toDouble())
}
