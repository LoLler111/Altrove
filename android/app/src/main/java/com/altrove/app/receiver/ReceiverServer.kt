package com.altrove.app.receiver

import android.util.Log
import com.altrove.app.protocol.Bye
import com.altrove.app.protocol.ByeReasons
import com.altrove.app.protocol.ControlMessage
import com.altrove.app.protocol.Messages
import com.altrove.app.protocol.VideoPacket
import org.java_websocket.WebSocket
import org.java_websocket.handshake.ClientHandshake
import org.java_websocket.server.WebSocketServer
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap

/**
 * The WebSocket server senders connect to. A new connection is pending until the controller lets it in
 * ([admit]: after its hello, and pairing when that is on); only then it takes the place of the sender
 * streaming now, which is told why (docs/PROTOCOLLO.md). A stranger on the network can no longer push
 * the current sender out just by connecting.
 */
class ReceiverServer(
    port: Int,
    private val listener: Listener,
) : WebSocketServer(InetSocketAddress(port)) {

    interface Listener {
        /** A connection opened: send it this receiver's hello. */
        fun onConnectionOpened(connection: WebSocket)

        /** A message from a connection not admitted yet (hello, pair, ping). */
        fun onPendingControl(connection: WebSocket, message: ControlMessage)

        fun onPendingClosed(connection: WebSocket)

        /** [connection] was admitted and is now the sender. */
        fun onSenderConnected(connection: WebSocket)
        fun onControl(connection: WebSocket, message: ControlMessage)
        fun onVideo(connection: WebSocket, packet: VideoPacket)
        fun onAudio(connection: WebSocket, data: ByteArray, offset: Int, length: Int)
        fun onSenderDisconnected(connection: WebSocket)
        fun onServerStarted(port: Int)
        fun onServerError(error: Exception)
    }

    @Volatile private var active: WebSocket? = null
    private val pending = ConcurrentHashMap.newKeySet<WebSocket>()

    init {
        isReuseAddr = true
        isTcpNoDelay = true
        connectionLostTimeout = 10
    }

    fun send(message: ControlMessage) {
        val connection = active ?: return
        if (connection.isOpen) connection.send(Messages.encode(message))
    }

    /** A message to one connection, admitted or not. */
    fun sendTo(connection: WebSocket, message: ControlMessage) {
        if (connection.isOpen) connection.send(Messages.encode(message))
    }

    /** A binary message to the sender (the microphone). */
    fun sendBinary(data: ByteArray) {
        val connection = active ?: return
        if (connection.isOpen) connection.send(data)
    }

    /** [connection] becomes the sender; the one before, if any, is told it was replaced. */
    @Synchronized
    fun admit(connection: WebSocket) {
        if (!pending.remove(connection) || !connection.isOpen) return
        val previous = active
        active = connection
        if (previous != null) {
            listener.onSenderDisconnected(previous)
            if (previous.isOpen) {
                previous.send(Messages.encode(Bye(ByeReasons.REPLACED)))
                previous.close()
            }
        }
        listener.onSenderConnected(connection)
    }

    /** Sends [reason] to a connection and closes it (a pending one refused, or the sender). */
    fun reject(connection: WebSocket, reason: String) {
        if (connection.isOpen) {
            connection.send(Messages.encode(Bye(reason)))
            connection.close()
        }
    }

    /** Says goodbye to the sender and closes its connection; the server keeps listening. */
    fun disconnect(reason: String = ByeReasons.USER) {
        val connection = active ?: return
        reject(connection, reason)
    }

    override fun onStart() = listener.onServerStarted(port)

    override fun onOpen(connection: WebSocket, handshake: ClientHandshake) {
        pending.add(connection)
        listener.onConnectionOpened(connection)
    }

    override fun onMessage(connection: WebSocket, message: String) {
        val parsed = Messages.parse(message) ?: return
        when {
            connection === active -> listener.onControl(connection, parsed)
            connection in pending -> listener.onPendingControl(connection, parsed)
        }
    }

    override fun onMessage(connection: WebSocket, message: ByteBuffer) {
        if (connection !== active) return
        if (message.remaining() > VideoPacket.HEADER_SIZE && message.get(message.position()).toInt() == VideoPacket.KIND_AUDIO) {
            val bytes = ByteArray(message.remaining())
            message.get(bytes)
            listener.onAudio(connection, bytes, VideoPacket.HEADER_SIZE, bytes.size - VideoPacket.HEADER_SIZE)
            return
        }
        VideoPacket.parse(message)?.let { listener.onVideo(connection, it) }
    }

    override fun onClose(connection: WebSocket, code: Int, reason: String?, remote: Boolean) {
        if (pending.remove(connection)) {
            listener.onPendingClosed(connection)
            return
        }
        synchronized(this) {
            if (connection !== active) return
            active = null
        }
        listener.onSenderDisconnected(connection)
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
