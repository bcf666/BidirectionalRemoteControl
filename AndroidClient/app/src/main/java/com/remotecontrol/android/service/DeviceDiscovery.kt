package com.remotecontrol.android.service

import android.content.Context
import android.net.wifi.WifiManager
import com.remotecontrol.android.protocol.DiscoverPacket
import com.remotecontrol.android.protocol.OnlineDevice
import com.remotecontrol.android.protocol.Ports
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.InetAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.nio.charset.StandardCharsets

/**
 * 局域网 UDP 广播发现：
 * - 每 3 秒在 23000 端口广播自己的设备信息
 * - 监听同端口，维护在线设备列表 (StateFlow)
 */
class DeviceDiscovery(private val ctx: Context) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null

    val localInfo = DiscoverPacket(
        deviceId = java.util.UUID.randomUUID().toString(),
        deviceName = android.os.Build.MODEL.ifBlank { "Android-" + android.os.Build.BRAND },
        deviceType = "PHONE",
        listenPort = Ports.TCP_DEFAULT
    )

    private val _devices = MutableStateFlow<List<OnlineDevice>>(emptyList())
    val devices: StateFlow<List<OnlineDevice>> = _devices

    fun start() {
        if (job != null) return
        job = scope.launch {
            val broadcastSocket = MulticastSocket(Ports.UDP_DISCOVER)
            broadcastSocket.soTimeout = 2000
            broadcastSocket.broadcast = true
            runCatching { broadcastSocket.reuseAddress = true }
            val broadcastAddr = InetAddress.getByName("255.255.255.255")

            // 广播循环
            val sendJob = launch {
                while (true) {
                    runCatching {
                        val data = localInfo.toJson().toString().toByteArray(StandardCharsets.UTF_8)
                        val p = DatagramPacket(data, data.size, broadcastAddr, Ports.UDP_DISCOVER)
                        broadcastSocket.send(p)
                        // 单网卡单播一次全部可用子网：如果有 VPN/多网卡，再把各网段也广播一次
                        runCatching { broadcastAllInterfaces(data) }
                    }
                    delay(3000)
                }
            }

            // 接收循环
            val recvJob = launch {
                val buf = ByteArray(2048)
                val pending = mutableMapOf<String, OnlineDevice>()
                while (true) {
                    val pkt = DatagramPacket(buf, buf.size)
                    val ok = runCatching { broadcastSocket.receive(pkt) }
                    if (ok.isSuccess) {
                        val str = String(pkt.data, 0, pkt.length, StandardCharsets.UTF_8)
                        runCatching { JSONObject(str) }
                            .onSuccess { j ->
                                if (j.optString("type").equals("DISCOVER", ignoreCase = true)) {
                                    val d = DiscoverPacket.fromJson(j)
                                    if (d.deviceId == localInfo.deviceId) return@onSuccess
                                    val ip = pkt.address.hostAddress ?: return@onSuccess
                                    pending[d.deviceId] = OnlineDevice(
                                        deviceId = d.deviceId, deviceName = d.deviceName,
                                        deviceType = d.deviceType, ipAddress = ip,
                                        listenPort = d.listenPort,
                                        lastSeenMs = System.currentTimeMillis()
                                    )
                                }
                            }
                    }
                    // 定期剔旧 + 更新 UI State
                    val cutoff = System.currentTimeMillis() - 15000L
                    val live = pending.values.filter { it.lastSeenMs > cutoff }
                    pending.entries.removeAll { it.value.lastSeenMs <= cutoff }
                    _devices.emit(live.sortedByDescending { it.lastSeenMs })
                }
            }

            sendJob.join(); recvJob.join()
        }
    }

    private fun broadcastAllInterfaces(data: ByteArray) {
        val en = NetworkInterface.getNetworkInterfaces()
        while (en.hasMoreElements()) {
            val ni = en.nextElement()
            if (ni.isLoopback || !ni.isUp) continue
            for (ia in ni.interfaceAddresses) {
                val broadcast = ia.broadcast ?: continue
                runCatching {
                    val s = java.net.DatagramSocket()
                    s.broadcast = true
                    val p = DatagramPacket(data, data.size, broadcast, Ports.UDP_DISCOVER)
                    s.send(p); s.close()
                }
            }
        }
    }

    fun stop() {
        job?.cancel(); job = null
    }

    fun release() { stop(); scope.cancel() }
}
