package io.github.loller111.altrove

import android.view.MotionEvent
import io.github.loller111.altrove.protocol.Configure
import io.github.loller111.altrove.protocol.DisplayInfo
import io.github.loller111.altrove.protocol.Features
import io.github.loller111.altrove.protocol.H264
import io.github.loller111.altrove.protocol.Hello
import io.github.loller111.altrove.protocol.Keyframe
import io.github.loller111.altrove.protocol.Messages
import io.github.loller111.altrove.protocol.Ping
import io.github.loller111.altrove.protocol.Start
import io.github.loller111.altrove.protocol.ScreenChoice
import io.github.loller111.altrove.protocol.Stats
import io.github.loller111.altrove.protocol.StreamSettings
import io.github.loller111.altrove.protocol.Touch
import io.github.loller111.altrove.protocol.TouchPointer
import io.github.loller111.altrove.protocol.TouchStates
import io.github.loller111.altrove.protocol.VideoPacket
import io.github.loller111.altrove.receiver.TouchMapper
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer

/** The same messages the Windows tests use (tests/Altrove.Core.Tests/ProtocolTests.cs). */
class ProtocolTest {
    @Test
    fun parses_what_the_windows_sender_writes() {
        val hello = Messages.parse("""{"type":"hello","protocol":1,"role":"sender","id":"pc","name":"PC-FISSO","app":"0.1.0"}""")
        assertEquals(Hello(1, "sender", "pc", "PC-FISSO", "0.1.0"), hello)
        val start = Messages.parse("""{"type":"start","codec":"h264","width":1280,"height":720,"fps":60,"mode":"mirror"}""")
        assertEquals(Start("h264", 1280, 720, 60, "mirror"), start)
        assertEquals(Ping(42), Messages.parse("""{"t":42,"type":"ping"}"""))
        assertEquals(Keyframe, Messages.parse("""{"type":"keyframe"}"""))
    }

    @Test
    fun unknown_types_and_garbage_are_null() {
        assertNull(Messages.parse("""{"type":"future","x":1}"""))
        assertNull(Messages.parse("not json"))
        assertNull(Messages.parse("""{"no":"type"}"""))
        assertNull(Messages.parse("""{"type":"start","width":"wide"}"""))
    }

