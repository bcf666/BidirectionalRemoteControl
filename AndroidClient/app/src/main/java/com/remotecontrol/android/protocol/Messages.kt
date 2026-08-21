package com.remotecontrol.android.protocol

import org.json.JSONObject

/** 所有协议消息的 JSON 辅助封装（保持跨端字段一致）。首版用 JSONObject，后续可切换 protobuf。 */

class DiscoverPacket(
    val deviceId: String,
    val deviceName: String,
    val deviceType: String = "PHONE",
    val listenPort: Int = Ports.TCP_DEFAULT,
    val protocolVersion: Int = 1
) {
    fun toJson(): JSONObject = JSONObject()
        .put("type", "DISCOVER")
        .put("deviceId", deviceId)
        .put("deviceName", deviceName)
        .put("deviceType", deviceType)
        .put("listenPort", listenPort)
        .put("protocolVersion", protocolVersion)

    companion object {
        fun fromJson(j: JSONObject): DiscoverPacket = DiscoverPacket(
            deviceId = j.optString("deviceId"),
            deviceName = j.optString("deviceName"),
            deviceType = j.optString("deviceType", "PC"),
            listenPort = j.optInt("listenPort", Ports.TCP_DEFAULT),
            protocolVersion = j.optInt("protocolVersion", 1)
        )
    }
}

data class OnlineDevice(
    val deviceId: String,
    val deviceName: String,
    val deviceType: String,
    val ipAddress: String,
    val listenPort: Int,
    val lastSeenMs: Long
)

data class Capabilities(
    val maxWidth: Int, val maxHeight: Int, val maxFps: Int,
    val codecs: List<String> = listOf("MJPEG"),
    val supportsFileTransfer: Boolean = true,
    val supportsControl: Boolean = true
)
data class Preferences(
    val width: Int = 1280, val height: Int = 720,
    val fps: Int = 20, val codec: String = "MJPEG", val quality: Int = 80
)

class HelloMessage(
    val deviceId: String, val deviceName: String, val deviceType: String,
    val protocolVersion: Int = 1,
    val capabilities: Capabilities, val preferences: Preferences,
    val listenPort: Int = Ports.TCP_DEFAULT
) {
    fun toJson(): JSONObject = JSONObject()
        .put("deviceId", deviceId).put("deviceName", deviceName).put("deviceType", deviceType)
        .put("protocolVersion", protocolVersion)
        .put("capabilities", JSONObject().apply {
            put("maxWidth", capabilities.maxWidth)
            put("maxHeight", capabilities.maxHeight)
            put("maxFps", capabilities.maxFps)
            put("codecs", org.json.JSONArray(capabilities.codecs))
            put("supportsFileTransfer", capabilities.supportsFileTransfer)
            put("supportsControl", capabilities.supportsControl)
        })
        .put("preferences", JSONObject().apply {
            put("width", preferences.width); put("height", preferences.height)
            put("fps", preferences.fps); put("codec", preferences.codec)
            put("quality", preferences.quality)
        })
        .put("listenPort", listenPort)

    companion object {
        fun fromJson(j: JSONObject): HelloMessage = HelloMessage(
            deviceId = j.optString("deviceId"),
            deviceName = j.optString("deviceName"),
            deviceType = j.optString("deviceType", "PC"),
            protocolVersion = j.optInt("protocolVersion", 1),
            capabilities = j.optJSONObject("capabilities").let { c ->
                Capabilities(
                    maxWidth = c?.optInt("maxWidth", 1920) ?: 1920,
                    maxHeight = c?.optInt("maxHeight", 1080) ?: 1080,
                    maxFps = c?.optInt("maxFps", 30) ?: 30,
                    codecs = c?.optJSONArray("codecs")?.let { arr ->
                        (0 until arr.length()).map { i -> arr.optString(i) }
                    } ?: listOf("MJPEG")
                )
            },
            preferences = j.optJSONObject("preferences").let { p ->
                Preferences(
                    width = p?.optInt("width", 1280) ?: 1280,
                    height = p?.optInt("height", 720) ?: 720,
                    fps = p?.optInt("fps", 20) ?: 20,
                    codec = p?.optString("codec", "MJPEG") ?: "MJPEG",
                    quality = p?.optInt("quality", 80) ?: 80
                )
            },
            listenPort = j.optInt("listenPort", Ports.TCP_DEFAULT)
        )
    }
}

/** 输入事件：与 Windows 端共享字段（x/y 归一化 0~1） */
data class InputEvent(
    val type: String,
    val x: Float? = null, val y: Float? = null,
    val button: String? = null, val delta: Int? = null, val axis: String? = null,
    val key: String? = null, val vk: Int? = null, val text: String? = null,
    val pointerId: Int? = null,
    val ts: Long = System.currentTimeMillis()
) {
    fun toJson(): JSONObject {
        val o = JSONObject().put("type", type).put("ts", ts)
        x?.let { o.put("x", it) }; y?.let { o.put("y", it) }
        button?.let { o.put("button", it) }; delta?.let { o.put("delta", it) }
        axis?.let { o.put("axis", it) }; key?.let { o.put("key", it) }
        vk?.let { o.put("vk", it) }; text?.let { o.put("text", it) }
        pointerId?.let { o.put("pointerId", it) }
        return o
    }
    companion object {
        fun fromJson(j: JSONObject): InputEvent = InputEvent(
            type = j.optString("type"),
            x = if (j.has("x")) j.optDouble("x").toFloat() else null,
            y = if (j.has("y")) j.optDouble("y").toFloat() else null,
            button = j.optString("button").takeIf { it.isNotEmpty() },
            delta = if (j.has("delta")) j.optInt("delta") else null,
            axis = j.optString("axis").takeIf { it.isNotEmpty() },
            key = j.optString("key").takeIf { it.isNotEmpty() },
            vk = if (j.has("vk")) j.optInt("vk") else null,
            text = j.optString("text").takeIf { it.isNotEmpty() },
            pointerId = if (j.has("pointerId")) j.optInt("pointerId") else null,
            ts = j.optLong("ts")
        )
    }
}

/** 文件传输 */
data class FileMeta(val fileId: String, val name: String, val size: Long,
                    val chunkSize: Int = 65536, val lastModified: Long = 0L,
                    val mimeType: String = "application/octet-stream") {
    fun toJson(): JSONObject = JSONObject()
        .put("fileId", fileId).put("name", name).put("size", size)
        .put("chunkSize", chunkSize).put("lastModified", lastModified)
        .put("mimeType", mimeType)
    companion object {
        fun fromJson(j: JSONObject) = FileMeta(
            j.optString("fileId"), j.optString("name"), j.optLong("size"),
            j.optInt("chunkSize", 65536), j.optLong("lastModified"),
            j.optString("mimeType", "application/octet-stream")
        )
    }
}
data class FileAck(val fileId: String, val accept: Boolean, val savePath: String = "") {
    fun toJson(): JSONObject = JSONObject()
        .put("fileId", fileId).put("accept", accept).put("savePath", savePath)
}
data class FileDone(val fileId: String, val sha256: String = "") {
    fun toJson(): JSONObject = JSONObject()
        .put("fileId", fileId).put("sha256", sha256)
}

/** 控制命令 */
data class CtrlMsg(val cmd: String, val params: Map<String, Any> = emptyMap()) {
    fun toJson(): JSONObject = JSONObject().put("cmd", cmd)
        .put("params", JSONObject(params))
}

/** 消息辅助工具集 */
object Messages {
    fun InputEventFromJson(j: JSONObject): InputEvent = InputEvent.fromJson(j)
}
