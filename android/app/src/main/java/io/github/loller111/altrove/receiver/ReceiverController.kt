package io.github.loller111.altrove.receiver

import android.content.Context
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.provider.Settings
import android.view.Surface
import io.github.loller111.altrove.BuildConfig
import io.github.loller111.altrove.protocol.AltroveProtocol
import io.github.loller111.altrove.protocol.Bye
import io.github.loller111.altrove.protocol.Configure
import io.github.loller111.altrove.protocol.Features
import io.github.loller111.altrove.protocol.StreamSettings
import io.github.loller111.altrove.protocol.Touch
import io.github.loller111.altrove.protocol.ControlMessage
import io.github.loller111.altrove.protocol.DisplayInfo
import io.github.loller111.altrove.protocol.Hello
import io.github.loller111.altrove.protocol.Keyframe
import io.github.loller111.altrove.protocol.Messages
import io.github.loller111.altrove.protocol.Ping
import io.github.loller111.altrove.protocol.Pong
import io.github.loller111.altrove.protocol.Start
import io.github.loller111.altrove.protocol.Stats
import io.github.loller111.altrove.protocol.Stop
import io.github.loller111.altrove.protocol.VideoPacket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.java_websocket.WebSocket
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.UUID

sealed interface ReceiverState {
    /** Listening and announced; [addresses] is what the user can type on the PC if discovery fails. */
    data class Waiting(val name: String, val addresses: List<String>, val port: Int) : ReceiverState
    data class Connected(val senderName: String) : ReceiverState
    data class Streaming(val senderName: String, val width: Int, val height: Int, val fps: Int) : ReceiverState
    data class Failed(val message: String) : ReceiverState
}

/**
 * The receiver mode: a WebSocket server announced on the network, and a decoder drawing on the
 * screen while a sender streams. Lives while the app is on screen.
 */
class ReceiverController(private val context: Context) : ReceiverServer.Listener {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val advertiser = Advertiser(context)
    private val _state = MutableStateFlow<ReceiverState>(ReceiverState.Waiting(deviceName(), localAddresses(), AltroveProtocol.DEFAULT_PORT))
    private val _stats = MutableStateFlow("")
    private var server: ReceiverServer? = null
    private var display: DisplayInfo = DisplayInfo(1920, 1080, 60.0)
    private var statsJob: Job? = null

    private val lock = Any()
    private var surface: Surface? = null
    private var decoder: VideoDecoder? = null
    private var stream: Start? = null
    private var senderName = ""

    private val prefs = context.getSharedPreferences("altrove", Context.MODE_PRIVATE)
    private val _features = MutableStateFlow<Set<String>>(emptySet())
    private val _settings = MutableStateFlow<StreamSettings?>(null)
    private val _applying = MutableStateFlow(false)
    private val _touchEnabled = MutableStateFlow(prefs.getBoolean("touch", true))
    private var applyingJob: Job? = null

    val state: StateFlow<ReceiverState> = _state
    /** One line for the settings panel while streaming. */
    val stats: StateFlow<String> = _stats
    /** What the connected sender handles ([Features]); empty for a 0.1 sender. */
    val senderFeatures: StateFlow<Set<String>> = _features
    /** The sender's latest settings, for the panel; null until it sends some. */
    val settings: StateFlow<StreamSettings?> = _settings
    /** A [configure] was sent and the sender has not answered yet. */
    val applying: StateFlow<Boolean> = _applying
    /** The user's choice: touches on the video go to the PC. */
    val touchEnabled: StateFlow<Boolean> = _touchEnabled

    val deviceId: String by lazy {
        prefs.getString("id", null) ?: UUID.randomUUID().toString().replace("-", "").also { prefs.edit().putString("id", it).apply() }
    }

    fun setTouchEnabled(enabled: Boolean) {
        _touchEnabled.value = enabled
        prefs.edit().putBoolean("touch", enabled).apply()
    }

    /** True when a touch on the video should go to the PC rather than open the panel. */
    val sendsTouch get() = _touchEnabled.value && Features.TOUCH in _features.value

    fun sendTouch(touch: Touch) {
        if (sendsTouch) server?.send(touch)
    }

    /** Asks the sender for other settings; [applying] stays true until its new settings arrive. */
    fun configure(request: Configure) {
        if (Features.CONFIGURE !in _features.value) return
        _applying.value = true
        server?.send(request)
        applyingJob?.cancel()
        applyingJob = scope.launch {
            // An answer that never comes must not lock the panel.
            delay(20_000)
            _applying.value = false
        }
    }

    /** Ends the stream from this side: the PC stops and the receiver waits for the next one. */
    fun disconnect() {
        server?.disconnect()
    }

    fun start(display: DisplayInfo) {
        this.display = display
        if (server != null) return
        listen(AltroveProtocol.DEFAULT_PORT)
    }

    private fun listen(port: Int) {
        val server = ReceiverServer(port, this)
        this.server = server
        server.start()
    }

