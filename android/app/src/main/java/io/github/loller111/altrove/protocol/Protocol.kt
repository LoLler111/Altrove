package io.github.loller111.altrove.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Shared with the Windows app: docs/PROTOCOLLO.md. */
object AltroveProtocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 47800
    const val SERVICE_TYPE = "_altrove._tcp"
}

@Serializable
data class DisplayInfo(val width: Int, val height: Int, val refreshRate: Double)

@Serializable
sealed class ControlMessage

@Serializable
@SerialName("hello")
data class Hello(
    val protocol: Int,
    val role: String,
    val id: String,
    val name: String,
    val app: String? = null,
    val display: DisplayInfo? = null,
    val codecs: List<String>? = null,
    val maxWidth: Int? = null,
    val maxHeight: Int? = null,
) : ControlMessage()

@Serializable
@SerialName("start")
data class Start(val codec: String, val width: Int, val height: Int, val fps: Int, val mode: String) : ControlMessage()

@Serializable
@SerialName("stop")
data object Stop : ControlMessage()

@Serializable
@SerialName("keyframe")
data object Keyframe : ControlMessage()

@Serializable
@SerialName("ping")
data class Ping(val t: Long) : ControlMessage()

@Serializable
@SerialName("pong")
data class Pong(val t: Long) : ControlMessage()

@Serializable
@SerialName("stats")
data class Stats(val fps: Double, val decodeMs: Double? = null, val dropped: Int? = null) : ControlMessage()

@Serializable
@SerialName("bye")
data class Bye(val reason: String) : ControlMessage()

object Messages {
    private val known = setOf("hello", "start", "stop", "keyframe", "ping", "pong", "stats", "bye")

    val json = Json {
        classDiscriminator = "type"
        ignoreUnknownKeys = true
        explicitNulls = false
    }

    fun encode(message: ControlMessage): String = json.encodeToString(ControlMessage.serializer(), message)

    /** Null for message types this version does not know (a newer sender) or for text that is not a message. */
    fun parse(text: String): ControlMessage? = try {
        val type = (json.parseToJsonElement(text) as? JsonObject)?.get("type")?.jsonPrimitive?.contentOrNull
        if (type in known) json.decodeFromString(ControlMessage.serializer(), text) else null
    } catch (e: SerializationException) {
        null
    } catch (e: IllegalArgumentException) {
        null
    }
}

/** One binary message: 10-byte header (kind, flags, presentation time in µs) and an Annex B access unit. */
class VideoPacket(val flags: Int, val presentationMicros: Long, val data: ByteArray, val offset: Int, val length: Int) {
    val isKeyframe get() = flags and FLAG_KEYFRAME != 0
    val hasParameterSets get() = flags and FLAG_PARAMETER_SETS != 0

    companion object {
        const val KIND_VIDEO = 1
        const val HEADER_SIZE = 10
        const val FLAG_KEYFRAME = 1
        const val FLAG_PARAMETER_SETS = 2

        fun parse(buffer: ByteBuffer): VideoPacket? {
            if (buffer.remaining() < HEADER_SIZE) return null
            val bytes = ByteArray(buffer.remaining())
            buffer.get(bytes)
            if (bytes[0].toInt() != KIND_VIDEO) return null
            val pts = ByteBuffer.wrap(bytes, 2, 8).order(ByteOrder.BIG_ENDIAN).long
            return VideoPacket(bytes[1].toInt() and 0xFF, pts, bytes, HEADER_SIZE, bytes.size - HEADER_SIZE)
        }
    }
}

/** Just enough Annex B parsing to pull SPS and PPS out of a keyframe. */
object H264 {
    const val NAL_IDR = 5
    const val NAL_SPS = 7
    const val NAL_PPS = 8

    /** (offset, length) of every NAL unit, without its start code. */
    fun nalUnits(data: ByteArray, offset: Int = 0, length: Int = data.size - offset): List<IntArray> {
        val end = offset + length
        val result = ArrayList<IntArray>()
        var start = nextStart(data, offset, end)
        while (start != null) {
            val payload = start[0] + start[1]
            val next = nextStart(data, payload, end)
            val stop = next?.get(0) ?: end
            if (stop > payload) result.add(intArrayOf(payload, stop - payload))
            start = next
        }
        return result
    }

    fun nalType(data: ByteArray, nalOffset: Int) = data[nalOffset].toInt() and 0x1F

    /** SPS and PPS with 4-byte start codes, ready for MediaFormat's csd-0 and csd-1; null if either is missing. */
    fun parameterSets(data: ByteArray, offset: Int, length: Int): Pair<ByteArray, ByteArray>? {
        var sps: ByteArray? = null
        var pps: ByteArray? = null
        for ((nalOffset, nalLength) in nalUnits(data, offset, length).map { it[0] to it[1] }) {
            when (nalType(data, nalOffset)) {
                NAL_SPS -> sps = withStartCode(data, nalOffset, nalLength)
                NAL_PPS -> pps = withStartCode(data, nalOffset, nalLength)
            }
        }
        return if (sps != null && pps != null) sps to pps else null
    }

    private fun withStartCode(data: ByteArray, offset: Int, length: Int) =
        byteArrayOf(0, 0, 0, 1) + data.copyOfRange(offset, offset + length)

    // [index of the first zero, start code length] of the next 00 00 01 / 00 00 00 01 at or after `from`.
    private fun nextStart(data: ByteArray, from: Int, end: Int): IntArray? {
        var i = from
        while (i + 2 < end) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0 && data[i + 2].toInt() == 1) {
                return if (i > from && data[i - 1].toInt() == 0) intArrayOf(i - 1, 4) else intArrayOf(i, 3)
            }
            i++
        }
        return null
    }
}