    @Test
    fun writes_what_the_windows_sender_reads() {
        val hello = Hello(1, "receiver", "tab", "Tab A", "0.1.0", DisplayInfo(1920, 1200, 60.0), listOf("h264"), 1920, 1080)
        val json = Messages.encode(hello)
        assertTrue(json, json.startsWith("""{"type":"hello""""))
        assertTrue(json, json.contains(""""display":{"width":1920,"height":1200,"refreshRate":60.0}"""))
        assertEquals("""{"type":"keyframe"}""", Messages.encode(Keyframe))
        assertEquals("""{"type":"stats","fps":59.0,"dropped":0}""", Messages.encode(Stats(59.0, dropped = 0)))
    }

    @Test
    fun video_packet_header() {
        val bytes = byteArrayOf(1, 3, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0x65)
        val packet = VideoPacket.parse(ByteBuffer.wrap(bytes))!!
        assertTrue(packet.isKeyframe)
        assertTrue(packet.hasParameterSets)
        assertEquals(256L, packet.presentationMicros)
        assertEquals(5, packet.length)
        assertNull(VideoPacket.parse(ByteBuffer.wrap(byteArrayOf(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1))))
    }

    @Test
    fun finds_sps_and_pps() {
        val data = byteArrayOf(0, 0, 0, 1, 0x67, 0xAA.toByte(), 0, 0, 1, 0x68, 0xBB.toByte(), 0, 0, 0, 1, 0x65, 0x11)
        assertEquals(3, H264.nalUnits(data).size)
        val (sps, pps) = H264.parameterSets(data, 0, data.size)!!
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x67, 0xAA.toByte()), sps)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x68, 0xBB.toByte()), pps)
        assertNull(H264.parameterSets(byteArrayOf(0, 0, 1, 0x65, 1), 0, 5))
    }

    // The same JSON as tests/Altrove.Core.Tests/ProtocolTests.cs.

    @Test
    fun hello_features() {
        val hello = Messages.parse("""{"type":"hello","protocol":1,"role":"sender","id":"pc","name":"PC-FISSO","features":["touch","configure"]}""") as Hello
        assertTrue(hello.supports(Features.TOUCH))
        assertTrue(hello.supports(Features.CONFIGURE))
        assertFalse(Hello(1, "sender", "pc", "PC").supports(Features.TOUCH))
    }

    @Test
    fun touch_is_written_as_the_windows_sender_reads_it() {
        val touch = Touch(listOf(TouchPointer(0, 0.25, 0.5, TouchStates.DOWN), TouchPointer(1, 1.0, 0.0, TouchStates.MOVE)))
        assertEquals(
            """{"type":"touch","pointers":[{"id":0,"x":0.25,"y":0.5,"state":"down"},{"id":1,"x":1.0,"y":0.0,"state":"move"}]}""",
            Messages.encode(touch),
        )
    }

    @Test
    fun configure_carries_only_what_changes() {
        assertEquals("""{"type":"configure","fps":30}""", Messages.encode(Configure(fps = 30)))
        assertEquals(Configure(mode = "extend", maxHeight = 720), Messages.parse("""{"type":"configure","mode":"extend","maxHeight":720}"""))
    }

    @Test
    fun parses_the_settings_the_windows_sender_writes() {
        val json = """{"type":"settings","mode":"mirror","screen":"\\\\.\\DISPLAY1","maxHeight":0,"fps":60,"bitrateKbps":12000,""" +
            """"choices":{"modes":["mirror","extend"],"screens":[{"id":"\\\\.\\DISPLAY1","name":"Schermo 1"}],""" +
            """"maxHeights":[0,1080,720],"fps":[60,30],"bitratesKbps":[6000,12000]}}"""
        val settings = Messages.parse(json) as StreamSettings
        assertEquals("\\\\.\\DISPLAY1", settings.screen)
        assertEquals(listOf(ScreenChoice("\\\\.\\DISPLAY1", "Schermo 1")), settings.choices.screens)
        assertEquals(listOf(0, 1080, 720), settings.choices.maxHeights)
        assertNull(settings.notice)
    }

    @Test
    fun motion_events_become_touch_messages() {
        // Second finger down while the first stays: both listed, only the new one "down".
        val two = TouchMapper.message(MotionEvent.ACTION_POINTER_DOWN, 1, intArrayOf(0, 1), floatArrayOf(100f, 1999f), floatArrayOf(50f, 600f), 2000, 1200)!!
        assertEquals(listOf(TouchPointer(0, 0.05, 0.0417, TouchStates.MOVE), TouchPointer(1, 0.9995, 0.5, TouchStates.DOWN)), two.pointers)
        val up = TouchMapper.message(MotionEvent.ACTION_POINTER_UP, 0, intArrayOf(0, 1), floatArrayOf(-5f, 2100f), floatArrayOf(0f, 0f), 2000, 1200)!!
        assertEquals(listOf(TouchStates.UP, TouchStates.MOVE), up.pointers.map { it.state })
        assertEquals(listOf(0.0, 1.0), up.pointers.map { it.x }) // clamped to the picture
        assertEquals(TouchStates.CANCEL, TouchMapper.message(MotionEvent.ACTION_CANCEL, 0, intArrayOf(3), floatArrayOf(1f), floatArrayOf(1f), 10, 10)!!.pointers[0].state)
        assertNull(TouchMapper.message(MotionEvent.ACTION_HOVER_MOVE, 0, intArrayOf(0), floatArrayOf(1f), floatArrayOf(1f), 10, 10))
        assertNull(TouchMapper.message(MotionEvent.ACTION_DOWN, 0, intArrayOf(0), floatArrayOf(1f), floatArrayOf(1f), 0, 10))
    }
}
