package com.altrove.app.receiver

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.util.Log
import com.altrove.app.protocol.AudioCodecs
import com.altrove.app.protocol.AudioStart
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

/**
 * Plays the PC's sound (docs/PROTOCOLLO.md, audioStart): Opus through Android's decoder, or 16-bit PCM as it is,
 * into a low-latency AudioTrack. Sound is played as soon as it comes; when more than [MAX_LATENCY_MS] piles up
 * (Wi-Fi hiccup) the oldest is dropped, so the delay never grows.
 */
class AudioPlayer(private val format: AudioStart) {
    private val packets = LinkedBlockingDeque<ByteArray>()
    private val track: AudioTrack
    private val decoder: MediaCodec?
    private val thread: Thread
    @Volatile private var running = true
    private var framesWritten = 0L

    /** Packets played so far, for the panel. */
    @Volatile var packetsPlayed = 0
        private set

    init {
        val channelMask = if (format.channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minBuffer = AudioTrack.getMinBufferSize(format.sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        track = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MOVIE).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(format.sampleRate).setChannelMask(channelMask).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minBuffer, bytesFor(60)))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
        decoder = if (format.codec == AudioCodecs.OPUS) createOpusDecoder() else null
        track.play()
        thread = Thread(::loop, "Altrove audio").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    fun submit(data: ByteArray, offset: Int, length: Int) {
        if (!running) return
        packets.offer(data.copyOfRange(offset, offset + length))
        // Too much waiting: throw the oldest away rather than play it late.
        val limit = MAX_LATENCY_MS / maxOf(1, format.frameMs)
        while (packets.size > limit) packets.pollFirst()
    }

    private fun loop() {
        try {
            while (running) {
                val packet = packets.pollFirst(100, TimeUnit.MILLISECONDS) ?: continue
                if (decoder == null) play(ByteBuffer.wrap(packet)) else decode(decoder, packet)
                packetsPlayed++
                if (packetsPlayed % 100 == 0) Log.i(TAG, "Played $packetsPlayed ${format.codec} packets")
            }
        } catch (e: InterruptedException) {
            // released
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Audio stopped", e)
        }
    }

    private fun decode(codec: MediaCodec, packet: ByteArray) {
        val input = codec.dequeueInputBuffer(20_000)
        if (input >= 0) {
            codec.getInputBuffer(input)!!.apply { clear(); put(packet) }
            codec.queueInputBuffer(input, 0, packet.size, System.nanoTime() / 1000, 0)
        }
        val info = MediaCodec.BufferInfo()
        while (true) {
            val output = codec.dequeueOutputBuffer(info, 0)
            if (output < 0) break
            val pcm = codec.getOutputBuffer(output)!!
            pcm.position(info.offset)
            pcm.limit(info.offset + info.size)
            play(pcm)
            codec.releaseOutputBuffer(output, false)
        }
    }

    private fun play(pcm: ByteBuffer) {
        val bytes = pcm.remaining()
        // Behind by more than the limit (e.g. after a pause): skip this block instead of adding to the delay.
        val queuedFrames = framesWritten - track.playbackHeadPosition.toLong()
        if (queuedFrames * 1000 / format.sampleRate > MAX_LATENCY_MS) return
        track.write(pcm, bytes, AudioTrack.WRITE_BLOCKING)
        framesWritten += bytes / (2 * format.channels)
    }

    private fun createOpusDecoder(): MediaCodec {
        val mediaFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, format.sampleRate, format.channels)
        // Android's Opus decoder wants the Ogg identification header and the pre-skip / seek pre-roll in ns.
        mediaFormat.setByteBuffer("csd-0", opusHead(format.channels, format.sampleRate))
        mediaFormat.setByteBuffer("csd-1", longBuffer(0))
        mediaFormat.setByteBuffer("csd-2", longBuffer(80_000_000))
        return MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS).apply {
            configure(mediaFormat, null, null, 0)
            start()
        }
    }

    private fun bytesFor(ms: Int) = format.sampleRate * format.channels * 2 * ms / 1000

    fun release() {
        running = false
        thread.interrupt()
        thread.join(500)
        runCatching { decoder?.stop() }
        runCatching { decoder?.release() }
        runCatching { track.stop() }
        track.release()
    }

    companion object {
        private const val TAG = "AltroveAudio"
        const val MAX_LATENCY_MS = 150

        /** What this device can play, best first, for the hello. */
        fun supportedCodecs(): List<String> {
            val opus = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { info ->
                !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_AUDIO_OPUS, true) }
            }
            return if (opus) listOf(AudioCodecs.OPUS, AudioCodecs.PCM) else listOf(AudioCodecs.PCM)
        }

        /** RFC 7845 identification header: "OpusHead", version 1, channels, pre-skip 0, rate, gain 0, mapping 0. */
        fun opusHead(channels: Int, sampleRate: Int): ByteBuffer = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("OpusHead".toByteArray(Charsets.US_ASCII))
            put(1)
            put(channels.toByte())
            putShort(0)
            putInt(sampleRate)
            putShort(0)
            put(0)
            flip()
        }

        private fun longBuffer(value: Long): ByteBuffer =
            ByteBuffer.allocate(8).order(ByteOrder.nativeOrder()).putLong(value).apply { flip() }
    }
}
