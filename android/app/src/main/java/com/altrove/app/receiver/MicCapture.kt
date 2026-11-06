package com.altrove.app.receiver

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Build
import android.util.Log
import com.altrove.app.protocol.AudioCodecs
import com.altrove.app.protocol.MicStart
import com.altrove.app.protocol.VideoPacket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * This device's microphone for the PC's "Microfono Altrove" (docs/PROTOCOLLO.md, micStart): 48 kHz mono from the
 * voice-call source (echo cancellation and noise suppression where the device has them, so the PC's own sound
 * coming out of this speaker is not sent back), compressed to Opus at 64 kbit/s when Android can (10 and later),
 * else sent as 16-bit PCM. [send] gets each binary packet, kind 3 header included.
 */
class MicCapture(private val send: (ByteArray) -> Unit) {
    private val record: AudioRecord
    private val encoder: MediaCodec?
    private val effects = ArrayList<android.media.audiofx.AudioEffect>()
    private var thread: Thread? = null
    @Volatile private var running = true
    private val startNanos = System.nanoTime()

    val format: MicStart

    init {
        val minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        record = newRecord(maxOf(minBuffer, FRAME_BYTES * 8))
        if (AcousticEchoCanceler.isAvailable()) AcousticEchoCanceler.create(record.audioSessionId)?.let { it.enabled = true; effects += it }
        if (NoiseSuppressor.isAvailable()) NoiseSuppressor.create(record.audioSessionId)?.let { it.enabled = true; effects += it }
        encoder = runCatching { createOpusEncoder() }.onFailure { Log.i(TAG, "No Opus encoder, sending PCM: ${it.message}") }.getOrNull()
        format = MicStart(if (encoder != null) AudioCodecs.OPUS else AudioCodecs.PCM, SAMPLE_RATE, 1, if (encoder != null) 20 else FRAME_MS)
    }

    /** Starts recording; call after the PC was told [format], so the packets come after it. */
    fun start() {
        record.startRecording()
        thread = Thread(::loop, "Altrove microphone").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    @SuppressLint("MissingPermission") // the controller starts this only with RECORD_AUDIO granted
    private fun newRecord(bufferBytes: Int) = AudioRecord(
        MediaRecorder.AudioSource.VOICE_COMMUNICATION,
        SAMPLE_RATE,
        AudioFormat.CHANNEL_IN_MONO,
        AudioFormat.ENCODING_PCM_16BIT,
        bufferBytes,
    )

    private fun loop() {
        val pcm = ByteArray(FRAME_BYTES)
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                var read = 0
                while (read < pcm.size && running) {
                    val n = record.read(pcm, read, pcm.size - read)
                    if (n <= 0) return
                    read += n
                }
                val time = (System.nanoTime() - startNanos) / 1000
                val codec = encoder
                if (codec == null) {
                    send(packet(pcm, 0, pcm.size, time))
                    continue
                }
                val input = codec.dequeueInputBuffer(10_000)
                if (input >= 0) {
                    codec.getInputBuffer(input)!!.apply { clear(); put(pcm) }
                    codec.queueInputBuffer(input, 0, pcm.size, time, 0)
                }
                while (true) {
                    val output = codec.dequeueOutputBuffer(info, 0)
                    if (output < 0) break
                    // The first buffer is the Opus header (codec config): the PC needs only the packets.
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0 && info.size > 0) {
                        val data = ByteArray(info.size)
                        codec.getOutputBuffer(output)!!.apply { position(info.offset); get(data) }
                        send(packet(data, 0, data.size, info.presentationTimeUs))
                    }
                    codec.releaseOutputBuffer(output, false)
                }
            }
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Microphone stopped", e)
        }
    }

    private fun createOpusEncoder(): MediaCodec {
        check(Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) { "Android ${Build.VERSION.SDK_INT}" }
        val mediaFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, SAMPLE_RATE, 1).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, 64_000)
        }
        val name = MediaCodecList(MediaCodecList.REGULAR_CODECS).findEncoderForFormat(mediaFormat) ?: error("no encoder")
        return MediaCodec.createByCodecName(name).apply {
            configure(mediaFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            start()
        }
    }

    fun release() {
        running = false
        runCatching { record.stop() }
        thread?.join(500)
        effects.forEach { runCatching { it.release() } }
        runCatching { encoder?.stop() }
        runCatching { encoder?.release() }
        record.release()
    }

    companion object {
        private const val TAG = "AltroveMic"
        const val SAMPLE_RATE = 48_000
        const val FRAME_MS = 10
        const val FRAME_BYTES = SAMPLE_RATE * FRAME_MS / 1000 * 2

        /** One binary message: the protocol's 10-byte header, kind 3, then the payload. */
        fun packet(data: ByteArray, offset: Int, length: Int, timeMicros: Long): ByteArray =
            ByteBuffer.allocate(VideoPacket.HEADER_SIZE + length).order(ByteOrder.BIG_ENDIAN).apply {
                put(VideoPacket.KIND_MIC.toByte())
                put(0)
                putLong(timeMicros)
                put(data, offset, length)
            }.array()
    }
}
