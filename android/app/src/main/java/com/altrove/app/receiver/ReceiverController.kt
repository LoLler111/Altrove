package com.altrove.app.receiver

import android.content.Context
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.provider.Settings
import android.util.Log
import android.view.Surface
import com.altrove.app.BuildConfig
import com.altrove.app.protocol.AltroveProtocol
import com.altrove.app.protocol.AudioStart
import com.altrove.app.protocol.AudioStop
import com.altrove.app.protocol.MicStop
import com.altrove.app.protocol.Bye
import com.altrove.app.protocol.ByeReasons
import com.altrove.app.protocol.Key
import com.altrove.app.protocol.PairRequest
import com.altrove.app.protocol.Paired
import com.altrove.app.protocol.PinRequired
import com.altrove.app.protocol.Configure
import com.altrove.app.protocol.Features
import com.altrove.app.protocol.StreamSettings
import com.altrove.app.protocol.Touch
import com.altrove.app.protocol.ControlMessage
import com.altrove.app.protocol.DisplayInfo
import com.altrove.app.protocol.Hello
import com.altrove.app.protocol.Keyframe
import com.altrove.app.protocol.Messages
import com.altrove.app.protocol.Mouse
import com.altrove.app.protocol.Ping
import com.altrove.app.protocol.Pong
import com.altrove.app.protocol.Start
import com.altrove.app.protocol.Stats
import com.altrove.app.protocol.Stop
import com.altrove.app.protocol.VideoPacket
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

