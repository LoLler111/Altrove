package io.github.loller111.altrove

import io.github.loller111.altrove.protocol.DisplayInfo
import io.github.loller111.altrove.protocol.H264
import io.github.loller111.altrove.protocol.Hello
import io.github.loller111.altrove.protocol.Keyframe
import io.github.loller111.altrove.protocol.Messages
import io.github.loller111.altrove.protocol.Ping
import io.github.loller111.altrove.protocol.Start
import io.github.loller111.altrove.protocol.Stats
import io.github.loller111.altrove.protocol.VideoPacket
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
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
        assertNull(Messages.parse("""{"type":"touch","x":1}"""))
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
}
