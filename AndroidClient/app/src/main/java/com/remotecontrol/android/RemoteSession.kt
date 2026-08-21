package com.remotecontrol.android

import android.app.Application
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.remotecontrol.android.protocol.Capabilities
import com.remotecontrol.android.protocol.HelloMessage
import com.remotecontrol.android.protocol.InputEvent
import com.remotecontrol.android.protocol.MessageType
import com.remotecontrol.android.protocol.Messages
import com.remotecontrol.android.protocol.OnlineDevice
import com.remotecontrol.android.protocol.Ports
import com.remotecontrol.android.protocol.Preferences
import com.remotecontrol.android.service.AccessibilityInjectionService
import com.remotecontrol.android.service.DeviceDiscovery
import com.remotecontrol.android.service.FileTransferService
import com.remotecontrol.android.service.NetworkTransport
import com.remotecontrol.android.service.ScreenCaptureService
import com.remotecontrol.android.service.VideoCodec
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.json.JSONObject
import androidx.compose.ui.graphics.ImageBitmap

enum class ControlDirection { IControlPeer, PeerControlsMe }

@OptIn(ExperimentalCoroutinesApi::class)
class RemoteSession(app: Application) : AndroidViewModel(app) {

    val discovery = DeviceDiscovery(app)
    val net = NetworkTransport()
    val files = FileTransferService(app, net, viewModelScope)

    private val _direction = MutableStateFlow(ControlDirection.IControlPeer)
    val direction: StateFlow<ControlDirection> = _direction

    private val _status = MutableStateFlow("未连接")
    val status: StateFlow<String> = _status

    private val _selected = MutableStateFlow<OnlineDevice?>(null)
    val selectedDevice: StateFlow<OnlineDevice?> = _selected

