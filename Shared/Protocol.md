# Windows ↔ Android 双向远程控制协议 v1.0

## 1. 概述
- 传输层：TCP（控制/视频/文件复用同一条 TCP 连接；首版单连接，后续可分通道）
- 封包格式：长度前缀 + 类型 + Payload
- 编码：Payload 为 JSON（首版），后续可切换为 Protobuf
- 设备发现：UDP 广播（端口 23000）

---

## 2. 包格式 (TCP)

```
+-----------------+----------------+-------------------------+
| 4 字节 (uint32) | 1 字节 (byte)  | N 字节                  |
| payload 长度 L  | 消息类型 T     | JSON / 二进制 Payload   |
+-----------------+----------------+-------------------------+
```

- L：仅 Payload 的字节数（不含 L 和 T），**大端序**
- T：消息类型（见下表 / enums.json）

---

## 3. 消息类型（T）

| 值 | 枚举名 | 方向 | Payload 说明 |
|---|---|---|---|
| 0x01 | HELLO | 双向 | 握手：设备信息、能力协商 |
| 0x02 | AUTH | 双向 | 鉴权：配对码校验 |
| 0x03 | AUTH_ACK | 响应方 | 鉴权结果 |
| 0x10 | VIDEO | 发送方→接收方 | 视频帧（JPEG，二进制） |
| 0x11 | INPUT | 控制方→被控方 | 输入事件（JSON） |
| 0x20 | FILE_META | 发送方→接收方 | 文件元数据（名称/大小等） |
| 0x21 | FILE_ACK | 接收方→发送方 | 接收/拒绝文件 |
| 0x22 | FILE_CHUNK | 发送方→接收方 | 文件分片（64KB） |
| 0x23 | FILE_DONE | 发送方→接收方 | 发送完成（含可选校验） |
| 0x30 | CTRL | 双向 | 控制命令（改画质/切方向/暂停镜像） |
| 0x31 | CTRL_ACK | 响应方 | 控制命令应答 |
| 0xF0 | PING | 双向 | 心跳，Payload = 时间戳字符串 |
| 0xF1 | PONG | 双向 | 心跳响应 |
| 0xFF | BYE | 双向 | 断开连接，可选 reason |

---

## 4. Payload 详解

### 4.1 HELLO (0x01)
```json
{
  "deviceId": "uuid-string",
  "deviceName": "My-PC",
  "deviceType": "PC",           // "PC" | "PHONE"
  "protocolVersion": 1,
  "capabilities": {
    "maxWidth": 1920,
    "maxHeight": 1080,
    "maxFps": 30,
    "codecs": ["MJPEG", "H264"],
    "supportsFileTransfer": true,
    "supportsControl": true
  },
  "preferences": {
    "width": 1280,
    "height": 720,
    "fps": 20,
    "codec": "MJPEG",
    "quality": 80                // JPEG 质量 1-100
  },
  "listenPort": 23001           // 本机 TCP 监听端口（若作为 server）
}
```

### 4.2 AUTH (0x02) / AUTH_ACK (0x03)
```json
// AUTH
{ "code": "123456" }

// AUTH_ACK
{ "ok": true, "reason": "" }
```

### 4.3 VIDEO (0x10)
Payload = **二进制 JPEG 数据**（不含 JSON，直接 L = JPEG 长度）。
- 帧头信息（分辨率、时间戳）建议首版省略，直接 JPEG 解码；如需可在后续版本在 JPEG 前加 8 字节头。

### 4.4 INPUT (0x11)
```json
// 公共字段
{
  "type": "MOUSE_MOVE",     // 见 4.4.1
  "ts": 1710000000000,      // 可选，发送端时间戳 ms
  ...
}
```

#### 4.4.1 输入事件类型

**MOUSE_MOVE** — 坐标采用 **归一化 0~1 浮点**（避免两端分辨率差异）：
```json
{ "type": "MOUSE_MOVE", "x": 0.5, "y": 0.3 }
```

**MOUSE_DOWN / MOUSE_UP**
```json
{ "type": "MOUSE_DOWN", "x": 0.5, "y": 0.3, "button": "LEFT" }  // LEFT|RIGHT|MIDDLE|X1|X2
{ "type": "MOUSE_UP",   "x": 0.5, "y": 0.3, "button": "LEFT" }
```

