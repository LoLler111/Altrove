package io.github.loller111.altrove.receiver

import android.view.MotionEvent
import io.github.loller111.altrove.protocol.Touch
import io.github.loller111.altrove.protocol.TouchPointer
import io.github.loller111.altrove.protocol.TouchStates
import kotlin.math.roundToLong

/** Turns Android touch events on the video into protocol touch messages (docs/PROTOCOLLO.md, "touch"). */
object TouchMapper {
    fun message(event: MotionEvent, width: Int, height: Int): Touch? {
        val count = event.pointerCount
        return message(
            event.actionMasked,
            event.actionIndex,
            IntArray(count) { event.getPointerId(it) },
            FloatArray(count) { event.getX(it) },
            FloatArray(count) { event.getY(it) },
            width,
            height,
        )
    }

    /**
     * Every pointer of the event, at positions from 0 to 1 on a view of [width]×[height]. Null for events
     * that are not touch changes (hover and the like).
     */
    fun message(action: Int, actionIndex: Int, ids: IntArray, xs: FloatArray, ys: FloatArray, width: Int, height: Int): Touch? {
        if (width <= 0 || height <= 0 || ids.isEmpty()) return null
        val pointers = ids.indices.map { i ->
            val state = when (action) {
                MotionEvent.ACTION_DOWN -> TouchStates.DOWN
                MotionEvent.ACTION_POINTER_DOWN -> if (i == actionIndex) TouchStates.DOWN else TouchStates.MOVE
                MotionEvent.ACTION_UP -> TouchStates.UP
                MotionEvent.ACTION_POINTER_UP -> if (i == actionIndex) TouchStates.UP else TouchStates.MOVE
                MotionEvent.ACTION_MOVE -> TouchStates.MOVE
                MotionEvent.ACTION_CANCEL -> TouchStates.CANCEL
                else -> return null
            }
            TouchPointer(ids[i], unit(xs[i], width), unit(ys[i], height), state)
        }
        return Touch(pointers)
    }

    // 4 decimals: a tenth of a pixel even on a 4K screen, and short JSON.
    private fun unit(value: Float, size: Int): Double =
        ((value / size).coerceIn(0f, 1f) * 10000.0).roundToLong() / 10000.0
}