    fun stop() {
        advertiser.stop()
        statsJob?.cancel()
        server?.let {
            it.send(Bye("user"))
            try {
                it.stop(500)
            } catch (_: InterruptedException) {
            }
        }
        server = null
        synchronized(lock) {
            decoder?.release()
            decoder = null
            stream = null
        }
        _state.value = waiting()
    }

    fun attachSurface(surface: Surface) = synchronized(lock) {
        this.surface = surface
        startDecoder()
    }

    fun detachSurface() = synchronized(lock) {
        decoder?.release()
        decoder = null
        surface = null
    }

    private fun startDecoder() {
        val surface = surface ?: return
        val stream = stream ?: return
        decoder?.release()
        decoder = VideoDecoder(surface, stream.width, stream.height, stream.fps) { server?.send(Keyframe) }.also { it.start() }
    }

    // ---- Server events (network threads) ----

    override fun onServerStarted(port: Int) {
        advertiser.start(deviceName(), deviceId, port)
        _state.value = waiting(port)
    }

    override fun onServerError(error: Exception) {
        if (server?.port == AltroveProtocol.DEFAULT_PORT && error is java.net.BindException) {
            // Another app holds the usual port: take any free one, announced the same way.
            server = null
            listen(0)
            return
        }
        _state.value = ReceiverState.Failed("Impossibile mettersi in ascolto: ${error.message}")
    }

    override fun onSenderConnected(connection: WebSocket) {
        val hello = Hello(
            protocol = AltroveProtocol.VERSION,
            role = "receiver",
            id = deviceId,
            name = deviceName(),
            app = BuildConfig.VERSION_NAME,
            display = display,
            codecs = listOf("h264"),
            maxWidth = maxStreamSize().first,
            maxHeight = maxStreamSize().second,
        )
        connection.send(Messages.encode(hello))
    }

    override fun onControl(connection: WebSocket, message: ControlMessage) {
        when (message) {
            is Hello -> {
                if (message.protocol != AltroveProtocol.VERSION) {
                    connection.send(Messages.encode(Bye("protocol")))
                    connection.close()
                    return
                }
                senderName = message.name
                _features.value = message.features.orEmpty().toSet()
                _state.value = ReceiverState.Connected(senderName)
            }
            is Start -> {
                synchronized(lock) {
                    stream = message
                    startDecoder()
                }
                _state.value = ReceiverState.Streaming(senderName, message.width, message.height, message.fps)
                startStats()
            }
            is Stop -> {
                synchronized(lock) {
                    decoder?.release()
                    decoder = null
                    stream = null
                }
                _state.value = ReceiverState.Connected(senderName)
            }
            is Ping -> connection.send(Messages.encode(Pong(message.t)))
            is StreamSettings -> {
                _settings.value = message
                applyingJob?.cancel()
                _applying.value = false
            }
            is Bye -> connection.close()
            else -> Unit
        }
    }

    override fun onVideo(connection: WebSocket, packet: VideoPacket) {
        synchronized(lock) { decoder }?.submit(packet)
    }

    override fun onSenderDisconnected(connection: WebSocket) {
        statsJob?.cancel()
        applyingJob?.cancel()
        _applying.value = false
        _features.value = emptySet()
        _settings.value = null
        synchronized(lock) {
            decoder?.release()
            decoder = null
            stream = null
        }
        _stats.value = ""
        _state.value = waiting()
    }

    private fun startStats() {
        statsJob?.cancel()
        statsJob = scope.launch {
            while (isActive) {
                delay(1000)
                val decoder = synchronized(lock) { decoder } ?: continue
                val (frames, dropped, decodeMs) = decoder.takeStats()
                server?.send(Stats(frames.toDouble(), decodeMs, dropped))
                _stats.value = "$frames fps · decodifica ${"%.0f".format(decodeMs)} ms" + (decoder.decoderName?.let { " · $it" } ?: "")
            }
        }
    }

    private fun waiting(port: Int = server?.port ?: AltroveProtocol.DEFAULT_PORT) =
        ReceiverState.Waiting(deviceName(), localAddresses(), port)

    private fun deviceName(): String =
        Settings.Global.getString(context.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    /** The largest stream worth sending: what the screen shows, within what the decoder handles at 30 fps or more. */
    private fun maxStreamSize(): Pair<Int, Int> {
        val long = maxOf(display.width, display.height)
        val short = minOf(display.width, display.height)
        val sizes = listOf(3840 to 2160, 2560 to 1440, 1920 to 1080, 1280 to 720)
        val decodable = sizes.firstOrNull { (w, h) ->
            MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { info ->
                !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, true) } &&
                    info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities
                        ?.areSizeAndRateSupported(w, h, 30.0) == true
            }
        } ?: (1280 to 720)
        return minOf(decodable.first, long) to minOf(decodable.second, short)
    }

    private fun localAddresses(): List<String> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .map { it.hostAddress ?: "" }
            .filter { it.isNotEmpty() }
    } catch (_: Exception) {
        emptyList()
    }
}