**MOUSE_WHEEL**
```json
{ "type": "MOUSE_WHEEL", "x": 0.5, "y": 0.3, "delta": 120, "axis": "V" }  // axis: V=垂直 H=水平
```

**KEY_DOWN / KEY_UP**
```json
{ "type": "KEY_DOWN", "key": "A" }                         // 单字符 / 数字
{ "type": "KEY_DOWN", "vk": 0x41 }                         // 或直接 Virtual Key code
{ "type": "KEY_DOWN", "key": "Enter" }                     // 命名键: Enter Space Backspace Tab Esc Ctrl Shift Alt ArrowLeft ...
```

**TOUCH_DOWN / TOUCH_MOVE / TOUCH_UP** — 被控端为手机时使用：
```json
{ "type": "TOUCH_DOWN", "x": 0.5, "y": 0.3, "pointerId": 0 }
{ "type": "TOUCH_MOVE", "x": 0.52, "y": 0.31, "pointerId": 0 }
{ "type": "TOUCH_UP",   "x": 0.52, "y": 0.31, "pointerId": 0 }
```

**KEY_TEXT** — 向对端输入一串文本（中文、字符批量）：
```json
{ "type": "KEY_TEXT", "text": "你好，世界！" }
```

### 4.5 FILE_META (0x20) / FILE_ACK (0x21)
```json
// FILE_META
{
  "fileId": "uuid-xxxx",
  "name": "report.pdf",
  "size": 2097152,
  "chunkSize": 65536,
  "lastModified": 1710000000,
  "mimeType": "application/pdf"
}

// FILE_ACK
{ "fileId": "uuid-xxxx", "accept": true, "savePath": "/sdcard/Download/report.pdf" }
```

### 4.6 FILE_CHUNK (0x22)
Payload 前 36 字节为 fileId (UTF-8 固定 36 字符 UUID) + 4 字节大端序 offset，剩余为二进制块数据。
简化版：也可改为 JSON 描述 + Base64，但大文件会增加 33% 体积，首版推荐二进制混合。

**简化实现**：
```
[fileId 36 bytes][offset 4 bytes BE][data (chunkSize bytes)]
```

### 4.7 FILE_DONE (0x23)
```json
{ "fileId": "uuid-xxxx", "sha256": "<hex>" }   // sha256 可省略用于校验
```

### 4.8 CTRL (0x30) / CTRL_ACK (0x31)
```json
// 常见 commands
// "setQuality" — 改画质/分辨率
// "pauseMirror" / "resumeMirror" — 暂停/恢复屏幕发送
// "switchDirection" — 切换控制方向（谁主控谁被控）
// "requestClipboard" / "clipboardData"
{
  "cmd": "setQuality",
  "params": { "width": 1280, "height": 720, "fps": 25, "quality": 75 }
}
```

---

## 5. 建立连接流程

1. **发现阶段 (UDP)**：
   - 设备周期性在 23000 端口广播 DISCOVER JSON；
   - 收到广播后更新在线列表。
2. **TCP 连接**：
   - 用户点击 UI 的设备 → 作为 client 连接对方的 listenPort (默认 23001)。
3. **握手 + 鉴权**：
   - Client → Server : HELLO
   - Server → Client : HELLO
   - 若开启配对码：两端弹 6 位码，用户确认后一端发 AUTH → 对端回 AUTH_ACK
4. **进入会话**：
   - 按 HELLO.preferences 开始视频帧循环、等待 INPUT、文件等消息。

---

## 6. UDP 设备发现

- 端口：`23000`
- 广播地址：`255.255.255.255`（IPv4）；IPv6 `ff02::1` 可后续补充
- 发送频率：每 3 秒一次；监听端收到后若 15 秒未再收到则判定设备下线
- Payload (JSON)：
```json
{
  "type": "DISCOVER",
  "deviceId": "uuid",
  "deviceName": "My-PC",
  "deviceType": "PC",
  "listenPort": 23001,
  "protocolVersion": 1
}
```
收到广播的设备可回单播一条 DISCOVER，方便对方更快发现自己（可选）。

---

## 7. 心跳 & 断连

- 每 5 秒任一端可发 PING；3 次 PING 无响应视为断线。
- BYE：`{ "reason": "USER_CLOSE" | "ERROR" | "TIMEOUT" }`
