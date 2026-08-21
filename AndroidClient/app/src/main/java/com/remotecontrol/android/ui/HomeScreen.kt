package com.remotecontrol.android.ui

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.DesktopMac
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.getSystemService
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.remotecontrol.android.service.AccessibilityInjectionService
import com.remotecontrol.android.ControlDirection
import com.remotecontrol.android.R
import com.remotecontrol.android.RemoteSession
import com.remotecontrol.android.protocol.InputEvent
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    vm: RemoteSession = viewModel(),
    onNeedMediaProjection: (Intent, Int, (Int, Intent) -> Unit) -> Unit
) {
    val ctx = LocalContext.current
    val status by vm.status.collectAsStateWithLifecycle()
    val devices by vm.devices.collectAsStateWithLifecycle()
    val isConnected by vm.isConnected.collectAsStateWithLifecycle()
    val selected by vm.selectedDevice.collectAsStateWithLifecycle()
    val actAsServer by vm.actAsServer.collectAsStateWithLifecycle()
    val port by vm.listenPort.collectAsStateWithLifecycle()
    val direction by vm.direction.collectAsStateWithLifecycle()
    val remoteFrame by vm.remoteFrame.collectAsStateWithLifecycle()

    var requestPermMode by remember { mutableStateOf<ControlDirection?>(null) }

    // MediaProjection 授权 launcher
    val mpLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult()
    ) { ar ->
        val code = ar.resultCode; val data = ar.data
        if (data != null) {
            vm.pendingProjectionCode = code
            vm.pendingProjectionData = data
            if (isConnected && direction == ControlDirection.PeerControlsMe) {
                vm.startCaptureWithPermission(code, data, 1280, 720, 20, 80)
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(title = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.PhoneAndroid, contentDescription = null, Modifier.size(22.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("双向远程控制 · 手机端", fontWeight = FontWeight.Bold)
                }
            })
        }
    ) { pad ->
        Column(Modifier.padding(pad).padding(12.dp)) {
            // Status chip
            Card(shape = RoundedCornerShape(12.dp),
                 colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
                 modifier = Modifier.fillMaxWidth()) {
                Text("状态：$status", Modifier.padding(12.dp),
                     color = MaterialTheme.colorScheme.onPrimaryContainer,
                     fontSize = 13.sp)
            }
            Spacer(Modifier.height(10.dp))

            // Mode / Connection row
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("当前：" + if (direction == ControlDirection.IControlPeer) "我控制 PC" else "PC 控制我",
                         fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(4.dp))
                    Text(if (direction == ControlDirection.IControlPeer)
                             "查看 PC 屏幕，用本屏触摸控制 PC 鼠标/滚轮"
                         else "把手机屏幕共享给 PC，并响应 PC 发来的输入", fontSize = 12.sp, color = Color.Gray)
                }
                FilledTonalButton(onClick = { vm.toggleDirection() }) { Text("切换方向") }
            }
            Spacer(Modifier.height(8.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("作为服务器等待连接", fontSize = 13.sp); Spacer(Modifier.width(8.dp))
                Switch(checked = actAsServer, onCheckedChange = { vm.setAsServer(it) })
                Spacer(Modifier.width(16.dp))
                Text("端口 $port", fontSize = 12.sp, color = Color.Gray)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (!isConnected) {
                    if (actAsServer) {
                        Button(onClick = { vm.startServer() }) { Icon(Icons.Default.Cast, null); Spacer(Modifier.width(4.dp)); Text("开始监听") }
                    } else {
                        Button(onClick = { vm.connectToSelected() },
                               enabled = selected != null) {
                            Icon(Icons.Default.DesktopMac, null); Spacer(Modifier.width(4.dp))
                            Text(if (selected == null) "请选择设备" else "连接 ${selected!!.deviceName}")
                        }
                    }
                    Spacer(Modifier.width(8.dp))
                    // 请求权限按钮
                    OutlinedButton(onClick = {
                        if (direction == ControlDirection.PeerControlsMe) {
                            val mpm = ctx.getSystemService<MediaProjectionManager>()!!
                            mpLauncher.launch(mpm.createScreenCaptureIntent())
                        } else {
                            // 我控制对方时，需要无障碍服务通常不是必须（手机采集触摸自己来发），
                            // 但对方控制我时必须开启。这里无论哪种方向都给一个跳转入口。
                            ctx.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                        }
                    }) { Text("请求权限") }
                } else {
                    Button(onClick = { vm.disconnect() },
                           colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = Color(0xFFC62828))) {
                        Icon(Icons.Default.Stop, null); Spacer(Modifier.width(4.dp)); Text("断开")
                    }
                }
            }
            Spacer(Modifier.height(10.dp))

            // 未连接时显示设备列表；已连接时显示远程画面 / 提示
            if (!isConnected) {
                Text("📡 附近在线设备", fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                Spacer(Modifier.height(4.dp))
                if (devices.isEmpty()) {
                    Box(Modifier.fillMaxWidth().height(160.dp), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            CircularProgressIndicator()
                            Spacer(Modifier.height(8.dp))
                            Text("搜索中…请确保对端在同一局域网开启了 App", color = Color.Gray, fontSize = 12.sp)
                        }
                    }
                } else {
                    LazyColumn(modifier = Modifier.fillMaxWidth().height(240.dp),
                               verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        items(devices, key = { it.deviceId }) { d ->
                            Card(shape = RoundedCornerShape(10.dp),
                                 colors = CardDefaults.cardColors(
                                     containerColor = if (selected?.deviceId == d.deviceId)
                                         MaterialTheme.colorScheme.secondaryContainer
                                     else MaterialTheme.colorScheme.surfaceVariant),
                                 modifier = Modifier.fillMaxWidth().clickable { vm.selectDevice(d) }) {
                                Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Icon(if (d.deviceType == "PC") Icons.Default.DesktopMac
                                         else Icons.Default.PhoneAndroid, null, Modifier.size(26.dp))
                                    Spacer(Modifier.width(10.dp))
                                    Column {
                                        Text(d.deviceName, fontWeight = FontWeight.SemiBold)
                                        Text("${d.deviceType} · ${d.ipAddress}:${d.listenPort}",
                                             color = Color.Gray, fontSize = 11.sp)
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                // 已连接：远程画面（我控制PC）或共享提示（PC控制我）
                if (direction == ControlDirection.IControlPeer) {
                    Text("🖼️ PC 远程屏幕（点击/滑动=鼠标移动，双指=右键/滚轮）", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                    Spacer(Modifier.height(6.dp))
                    Card(shape = RoundedCornerShape(8.dp),
                         colors = CardDefaults.cardColors(containerColor = Color.Black),
                         modifier = Modifier.fillMaxWidth().aspectRatio(16f/9f)) {
                        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            val bmp = remoteFrame
                            if (bmp == null) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                    CircularProgressIndicator()
                                    Spacer(Modifier.height(6.dp))
                                    Text("等待画面…", color = Color.Gray, fontSize = 12.sp)
                                }
                            } else {
                                androidx.compose.foundation.Image(
                                    bitmap = bmp, contentDescription = "remote screen",
                                    modifier = Modifier.fillMaxSize().pointerInput(Unit) {
                                        detectTapGestures(
                                            onTap = { off ->
                                                val sx = (off.x / size.width).coerceIn(0f, 1f)
                                                val sy = (off.y / size.height).coerceIn(0f, 1f)
                                                vm.sendInput(InputEvent("MOUSE_MOVE", sx, sy))
                                                vm.sendInput(InputEvent("MOUSE_DOWN", sx, sy, button = "LEFT"))
                                                vm.sendInput(InputEvent("MOUSE_UP", sx, sy, button = "LEFT"))
                                            }
                                        )
                                    }.pointerInput(Unit) {
                                        detectDragGestures(
                                            onDragStart = { off ->
                                                val sx = (off.x / size.width).coerceIn(0f, 1f)
                                                val sy = (off.y / size.height).coerceIn(0f, 1f)
                                                vm.sendInput(InputEvent("MOUSE_DOWN", sx, sy, button = "LEFT"))
                                            },
                                            onDrag = { change, _ ->
                                                val p = change.position
                                                val sx = (p.x / size.width).coerceIn(0f, 1f)
                                                val sy = (p.y / size.height).coerceIn(0f, 1f)
                                                vm.sendInput(InputEvent("MOUSE_MOVE", sx, sy))
                                            },
                                            onDragEnd = {
                                                // 最后一次位置取 start 简化：发送 up 以当前中心近似（也可记录 lastPos）
                                                vm.sendInput(InputEvent("MOUSE_UP", 0f, 0f, button = "LEFT"))
                                            }
                                        )
                                    }
                                )
                            }
                        }
                    }
                    // 右键/滚轮辅助按钮
                    Row(Modifier.padding(top = 8.dp)) {
                        AssistChip(onClick = {
                            // 发送右键：先向中心发一次
                            vm.sendInput(InputEvent("MOUSE_DOWN", 0.5f, 0.5f, button = "RIGHT"))
                            vm.sendInput(InputEvent("MOUSE_UP", 0.5f, 0.5f, button = "RIGHT"))
                        }, label = { Text("右键") })
                        Spacer(Modifier.width(8.dp))
                        AssistChip(onClick = {
                            vm.sendInput(InputEvent("MOUSE_WHEEL", 0.5f, 0.5f, delta = 400, axis = "V"))
                        }, label = { Text("滚轮上") })
                        Spacer(Modifier.width(8.dp))
                        AssistChip(onClick = {
                            vm.sendInput(InputEvent("MOUSE_WHEEL", 0.5f, 0.5f, delta = -400, axis = "V"))
                        }, label = { Text("滚轮下") })
                    }
                } else {
                    // PeerControlsMe
                    val a11y = AccessibilityInjectionService.isEnabled
                    val perm = vm.pendingProjectionData != null
                    Card(shape = RoundedCornerShape(10.dp),
                         colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.tertiaryContainer),
                         modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(14.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Default.Share, null)
                                Spacer(Modifier.width(6.dp))
                                Text("正在共享屏幕", fontWeight = FontWeight.Bold)
                            }
                            Spacer(Modifier.height(8.dp))
                            Text("屏幕录制权限：${if (perm) "✅ 已获取" else "❌ 未授权"}", fontSize = 13.sp)
                            Text("无障碍服务：${if (a11y) "✅ 已开启（PC 可模拟触摸）" else "⚠️ 未开启，PC 无法控制此设备输入"}", fontSize = 13.sp)
                            Spacer(Modifier.height(8.dp))
                            if (!perm) {
                                OutlinedButton(onClick = {
                                    val mpm = ctx.getSystemService<MediaProjectionManager>()!!
                                    mpLauncher.launch(mpm.createScreenCaptureIntent())
                                }) { Text("开启屏幕录制授权") }
                            }
                            if (!a11y) {
                                OutlinedButton(onClick = {
                                    ctx.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
                                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                                }) { Text("跳转开启无障碍服务") }
                            }
                        }
                    }
                }
            }

            Spacer(Modifier.height(16.dp))
            Divider()
            Text("提示：两端必须在同一 Wi-Fi / 局域网。首版不支持外网穿透。",
                 color = Color.Gray, fontSize = 11.sp, modifier = Modifier.padding(top = 8.dp))
        }
    }
}
