package com.altrove.app

import android.view.MotionEvent.ACTION_DOWN
import android.view.MotionEvent.ACTION_MOVE
import android.view.MotionEvent.ACTION_POINTER_DOWN
import android.view.MotionEvent.ACTION_POINTER_UP
import android.view.MotionEvent.ACTION_UP
import com.altrove.app.protocol.Mouse
import com.altrove.app.protocol.MouseActions
import com.altrove.app.protocol.MouseButtons
import com.altrove.app.receiver.TrackpadGestures
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TrackpadGesturesTest {
    private val w = 1000
    private val h = 500
    private fun pad() = TrackpadGestures(slopPx = 10f, notchPx = 20f, sensitivity = 1.0)
    private fun TrackpadGestures.one(action: Int, x: Float, y: Float, t: Long) = onEvent(action, 0, floatArrayOf(x), floatArrayOf(y), t, w, h)
    private fun TrackpadGestures.two(action: Int, index: Int, x: Float, y: Float, t: Long) =
        onEvent(action, index, floatArrayOf(x, x + 100), floatArrayOf(y, y), t, w, h)

    private val leftClick = Mouse(MouseActions.CLICK, button = MouseButtons.LEFT)

    @Test
    fun a_tap_clicks_once_its_double_tap_window_is_over() {
        val pad = pad()
        assertTrue(pad.one(ACTION_DOWN, 100f, 100f, 0).isEmpty())
        assertTrue(pad.one(ACTION_UP, 101f, 100f, 80).isEmpty())
        assertEquals(380L, pad.deadline)
        assertTrue(pad.onTimeout(300).isEmpty())
        assertEquals(listOf(leftClick), pad.onTimeout(380))
        assertNull(pad.deadline)
    }

    @Test
    fun one_finger_moves_the_pointer_by_fractions_of_the_image() {
        val pad = pad()
        pad.one(ACTION_DOWN, 100f, 100f, 0)
        assertTrue(pad.one(ACTION_MOVE, 105f, 100f, 10).isEmpty()) // inside the slop
        assertEquals(listOf(Mouse(MouseActions.MOVE, dx = 0.1, dy = 0.0)), pad.one(ACTION_MOVE, 205f, 100f, 20))
        assertEquals(listOf(Mouse(MouseActions.MOVE, dx = 0.0, dy = 0.1)), pad.one(ACTION_MOVE, 205f, 150f, 30))
        assertTrue(pad.one(ACTION_UP, 205f, 150f, 40).isEmpty())
        assertNull(pad.deadline) // a move is not a tap
    }

    @Test
    fun two_finger_tap_is_a_right_click_and_two_fingers_scroll() {
        val pad = pad()
        pad.one(ACTION_DOWN, 100f, 100f, 0)
        pad.two(ACTION_POINTER_DOWN, 1, 100f, 100f, 20)
        pad.two(ACTION_POINTER_UP, 1, 100f, 100f, 120)
        assertEquals(listOf(Mouse(MouseActions.CLICK, button = MouseButtons.RIGHT)), pad.one(ACTION_UP, 100f, 100f, 130))

        val scroll = pad()
        scroll.one(ACTION_DOWN, 100f, 100f, 1000)
        scroll.two(ACTION_POINTER_DOWN, 1, 100f, 100f, 1010)
        scroll.two(ACTION_MOVE, 0, 100f, 115f, 1020) // crosses the slop
        assertEquals(listOf(Mouse(MouseActions.SCROLL, dx = 0.0, dy = 2.0)), scroll.two(ACTION_MOVE, 0, 100f, 155f, 1030))
        assertTrue(scroll.one(ACTION_UP, 100f, 155f, 1100).isEmpty())
    }

    @Test
    fun tap_then_press_and_drag_drags() {
        val pad = pad()
        pad.one(ACTION_DOWN, 100f, 100f, 0)
        pad.one(ACTION_UP, 100f, 100f, 60)
        assertTrue(pad.one(ACTION_DOWN, 100f, 100f, 200).isEmpty()) // within the double-tap window
        val start = pad.one(ACTION_MOVE, 150f, 100f, 260)
        assertEquals(Mouse(MouseActions.DOWN, button = MouseButtons.LEFT), start.first())
        assertEquals(listOf(Mouse(MouseActions.UP, button = MouseButtons.LEFT)), pad.one(ACTION_UP, 150f, 100f, 400))
    }

    @Test
    fun double_tap_is_a_double_click_and_a_new_touch_delivers_a_waiting_click() {
        val pad = pad()
        pad.one(ACTION_DOWN, 100f, 100f, 0)
        pad.one(ACTION_UP, 100f, 100f, 60)
        pad.one(ACTION_DOWN, 100f, 100f, 200)
        assertEquals(listOf(leftClick, leftClick), pad.one(ACTION_UP, 100f, 100f, 260))

        val late = pad()
        late.one(ACTION_DOWN, 100f, 100f, 0)
        late.one(ACTION_UP, 100f, 100f, 60)
        assertEquals(listOf(leftClick), late.one(ACTION_DOWN, 100f, 100f, 900)) // too late for a double tap
    }
}
