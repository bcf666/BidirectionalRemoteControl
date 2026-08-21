package com.remotecontrol.android.protocol

/** 协议消息类型，与 Shared/enums.json 完全对应 */
enum class MessageType(val value: Byte) {
    HELLO(0x01),
    AUTH(0x02),
    AUTH_ACK(0x03),
    VIDEO(0x10),
    INPUT(0x11),
    FILE_META(0x20),
    FILE_ACK(0x21),
    FILE_CHUNK(0x22),
    FILE_DONE(0x23),
    CTRL(0x30),
    CTRL_ACK(0x31),
    PING(0xF0.toByte()),
    PONG(0xF1.toByte()),
    BYE(0xFF.toByte());

    companion object {
        fun from(v: Byte): MessageType? = entries.firstOrNull { it.value == v }
    }
}

object Ports {
    const val UDP_DISCOVER = 23000
    const val TCP_DEFAULT  = 23001
}

/** 归一化坐标输入事件（与 Windows 端一致） */
enum class InputEventType {
    MOUSE_MOVE, MOUSE_DOWN, MOUSE_UP, MOUSE_WHEEL,
    KEY_DOWN, KEY_UP, KEY_TEXT,
    TOUCH_DOWN, TOUCH_MOVE, TOUCH_UP
}
