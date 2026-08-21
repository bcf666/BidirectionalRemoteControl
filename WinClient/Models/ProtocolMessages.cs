using System.Text.Json.Serialization;

namespace WinClient.Models;

#region UDP 发现 / 握手

public class DiscoverPacket
{
    [JsonPropertyName("type")]          public string Type { get; set; } = "DISCOVER";
    [JsonPropertyName("deviceId")]      public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")]    public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("deviceType")]    public string DeviceType { get; set; } = "PC";
    [JsonPropertyName("listenPort")]    public int    ListenPort { get; set; } = Ports.TCP_DEFAULT;
    [JsonPropertyName("protocolVersion")] public int  ProtocolVersion { get; set; } = 1;
}

public class Capabilities
{
    public int   MaxWidth { get; set; } = 1920;
    public int   MaxHeight { get; set; } = 1080;
    public int   MaxFps { get; set; } = 30;
    public List<string> Codecs { get; set; } = new() { "MJPEG" };
    public bool  SupportsFileTransfer { get; set; } = true;
    public bool  SupportsControl { get; set; } = true;
}

public class Preferences
{
    public int    Width   { get; set; } = 1280;
    public int    Height  { get; set; } = 720;
    public int    Fps     { get; set; } = 20;
    public string Codec   { get; set; } = "MJPEG";
    public int    Quality { get; set; } = 80;
}

public class HelloMessage
{
    public string       DeviceId   { get; set; } = string.Empty;
    public string       DeviceName { get; set; } = string.Empty;
    public string       DeviceType { get; set; } = "PC";
    public int          ProtocolVersion { get; set; } = 1;
    public Capabilities Capabilities { get; set; } = new();
    public Preferences  Preferences  { get; set; } = new();
    public int          ListenPort   { get; set; } = Ports.TCP_DEFAULT;
}

public class AuthMessage
{
    public string Code { get; set; } = string.Empty;
}
public class AuthAckMessage
{
    public bool   Ok { get; set; }
    public string Reason { get; set; } = string.Empty;
}

#endregion

#region 输入事件

public class InputEvent
{
    [JsonPropertyName("type")]        public string Type { get; set; } = string.Empty;
    [JsonPropertyName("ts")]          public long   Ts   { get; set; }

    // 坐标：0~1 归一化
    [JsonPropertyName("x")]           public float? X { get; set; }
    [JsonPropertyName("y")]           public float? Y { get; set; }

    [JsonPropertyName("button")]      public string? Button { get; set; }   // LEFT/RIGHT/...
    [JsonPropertyName("delta")]       public int?    Delta  { get; set; }
    [JsonPropertyName("axis")]        public string? Axis   { get; set; }   // "V" / "H"

    [JsonPropertyName("key")]         public string? Key    { get; set; }
    [JsonPropertyName("vk")]          public int?    Vk     { get; set; }
    [JsonPropertyName("text")]        public string? Text   { get; set; }

    [JsonPropertyName("pointerId")]   public int? PointerId { get; set; }
}

#endregion

#region 文件传输

public class FileMetaMessage
{
    public string FileId { get; set; } = Guid.NewGuid().ToString();
    public string Name   { get; set; } = string.Empty;
    public long   Size   { get; set; }
    public int    ChunkSize { get; set; } = 65536;
    public long   LastModified { get; set; }
    public string MimeType { get; set; } = "application/octet-stream";
}
public class FileAckMessage
{
    public string FileId { get; set; } = string.Empty;
    public bool   Accept { get; set; }
    public string SavePath { get; set; } = string.Empty;
}
public class FileDoneMessage
{
    public string FileId { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

#endregion

#region 控制命令

public class CtrlMessage
{
    public string               Cmd    { get; set; } = string.Empty;
    public Dictionary<string, object> Params { get; set; } = new();
}
public class CtrlAckMessage
{
    public string Cmd  { get; set; } = string.Empty;
    public bool   Ok   { get; set; }
    public string Info { get; set; } = string.Empty;
}

#endregion

#region 在线设备（UI 绑定用）

public class OnlineDevice
{
    public string DeviceId   { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "PHONE";
    public string IpAddress  { get; set; } = string.Empty;
    public int    ListenPort { get; set; } = Ports.TCP_DEFAULT;
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

#endregion