    val devices = discovery.devices.stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())
    val isConnected = net.isConnected.stateIn(viewModelScope, SharingStarted.Eagerly, false)

    private val _actAsServer = MutableStateFlow(false)
    val actAsServer: StateFlow<Boolean> = _actAsServer

    private val _listenPort = MutableStateFlow(Ports.TCP_DEFAULT)
    val listenPort: StateFlow<Int> = _listenPort

    private val _remoteFrame = MutableStateFlow<ImageBitmap?>(null)
    val remoteFrame: StateFlow<ImageBitmap?> = _remoteFrame

    // MediaProjection 数据缓存
    var pendingProjectionCode: Int? = null
    var pendingProjectionData: Intent? = null

    init {
        try {
            net.onDisconnected {
                viewModelScope.launch { _status.emit("已断开连接") }
                stopCapture()
            }
            viewModelScope.launch {
                net.packets.collect { pkt -> onPacket(pkt) }
            }
            ScreenCaptureService.sendJpeg = { bytes ->
                net.sendBytes(MessageType.VIDEO, bytes)
            }
        } catch (e: Exception) {
            android.util.Log.e("RemoteSession", "Init failed", e)
        }
    }

    fun selectDevice(d: OnlineDevice) { viewModelScope.launch { _selected.emit(d) } }
    fun setAsServer(on: Boolean) { viewModelScope.launch { _actAsServer.emit(on) } }
    fun setPort(p: Int) { viewModelScope.launch { _listenPort.emit(p) } }
    fun toggleDirection() {
        viewModelScope.launch {
            val now = if (_direction.value == ControlDirection.IControlPeer) ControlDirection.PeerControlsMe
                      else ControlDirection.IControlPeer
            _direction.emit(now)
            applyDirection()
        }
    }

    fun startDiscovery() = discovery.start()
    fun stopDiscovery()  = discovery.stop()

    fun startServer() {
        viewModelScope.launch {
            _status.emit("监听 ${_listenPort.value} 等待对端连接…")
            net.startServer(_listenPort.value) {
                handshake()
                _status.emit("对端已接入")
                applyDirection()
            }
        }
    }

    fun connectToSelected() {
        val d = _selected.value ?: return
        viewModelScope.launch {
            _status.emit("连接到 ${d.deviceName} (${d.ipAddress}:${d.listenPort})…")
            net.connect(d.ipAddress, d.listenPort)
            // 等待连接回调
            val start = System.currentTimeMillis()
            while (!net.isConnected.value && System.currentTimeMillis() - start < 3000)
                kotlinx.coroutines.delay(50)
            if (net.isConnected.value) {
                handshake()
                _status.emit("已连接到 ${d.deviceName}")
                applyDirection()
            } else {
                _status.emit("连接失败")
            }
        }
    }

    fun disconnect() { net.disconnect(); stopCapture(); }

    // MediaProjection 授权回调后调用：启动前台服务
    fun startCaptureWithPermission(resultCode: Int, data: Intent, width: Int, height: Int, fps: Int, quality: Int) {
        val ctx: Context = getApplication()
        val i = Intent(ctx, ScreenCaptureService::class.java)
            .putExtra(ScreenCaptureService.EXTRA_RESULT_CODE, resultCode)
            .putExtra(ScreenCaptureService.EXTRA_RESULT_DATA, data)
            .putExtra(ScreenCaptureService.EXTRA_WIDTH, width)
            .putExtra(ScreenCaptureService.EXTRA_HEIGHT, height)
            .putExtra(ScreenCaptureService.EXTRA_FPS, fps)
            .putExtra(ScreenCaptureService.EXTRA_QUALITY, quality)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            ctx.startForegroundService(i)
        else
            ctx.startService(i)
    }

    private fun stopCapture() {
        val ctx: Context = getApplication()
        ctx.stopService(Intent(ctx, ScreenCaptureService::class.java))
    }

    private suspend fun handshake() {
        val ctx: Context = getApplication()
        val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as android.view.WindowManager
        val metrics = android.util.DisplayMetrics()
        @Suppress("DEPRECATION") wm.defaultDisplay.getRealMetrics(metrics)
        val hello = HelloMessage(
            deviceId = discovery.localInfo.deviceId,
            deviceName = discovery.localInfo.deviceName,
            deviceType = "PHONE",
            capabilities = Capabilities(
                maxWidth = metrics.widthPixels, maxHeight = metrics.heightPixels,
                maxFps = 60, codecs = listOf("MJPEG")
            ),
            preferences = Preferences(width = 1280, height = 720, fps = 20, quality = 80, codec = "MJPEG"),
            listenPort = _listenPort.value
        )
        net.sendJson(MessageType.HELLO, hello.toJson())
    }

    private fun applyDirection() {
        if (!net.isConnected.value) return
        when (_direction.value) {
            ControlDirection.PeerControlsMe -> {
                _status.value = "会话中（PC 控制我：正在共享我的屏幕）"
                // 如果权限已就绪，启动共享；否则上层 UI 应该先请求权限
                val rc = pendingProjectionCode; val rd = pendingProjectionData
                if (rc != null && rd != null) {
                    startCaptureWithPermission(rc, rd, 1280, 720, 20, 80)
                } else {
                    _status.value = "请先授权屏幕录制（点击请求权限按钮）"
                }
            }
            ControlDirection.IControlPeer -> {
                _status.value = "会话中（我控制 PC：等待接收画面）"
                stopCapture()
            }
        }
    }

    private fun onPacket(pkt: com.remotecontrol.android.service.Packet) {
        when (pkt.type) {
            MessageType.PING -> viewModelScope.launch {
                net.sendJson(MessageType.PONG, JSONObject().put("ts", System.currentTimeMillis()))
            }
            MessageType.BYE -> disconnect()
            MessageType.VIDEO -> {
                if (_direction.value == ControlDirection.IControlPeer) {
                    val bmp = VideoCodec.decodeJpeg(pkt.payload)
                    bmp?.let { viewModelScope.launch(Dispatchers.Main.immediate) { _remoteFrame.emit(it) } }
                }
            }
            MessageType.INPUT -> {
                if (_direction.value == ControlDirection.PeerControlsMe) {
                    val srv = AccessibilityInjectionService.instance ?: run {
                        _status.value = "需要先开启无障碍服务才能接受 PC 触控指令"
                        return
                    }
                    val ctx: Context = getApplication()
                    val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as android.view.WindowManager
                    val m = android.util.DisplayMetrics()
                    @Suppress("DEPRECATION") wm.defaultDisplay.getRealMetrics(m)
                    val json = String(pkt.payload, Charsets.UTF_8)
                    runCatching { JSONObject(json) }
                        .onSuccess { j ->
                            val ev = Messages.InputEventFromJson(j)
                            srv.dispatcher.dispatch(ev, m.widthPixels, m.heightPixels)
                        }
                }
            }
            MessageType.FILE_CHUNK -> files.writeChunk(pkt.payload)
            else -> {}
        }
    }

    // 我控制 PC 时，把本地触屏 / 按键 发送出去
    fun sendInput(ev: InputEvent) {
        if (!net.isConnected.value || _direction.value != ControlDirection.IControlPeer) return
        viewModelScope.launch { net.sendJson(MessageType.INPUT, ev.toJson()) }
    }

    override fun onCleared() {
        disconnect()
        discovery.release()
        net.release()
        super.onCleared()
    }
}

