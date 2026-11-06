package io.github.loller111.altrove.receiver

import android.util.Log
import io.github.loller111.altrove.protocol.Bye
import io.github.loller111.altrove.protocol.ControlMessage
import io.github.loller111.altrove.protocol.Messages
import io.github.loller111.altrove.protocol.VideoPacket
import org.java_websocket.WebSocket
import org.java_websocket.handshake.ClientHandshake
import org.java_websocket.server.WebSocketServer
import java.net.InetSocketAddress
import java.nio.ByteBuffer

/**
 * The WebSocket server senders connect to. One sender at a time: a new one takes the place of the
 * old, which is told why (docs/PROTOCOLLO.md).
 */
class ReceiverServer(
    port: Int,
    private val listener: Listener,
) : WebSocketServer(InetSocketAddress(port)) {

    interface Listener {
        fun onSenderConnected(connection: WebSocket)
        fun onControl(connection: WebSocket, message: ControlMessage)
        fun onVideo(connection: WebSocket, packet: VideoPacket)
        fun onSenderDisconnected(connection: WebSocket)
        fun onServerStarted(port: Int)
        fun onServerError(error: Exception)
    }

    @Volatile private var active: WebSocket? = null

    init {
        isReuseAddr = true
        isTcpNoDelay = true
        connectionLostTimeout = 10
    }

    fun send(message: ControlMessage) {
        val connection = active ?: return
        if (connection.isOpen) connection.send(Messages.encode(message))
    }

    /** Says goodbye to the sender and closes its connection; the server keeps listening. */
    fun disconnect() {
        val connection = active ?: return
        if (connection.isOpen) {
            connection.send(Messages.encode(Bye("user")))
            connection.close()
        }
    }

    override fun onStart() = listener.onServerStarted(port)

    override fun onOpen(connection: WebSocket, handshake: ClientHandshake) {
        val previous = active
        active = connection
        if (previous != null && previous.isOpen) {
            previous.send(Messages.encode(Bye("replaced")))
            previous.close()
        }
        listener.onSenderConnected(connection)
    }

    override fun onMessage(connection: WebSocket, message: String) {
        if (connection !== active) return
        Messages.parse(message)?.let { listener.onControl(connection, it) }
    }

    override fun onMessage(connection: WebSocket, message: ByteBuffer) {
        if (connection !== active) return
        VideoPacket.parse(message)?.let { listener.onVideo(connection, it) }
    }

    override fun onClose(connection: WebSocket, code: Int, reason: String?, remote: Boolean) {
        if (connection === active) {
            active = null
            listener.onSenderDisconnected(connection)
        }
    }

    override fun onError(connection: WebSocket?, error: Exception) {
        if (connection == null) {
            // The server itself failed, e.g. the port is taken.
            Log.w("AltroveServer", "Server error", error)
            listener.onServerError(error)
        } else {
            Log.i("AltroveServer", "Connection error: ${error.message}")
        }
    }
}
