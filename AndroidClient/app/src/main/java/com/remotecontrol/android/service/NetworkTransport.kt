package com.remotecontrol.android.service

import com.remotecontrol.android.protocol.MessageType
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket

data class Packet(val type: MessageType, val payload: ByteArray)

/**
 * TCP 传输层：长度前缀(4B 大端) + 类型(1B) + payload
 * 同时提供 Server 监听 & Client 连接两种入口
 */
class NetworkTransport {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var socket: Socket? = null
    private var server: ServerSocket? = null
    private var input: InputStream? = null
    private var output: OutputStream? = null
    private var recvJob: Job? = null
    private var sendMutex = Mutex()

    private val _isConnected = MutableStateFlow(false)
    val isConnected: StateFlow<Boolean> = _isConnected

    private val _packets = MutableSharedFlow<Packet>(extraBufferCapacity = 64)
    val packets: Flow<Packet> = _packets

    private val _disconnectCallbacks = mutableListOf<() -> Unit>()

    fun onDisconnected(cb: () -> Unit) { _disconnectCallbacks += cb }

    // --- Server ---
    fun startServer(port: Int, onAccepted: (suspend () -> Unit)? = null) {
        scope.launch {
            runCatching { server?.close() }
            val srv = ServerSocket(port)
            server = srv
            val accepted = srv.accept()
            socket = accepted; input = accepted.getInputStream(); output = accepted.getOutputStream()
            _isConnected.value = true
            startReceiveLoop()
            onAccepted?.invoke()
        }
    }

    // --- Client ---
    fun connect(host: String, port: Int) {
        scope.launch {
            runCatching { socket?.close() }
            val s = Socket(host, port)
            s.tcpNoDelay = true
            socket = s; input = s.getInputStream(); output = s.getOutputStream()
            _isConnected.value = true
            startReceiveLoop()
        }
    }

    private fun startReceiveLoop() {
        recvJob?.cancel()
        recvJob = scope.launch {
            launch { pingLoop() }
            runCatching {
                val header = ByteArray(5)
                while (_isConnected.value) {
                    val r = readExact(header, 5)
                    if (r < 5) break
                    val len = ((header[0].toInt() and 0xFF) shl 24) or
                              ((header[1].toInt() and 0xFF) shl 16) or
                              ((header[2].toInt() and 0xFF) shl 8) or
                              (header[3].toInt() and 0xFF)
                    val typeByte = header[4]
                    val type = MessageType.from(typeByte) ?: continue
                    val payload = if (len > 0) {
                        val buf = ByteArray(len)
                        val got = readExact(buf, len)
                        if (got < len) break; buf
                    } else ByteArray(0)
                    _packets.emit(Packet(type, payload))
                }
            }
            disconnectInternal()
        }
    }

    private suspend fun pingLoop() {
        while (_isConnected.value) {
            delay(5000)
            runCatching { sendJson(MessageType.PING, JSONObject().put("ts", System.currentTimeMillis())) }
        }
    }

    private fun readExact(buf: ByteArray, count: Int): Int {
        var total = 0
        val ins = input ?: return -1
        while (total < count) {
            val n = ins.read(buf, total, count - total)
            if (n < 0) break
            total += n
        }
        return total
    }

    suspend fun sendBytes(type: MessageType, payload: ByteArray) {
        sendMutex.withLock {
            val os = output ?: return
            val header = ByteArray(5)
            val len = payload.size
            header[0] = ((len shr 24) and 0xFF).toByte()
            header[1] = ((len shr 16) and 0xFF).toByte()
            header[2] = ((len shr  8) and 0xFF).toByte()
            header[3] = ( len         and 0xFF).toByte()
            header[4] = type.value
            runCatching {
                os.write(header); os.write(payload); os.flush()
            }.onFailure { disconnectInternal() }
        }
    }

    suspend fun sendJson(type: MessageType, obj: JSONObject)
        = sendBytes(type, obj.toString().toByteArray(Charsets.UTF_8))

    suspend fun sendPacket(p: Packet) = sendBytes(p.type, p.payload)

    private fun disconnectInternal() {
        runCatching { _isConnected.compareAndSet(expect = true, update = false) }
        runCatching { socket?.close() }
        runCatching { server?.close() }
        socket = null; server = null; input = null; output = null
        _disconnectCallbacks.forEach { runCatching { it() } }
    }

    fun disconnect() = runCatching {
        scope.launch {
            runCatching { sendJson(MessageType.BYE, JSONObject().put("reason", "USER_CLOSE")) }
            delay(200)
            disconnectInternal()
        }
    }

    fun release() {
        disconnect()
        scope.cancel()
    }
}