/** What fingers on the video do. */
enum class InputMode { TOUCH, TRACKPAD, OFF }

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
    private val _inputMode = MutableStateFlow(
        // 0.2 kept only "touch on/off".
        prefs.getString("input", null)?.let { saved -> InputMode.entries.firstOrNull { it.name == saved } }
            ?: if (prefs.getBoolean("touch", true)) InputMode.TOUCH else InputMode.OFF,
    )
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
    /** The user's choice: what fingers on the video do to the PC. */
    val inputMode: StateFlow<InputMode> = _inputMode

    val deviceId: String by lazy {
        prefs.getString("id", null) ?: UUID.randomUUID().toString().replace("-", "").also { prefs.edit().putString("id", it).apply() }
    }

    fun setInputMode(mode: InputMode) {
        _inputMode.value = mode
        prefs.edit().putString("input", mode.name).apply()
    }

    /** Fingers on the video are touches on the PC. */
    val sendsTouch get() = _inputMode.value == InputMode.TOUCH && Features.TOUCH in _features.value

    /** Fingers on the video drive the PC's pointer like a trackpad. */
    val sendsMouse get() = _inputMode.value == InputMode.TRACKPAD && Features.MOUSE in _features.value

    fun sendTouch(touch: Touch) {
        if (sendsTouch) server?.send(touch)
    }

    fun sendMouse(mouse: Mouse) {
        if (sendsMouse) server?.send(mouse)
    }

    /** The PC types what this device's keyboard sends (feature [Features.KEYBOARD]). */
    val acceptsKeys get() = Features.KEYBOARD in _features.value

    fun sendKey(key: Key) {
        if (acceptsKeys) server?.send(key)
    }

    // ---- Pairing: a PC types the PIN shown here once, then connects with its token ----

    /** A PC waiting for the PIN on this screen. */
    data class PinRequest(val senderName: String, val pin: String, val wrong: Boolean)

    private val pairing = Pairing(Pairing.PreferencesStore(context.getSharedPreferences("altrove_paired", Context.MODE_PRIVATE)))
    private val pendingHellos = java.util.concurrent.ConcurrentHashMap<WebSocket, Hello>()
    private val _pinRequest = MutableStateFlow<PinRequest?>(null)
    private val _requirePin = MutableStateFlow(prefs.getBoolean("requirePin", true))
    private val _trustedPcs = MutableStateFlow(pairing.trustedNames)
    private val _notice = MutableStateFlow<String?>(null)
    @Volatile private var pinConnection: WebSocket? = null
    private var pinTimeout: Job? = null

    /** The PIN to show, while a new PC waits for it. */
    val pinRequest: StateFlow<PinRequest?> = _pinRequest
    /** The user's choice: a new PC must type a PIN shown here. */
    val requirePin: StateFlow<Boolean> = _requirePin
    /** The PCs that connect without a PIN. */
    val trustedPcs: StateFlow<List<String>> = _trustedPcs
    /** A line for the waiting screen: why the last PC was turned away. */
    val notice: StateFlow<String?> = _notice

    fun setRequirePin(on: Boolean) {
        _requirePin.value = on
        prefs.edit().putBoolean("requirePin", on).apply()
    }

    fun forgetTrustedPcs() {
        pairing.forgetAll()
        _trustedPcs.value = pairing.trustedNames
    }

    /** "Rifiuta" under the PIN: that PC is sent away. */
    fun refusePin() {
        val connection = pinConnection ?: return
        clearPin()
        pendingHellos[connection]?.let { pairing.cancel(it.id) }
        server?.reject(connection, ByeReasons.PAIRING)
    }

    private fun clearPin() {
        pinTimeout?.cancel()
        pinConnection = null
        _pinRequest.value = null
    }

    override fun onConnectionOpened(connection: WebSocket) {
        val hello = Hello(
            protocol = AltroveProtocol.VERSION,
            role = "receiver",
            id = deviceId,
            name = deviceName(),
            app = BuildConfig.VERSION_NAME,
            display = shownDisplay,
            codecs = listOf("h264"),
            maxWidth = maxStreamSize().first,
            maxHeight = maxStreamSize().second,
            features = if (_requirePin.value) listOf(Features.PAIRING) else null,
            audioCodecs = audioCodecs,
        )
        connection.send(Messages.encode(hello))
    }

    override fun onPendingControl(connection: WebSocket, message: ControlMessage) {
        val server = server ?: return
        when (message) {
            is Hello -> {
                if (message.protocol != AltroveProtocol.VERSION) {
                    server.reject(connection, ByeReasons.PROTOCOL)
                    return
                }
                pendingHellos[connection] = message
                when {
                    !_requirePin.value -> server.admit(connection)
                    !message.supports(Features.PAIRING) -> {
                        _notice.value = "«${message.name}» ha una versione di Altrove che non sa usare il PIN: aggiornala sul PC, " +
                            "oppure qui sotto spegni «Chiedi un PIN ai PC nuovi»."
                        server.reject(connection, ByeReasons.UPDATE)
                    }
                    // Otherwise the PC sends pair next.
                }
            }
            is PairRequest -> {
                val hello = pendingHellos[connection] ?: return
                when (val answer = pairing.onPair(hello.id, hello.name, message)) {
                    is Pairing.Answer.Accepted -> {
                        if (pinConnection === connection) clearPin()
                        _trustedPcs.value = pairing.trustedNames
                        server.sendTo(connection, Paired(answer.token))
                        server.admit(connection)
                    }
                    is Pairing.Answer.ShowPin -> {
                        // One PIN on screen at a time: a second PC asking takes the place of the first.
                        pinConnection?.takeIf { it !== connection }?.let { server.reject(it, ByeReasons.PAIRING) }
                        pinConnection = connection
                        _pinRequest.value = PinRequest(hello.name, answer.pin, answer.wrong)
                        server.sendTo(connection, PinRequired(answer.wrong))
                        pinTimeout?.cancel()
                        pinTimeout = scope.launch {
                            delay(PIN_TIMEOUT_MS)
                            if (pinConnection === connection) refusePin()
                        }
                    }
                    Pairing.Answer.Refused -> {
                        clearPin()
                        _notice.value = "Troppi PIN sbagliati da «${hello.name}»: collegamento rifiutato."
                        server.reject(connection, ByeReasons.PAIRING)
                    }
                }
            }
            is Ping -> server.sendTo(connection, Pong(message.t))
            is Bye -> connection.close()
            else -> Unit
        }
    }

    override fun onPendingClosed(connection: WebSocket) {
        pendingHellos.remove(connection)?.let { pairing.cancel(it.id) }
        if (pinConnection === connection) clearPin()
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
        releaseAudio()
        releaseMic(tellSender = true)
        advertiser.stop()
        statsJob?.cancel()
        clearPin()
        pendingHellos.clear()
        server?.let {
            // Background or screen off, not the user's choice: the PC tries again for a while.
            it.send(Bye(ByeReasons.AWAY))
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

    /** The picture goes to [surface] from now on: the tablet's own, or the TV's while one shows it. */
    fun attachSurface(surface: Surface) = synchronized(lock) {
        this.surface = surface
        startDecoder()
    }

    /** [surface] is going away. Ignored when another one already took its place (moving to or from the TV). */
    fun detachSurface(surface: Surface) = synchronized(lock) {
        if (this.surface !== surface) return
        decoder?.release()
        decoder = null
        this.surface = null
    }

    // ---- A TV linked through Android's own casting (Miracast: Smart View, Trasmetti) ----

    private val _tvName = MutableStateFlow<String?>(null)
    private val _tvShowing = MutableStateFlow(false)
    private val _useTv = MutableStateFlow(prefs.getBoolean("tv", true))
    @Volatile private var tvDisplay: DisplayInfo? = null

    /** The name of the wireless screen Android offers for a second picture; null when none is linked. */
    val tvName: StateFlow<String?> = _tvName
    /** The PC's picture is on the TV now, and this screen is left for the panel and the fingers. */
    val tvShowing: StateFlow<Boolean> = _tvShowing
    /** The user's choice: the PC on the TV when one is linked, or on this screen as usual. */
    val useTv: StateFlow<Boolean> = _useTv

    fun setUseTv(on: Boolean) {
        _useTv.value = on
        prefs.edit().putBoolean("tv", on).apply()
    }

    fun onTvDisplay(name: String?, info: DisplayInfo?) {
        tvDisplay = info
        _tvName.value = name
    }

    fun onTvShowing(showing: Boolean) {
        _tvShowing.value = showing
        adjustFpsForTv()
    }

    // The fps chosen before the picture went to the TV, to put back when it comes back here. Kept on disk: the PC
    // saves the 30 as its choice, and an app closed while on the TV must still put the user's choice back next time.
    private var fpsBeforeTv: Int?
        get() = prefs.getInt("fpsBeforeTv", 0).takeIf { it > 0 }
        set(value) = prefs.edit().putInt("fpsBeforeTv", value ?: 0).apply()

    // When the last start arrived: right after it the PC may still be starting sound and microphone, and a 0.5.0 PC
    // dropped a configure sent then without answering. The TV's request waits a little.
    @Volatile private var lastStartAt = 0L
    private var tvRetry: Job? = null

    /**
     * Android's Miracast sends at most 30 fps to the TV: more from the PC only costs this device a decoder twice as
     * busy and Wi-Fi time, when its single radio also carries the Miracast link (Wi-Fi Direct, not through the router).
     * So while the picture is on the TV the PC is asked for 30, once; a choice made afterwards in the panel stays.
     */
    @Synchronized
    private fun adjustFpsForTv() {
        val current = _settings.value ?: return
        if (Features.CONFIGURE !in _features.value || _applying.value) return
        val sinceStart = android.os.SystemClock.elapsedRealtime() - lastStartAt
        if (sinceStart < STREAM_SETTLE_MS) {
            tvRetry?.cancel()
            tvRetry = scope.launch {
                delay(STREAM_SETTLE_MS - sinceStart)
                adjustFpsForTv()
            }
            return
        }
        if (_tvShowing.value) {
            if (fpsBeforeTv == null && current.fps > TV_FPS && TV_FPS in current.choices.fps) {
                fpsBeforeTv = current.fps
                configure(Configure(fps = TV_FPS))
            }
        } else {
            val before = fpsBeforeTv ?: return
            fpsBeforeTv = null
            if (current.fps == TV_FPS && before in current.choices.fps) configure(Configure(fps = before))
        }
    }

    /** The screen the PC should size the stream for: the TV when the picture will go there. */
    private val shownDisplay get() = tvDisplay?.takeIf { _useTv.value } ?: display

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
        val hello = pendingHellos.remove(connection)
        senderName = hello?.name ?: ""
        _features.value = hello?.features.orEmpty().toSet()
        _notice.value = null
        _state.value = ReceiverState.Connected(senderName)
    }

    override fun onControl(connection: WebSocket, message: ControlMessage) {
        when (message) {
            is Start -> {
                lastStartAt = android.os.SystemClock.elapsedRealtime()
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
            is AudioStart -> synchronized(lock) {
                player?.release()
                player = runCatching { AudioPlayer(message) }.onFailure { Log.w("AltroveAudio", "No audio", it) }.getOrNull()
            }
            is AudioStop -> synchronized(lock) {
                player?.release()
                player = null
            }
            is StreamSettings -> {
                _settings.value = message
                applyingJob?.cancel()
                _applying.value = false
                updateMic()
                adjustFpsForTv() // the PC connected, or answered, while the picture is on the TV
            }
            is Bye -> connection.close()
            else -> Unit
        }
    }

    private val receivedBytes = java.util.concurrent.atomic.AtomicLong()
    private var player: AudioPlayer? = null
    private val audioCodecs by lazy { AudioPlayer.supportedCodecs() }

    override fun onAudio(connection: WebSocket, data: ByteArray, offset: Int, length: Int) {
        synchronized(lock) { player }?.submit(data, offset, length)
    }

    private fun releaseAudio() = synchronized(lock) {
        player?.release()
        player = null
    }

    // ---- This device's microphone, while the PC uses it as "Microfono Altrove" ----

    private var mic: MicCapture? = null
    private val _micPermissionNeeded = MutableStateFlow(false)

    /** The PC turned the microphone on and Android has not granted RECORD_AUDIO yet: the activity asks. */
    val micPermissionNeeded: StateFlow<Boolean> = _micPermissionNeeded

    private fun hasMicPermission() =
        context.checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED

    /** Records exactly while the PC's settings say the microphone is on. */
    private fun updateMic() = synchronized(lock) {
        val wanted = _settings.value?.mic == true
        if (wanted && mic == null) {
            if (!hasMicPermission()) {
                _micPermissionNeeded.value = true
                return
            }
            val capture = runCatching { MicCapture { packet -> server?.sendBinary(packet) } }
                .onFailure { Log.w("AltroveMic", "No microphone", it) }.getOrNull() ?: return
            server?.send(capture.format)
            capture.start()
            mic = capture
        } else if (!wanted && mic != null) {
            releaseMic(tellSender = true)
        }
    }

    private fun releaseMic(tellSender: Boolean) = synchronized(lock) {
        mic?.release()
        if (mic != null && tellSender) server?.send(MicStop)
        mic = null
    }

    /** The answer to the permission request: start, or tell the PC the microphone stays off. */
    fun onMicPermission(granted: Boolean) {
        _micPermissionNeeded.value = false
        if (granted) updateMic() else configure(Configure(mic = false))
    }

    override fun onVideo(connection: WebSocket, packet: VideoPacket) {
        receivedBytes.addAndGet(packet.length.toLong())
        synchronized(lock) { decoder }?.submit(packet)
    }

    override fun onSenderDisconnected(connection: WebSocket) {
        releaseAudio()
        releaseMic(tellSender = false)
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
                val mbps = receivedBytes.getAndSet(0) * 8 / 1_000_000.0
                val sound = synchronized(lock) { player }?.let { " · audio" } ?: ""
                _stats.value = "$frames fps · ${"%.1f".format(mbps)} Mbit/s$sound · decodifica ${"%.0f".format(decodeMs)} ms" +
                    (decoder.decoderName?.let { " · $it" } ?: "")
            }
        }
    }

    private companion object {
        /** A PIN left on screen this long without the right answer is withdrawn. */
        const val PIN_TIMEOUT_MS = 120_000L

        /** What Android's Miracast sends to a TV at most. */
        const val TV_FPS = 30

        /** How long after a start the automatic request for the TV waits. */
        const val STREAM_SETTLE_MS = 3_000L
    }

    private fun waiting(port: Int = server?.port ?: AltroveProtocol.DEFAULT_PORT) =
        ReceiverState.Waiting(deviceName(), localAddresses(), port)

    private fun deviceName(): String =
        Settings.Global.getString(context.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    /** The largest stream worth sending: what the screen shows, within what the decoder handles at 30 fps or more. */
    /**
     * The largest stream the H.264 decoder handles at 30 fps, in landscape: what the PC may send, even bigger
     * than this screen (in Estendi a bigger virtual monitor gives more room). The PC itself recommends the
     * resolution that fits the screen. Sizes in this screen's shape come first, so its own is never refused.
     */
    private fun maxStreamSize(): Pair<Int, Int> {
        val screen = shownDisplay
        val long = maxOf(screen.width, screen.height)
        val short = minOf(screen.width, screen.height)
        val aspect = long.toDouble() / short
        val ownShape = listOf(2160, 1600, 1440, 1200, 1080, 900, 720).map { h -> ((h * aspect).toInt() and 1.inv()) to h }
        val sizes = (ownShape + listOf(3840 to 2160, 2560 to 1440, 1920 to 1080, 1280 to 720)).sortedByDescending { it.first * it.second }
        val decoders = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, true) }
        }
        fun decodable(size: Pair<Int, Int>) = decoders.any { info ->
            info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities
                ?.areSizeAndRateSupported(size.first, size.second, 30.0) == true
        }
        val supported = sizes.filter(::decodable)
        if (supported.isEmpty()) return minOf(1280, long) to minOf(720, short)
        // The PC reads these as a box: report the biggest box whose corner the decoder still handles.
        val box = supported.maxOf { it.first } to supported.maxOf { it.second }
        return if (decodable(box)) box else supported.first()
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
