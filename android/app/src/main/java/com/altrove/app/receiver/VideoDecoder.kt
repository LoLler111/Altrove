package com.altrove.app.receiver

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import com.altrove.app.protocol.H264
import com.altrove.app.protocol.VideoPacket
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

/**
 * Hardware H.264 decoding straight onto a Surface, tuned for latency: low-latency mode where the
 * device has it, every decoded frame shown at once, and when frames pile up only the newest shown.
 */
class VideoDecoder(
    private val surface: Surface,
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    /** Called when the decoder needs a fresh IDR: at start and after an error. */
    private val requestKeyframe: () -> Unit,
) {
    private val queue = LinkedBlockingDeque<VideoPacket>()
    @Volatile private var running = true
    private var codec: MediaCodec? = null
    private val thread = Thread(::loop, "Altrove decoder").apply { priority = Thread.MAX_PRIORITY }

    // Counters for the stats message, read and reset once a second.
    @Volatile var renderedFrames = 0
        private set
    @Volatile var droppedFrames = 0
        private set
    @Volatile var decodeMillis = 0.0
        private set

    val decoderName: String? get() = codec?.name

    fun start() = thread.start()

    /** Takes a frame from the network thread. Before the first IDR, and when far behind, frames are dropped. */
    fun submit(packet: VideoPacket) {
        if (queue.size > MAX_QUEUED && !packet.isKeyframe) {
            // Far behind: throw the backlog away and wait for the sender's next IDR. The marker tells the
            // decoder thread where the hole is, in stream order: a flag set from here could be undone by a
            // keyframe it took just before the clear, and the frames after the hole would be decoded.
            droppedFrames += queue.size + 1
            queue.clear()
            queue.offer(GAP)
            requestKeyframe()
            return
        }
        queue.offer(packet)
    }

    // Decoder thread only. Every P-frame refers to the frames before it: once one is missing, nothing
    // until the next IDR may reach the decoder, or the picture smears until the screen changes there.
    private var waitingForKeyframe = true

    fun takeStats(): Triple<Int, Int, Double> {
        val result = Triple(renderedFrames, droppedFrames, decodeMillis)
        renderedFrames = 0
        droppedFrames = 0
        return result
    }

    fun release() {
        running = false
        thread.interrupt()
        thread.join(1000)
    }

    private fun loop() {
        requestKeyframe()
        val info = MediaCodec.BufferInfo()
        val queuedAt = HashMap<Long, Long>()
        try {
            while (running) {
                val packet = try {
                    queue.poll(5, TimeUnit.MILLISECONDS)
                } catch (e: InterruptedException) {
                    break
                }
                if (packet === GAP) {
                    waitingForKeyframe = true
                } else if (packet != null) {
                    if (waitingForKeyframe) {
                        if (!packet.isKeyframe) {
                            droppedFrames++
                        } else {
                            waitingForKeyframe = false
                        }
                    }
                    if (!waitingForKeyframe) {
                        if (codec == null) codec = configure(packet) ?: run {
                            waitingForKeyframe = true
                            null
                        }
                        codec?.let { queueInput(it, packet, info, queuedAt) }
                    }
                }
                codec?.let { drainOutput(it, info, queuedAt) }
            }
        } catch (e: Exception) {
            if (running) Log.w(TAG, "Decoder failed", e)
        } finally {
            try {
                codec?.stop()
            } catch (_: Exception) {
            }
            codec?.release()
            codec = null
        }
    }

    private fun configure(keyframe: VideoPacket): MediaCodec? {
        val parameterSets = H264.parameterSets(keyframe.data, keyframe.offset, keyframe.length)
        if (parameterSets == null) {
            requestKeyframe()
            return null
        }
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setByteBuffer("csd-0", java.nio.ByteBuffer.wrap(parameterSets.first))
            setByteBuffer("csd-1", java.nio.ByteBuffer.wrap(parameterSets.second))
            setInteger(MediaFormat.KEY_PRIORITY, 0) // real time
            setInteger(MediaFormat.KEY_OPERATING_RATE, fps.coerceAtLeast(30))
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, width * height)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            // Vendor switches for the same thing on older Qualcomm and Exynos firmware; unknown keys are ignored.
            setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
            setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
        }
        val name = pickDecoder(format)
        val codec = if (name != null) MediaCodec.createByCodecName(name) else MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        return try {
            codec.configure(format, surface, null, 0)
            codec.start()
            Log.i(TAG, "Decoding ${width}x$height with ${codec.name}")
            codec
        } catch (e: Exception) {
            Log.w(TAG, "Cannot configure ${codec.name}", e)
            codec.release()
            null
        }
    }

    /** A hardware decoder that supports low latency if there is one, else the system's choice. */
    private fun pickDecoder(format: MediaFormat): String? {
        val candidates = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, ignoreCase = true) } &&
                (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || info.isHardwareAccelerated)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            candidates.firstOrNull { info ->
                info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC)
                    .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
            }?.let { return it.name }
        }
        // Before Android 10 hardware and software decoders look alike: let the system pick (it puts hardware first).
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) candidates.firstOrNull()?.let { return it.name }
        return MediaCodecList(MediaCodecList.REGULAR_CODECS).findDecoderForFormat(format)
    }

    private fun queueInput(codec: MediaCodec, packet: VideoPacket, info: MediaCodec.BufferInfo, queuedAt: HashMap<Long, Long>) {
        // A busy decoder (frames arrive in bursts over Wi-Fi) gets a moment rather than losing the frame:
        // every frame after a lost one would be decoded against a picture it was not made from.
        val deadline = SystemClock.elapsedRealtime() + INPUT_WAIT_MS
        var index = codec.dequeueInputBuffer(5_000)
        while (index < 0) {
            // Decoded frames it still holds can be what keeps its input full.
            drainOutput(codec, info, queuedAt)
            if (!running || SystemClock.elapsedRealtime() > deadline) {
                Log.w(TAG, "Decoder busy for ${INPUT_WAIT_MS} ms: frame dropped, waiting for an IDR")
                lose()
                return
            }
            index = codec.dequeueInputBuffer(5_000)
        }
        val buffer = codec.getInputBuffer(index)
        if (buffer == null || packet.length > buffer.capacity()) {
            codec.queueInputBuffer(index, 0, 0, packet.presentationMicros, 0)
            lose()
            return
        }
        buffer.clear()
        buffer.put(packet.data, packet.offset, packet.length)
        queuedAt[packet.presentationMicros] = SystemClock.elapsedRealtimeNanos()
        if (queuedAt.size > 64) queuedAt.clear()
        codec.queueInputBuffer(index, 0, packet.length, packet.presentationMicros, 0)
    }

    /** A frame never reached the decoder: skip to the next IDR and ask the sender for one now. */
    private fun lose() {
        droppedFrames++
        waitingForKeyframe = true
        requestKeyframe()
    }

    private fun drainOutput(codec: MediaCodec, info: MediaCodec.BufferInfo, queuedAt: HashMap<Long, Long>) {
        // Show only the newest decoded frame: an older one would only add delay.
        var pending = -1
        while (true) {
            val index = codec.dequeueOutputBuffer(info, 0)
            if (index < 0) break
            if (pending >= 0) {
                codec.releaseOutputBuffer(pending, false)
                droppedFrames++
            }
            pending = index
            queuedAt.remove(info.presentationTimeUs)?.let { start ->
                val ms = (SystemClock.elapsedRealtimeNanos() - start) / 1e6
                decodeMillis = if (decodeMillis == 0.0) ms else decodeMillis * 0.9 + ms * 0.1
            }
        }
        if (pending >= 0) {
            codec.releaseOutputBuffer(pending, true)
            renderedFrames++
        }
    }

    private companion object {
        const val TAG = "AltroveDecoder"
        // About 130 ms at 60 fps, as on the sender side.
        const val MAX_QUEUED = 8
        // Longest wait for a free decoder input before the frame counts as lost.
        const val INPUT_WAIT_MS = 150L
        /** Queue marker: frames were thrown away here. */
        val GAP = VideoPacket(0, 0, ByteArray(0), 0, 0)
    }
}
