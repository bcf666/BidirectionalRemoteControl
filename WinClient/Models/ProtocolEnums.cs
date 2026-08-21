namespace WinClient.Models;

/// <summary>
/// 协议消息类型 (与 Shared/enums.json 保持一致)
/// </summary>
public enum MessageType : byte
{
    HELLO       = 0x01,
    AUTH        = 0x02,
    AUTH_ACK    = 0x03,
    VIDEO       = 0x10,
    INPUT       = 0x11,
    FILE_META   = 0x20,
    FILE_ACK    = 0x21,
    FILE_CHUNK  = 0x22,
    FILE_DONE   = 0x23,
    CTRL        = 0x30,
    CTRL_ACK    = 0x31,
    PING        = 0xF0,
    PONG        = 0xF1,
    BYE         = 0xFF
}

public enum DeviceType
{
    PC,
    PHONE
}

public enum InputEventType
{
    MOUSE_MOVE,
    MOUSE_DOWN,
    MOUSE_UP,
    MOUSE_WHEEL,
    KEY_DOWN,
    KEY_UP,
    KEY_TEXT,
    TOUCH_DOWN,
    TOUCH_MOVE,
    TOUCH_UP
}

public enum MouseButton
{
    LEFT,
    RIGHT,
    MIDDLE,
    X1,
    X2
}

public static class Ports
{
    public const int UDP_DISCOVER = 23000;
    public const int TCP_DEFAULT  = 23001;
}
