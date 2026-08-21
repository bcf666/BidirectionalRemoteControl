using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WinClient.Models;
using WinClient.Services;

namespace WinClient.ViewModels;

/// <summary>
/// 控制方向：我主控对方 还是 我被对方控制
/// </summary>
public enum ControlDirection
{
    IControlPeer,  // 我发鼠标/键盘 -> 对方执行；对方发屏幕 -> 我显示
    PeerControlsMe // 对方发鼠标/键盘 -> 我执行；我发屏幕 -> 对方显示
}

/// <summary>
/// 远程会话协调器：
/// - 持有 DeviceDiscovery / NetworkTransport / InputInjector / ScreenCapture / VideoDecoder
/// - 串联消息流程：HELLO → AUTH → 视频/输入/文件
/// - 暴露 ViewModel 可绑定的属性/命令
/// </summary>
public class RemoteSession : INotifyPropertyChanged, IDisposable
{
    public DeviceDiscovery       Discovery  { get; } = new();
    public INetworkTransport     Network    { get; } = new NetworkTransport();
    public InputInjector         Input      { get; } = new();
    public ScreenCaptureService  Capture    { get; } = new();
    public VideoDecoder          Decoder    { get; } = new();
    public FileTransferService   Files      { get; }

    public ControlDirection Direction { get; set; } = ControlDirection.IControlPeer;

    #region 绑定属性

    private string _status = "未连接";
    public string Status { get => _status; set { _status = value; OnProp(); } }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set { _isConnected = value; OnProp(); OnProp(nameof(NotConnected)); } }
    public bool NotConnected => !IsConnected;

    private OnlineDevice? _selectedDevice;
    public OnlineDevice? SelectedDevice { get => _selectedDevice; set { _selectedDevice = value; OnProp(); } }

    private bool _actAsServer = false;
    /// <summary>True=被动等待对方连接；False=主动连接 SelectedDevice</summary>
    public bool ActAsServer { get => _actAsServer; set { _actAsServer = value; OnProp(); } }

    private int _listenPort = Ports.TCP_DEFAULT;
    public int ListenPort { get => _listenPort; set { _listenPort = value; OnProp(); } }

    #endregion

    #region Commands

    public ICommand ConnectAsClientCommand { get; }
    public ICommand StartServerCommand    { get; }
    public ICommand DisconnectCommand     { get; }
    public ICommand SwitchDirectionCommand{ get; }

    #endregion

    public RemoteSession()
    {
        Files = new FileTransferService(Network);
        ConnectAsClientCommand = new RelayCommand(OnConnectAsClient,  _ => SelectedDevice != null && NotConnected);
        StartServerCommand     = new RelayCommand(OnStartServer,       _ => NotConnected);
        DisconnectCommand      = new RelayCommand(_ => DoDisconnect(), _ => IsConnected);
        SwitchDirectionCommand = new RelayCommand(_ =>
        {
            Direction = Direction == ControlDirection.IControlPeer
                ? ControlDirection.PeerControlsMe
                : ControlDirection.IControlPeer;
            ApplyDirection();
            OnProp(nameof(Direction));
        });

        Network.PacketReceived += OnPacket;
        Network.Disconnected   += () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = false;
                Status = "已断开";
                StopCaptureAndListen();
            });
        };

        Capture.FrameCaptured += (jpg, w, h) =>
        {
            if (!IsConnected) return;
            try { Network.SendBytesAsync(MessageType.VIDEO, jpg).Wait(500); } catch { }
        };
    }

    #region 公共启动

    public Task<(bool ok, string error)> StartDiscoveryAsync() => Discovery.StartAsync();

    #endregion

    #region 连接

    private async void OnConnectAsClient(object? _)
    {
        if (SelectedDevice == null) return;
        Status = $"正在连接 {SelectedDevice.DeviceName} ({SelectedDevice.IpAddress})...";
        try
        {
            await Network.ConnectAsync(SelectedDevice.IpAddress, SelectedDevice.ListenPort);
            await HandshakeAsync();
            IsConnected = true;
            Status = $"已连接到 {SelectedDevice.DeviceName}（我主动发起）";
            ApplyDirection();
        }
        catch (Exception ex)
        {
            Status = $"连接失败：{ex.Message}";
        }
    }

    private async void OnStartServer(object? _)
    {
        Status = $"正在监听端口 {ListenPort}，等待对端连接...";
        try
        {
            await Network.StartServerAsync(ListenPort);
            await HandshakeAsync();
            IsConnected = true;
            Status = "对端已接入";
            ApplyDirection();
        }
        catch (Exception ex)
        {
            Status = $"监听失败：{ex.Message}";
        }
    }

    private void DoDisconnect()
    {
        StopCaptureAndListen();
        Network.Disconnect();
        IsConnected = false;
        Status = "已手动断开";
    }

    private void StopCaptureAndListen()
    {
        Capture.Stop();
    }

    #endregion

    #region 握手

    private async Task HandshakeAsync()
    {
        var hello = new HelloMessage
        {
            DeviceId = Discovery.LocalInfo.DeviceId,
            DeviceName = Discovery.LocalInfo.DeviceName,
            DeviceType = Discovery.LocalInfo.DeviceType,
            ListenPort = ListenPort,
            Capabilities = new Capabilities
            {
                MaxWidth = (int)System.Windows.SystemParameters.PrimaryScreenWidth,
                MaxHeight = (int)System.Windows.SystemParameters.PrimaryScreenHeight,
                MaxFps = 30,
                Codecs = new List<string> { "MJPEG" }
            },
            Preferences = new Preferences { Width = 1280, Height = 720, Fps = 20, Quality = 80, Codec = "MJPEG" }
        };
        await Network.SendJsonAsync(MessageType.HELLO, hello);
        Capture.Configure(hello.Preferences.Width, hello.Preferences.Height, hello.Preferences.Fps, hello.Preferences.Quality);
    }

    private void ApplyDirection()
    {
        if (!IsConnected) return;
        if (Direction == ControlDirection.PeerControlsMe)
        {
            // 对方控制我：我需要持续发屏幕，并处理收到的 INPUT 事件（由 OnPacket 完成）
            Capture.Start();
            Status = "会话中（对方控制我，正在共享我的屏幕）";
        }
        else
        {
            // 我控制对方：我接收屏幕并显示（由 OnPacket VIDEO 完成），我的鼠标键盘要发出 INPUT
            Capture.Stop();
            Status = "会话中（我控制对方，正在接收对端屏幕）";
        }
    }

    #endregion

    #region 消息分发

    private void OnPacket(Packet pkt)
    {
        switch (pkt.Type)
        {
            case MessageType.HELLO:
                // 首版不强制双向校验，简单记日志即可
                break;
            case MessageType.PING:
                try { _ = Network.SendJsonAsync(MessageType.PONG, new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); } catch { }
                break;
            case MessageType.PONG: break;
            case MessageType.BYE:
                DoDisconnect();
                break;
            case MessageType.VIDEO:
                // 只有"我控制对方"时才渲染
                if (Direction == ControlDirection.IControlPeer)
                    Decoder.Decode(pkt.Payload);
                break;
            case MessageType.INPUT:
                // 只有"对方控制我"时才注入
                if (Direction == ControlDirection.PeerControlsMe)
                {
                    try
                    {
                        var ev = System.Text.Json.JsonSerializer.Deserialize<InputEvent>(pkt.Payload);
                        if (ev != null) Input.Dispatch(ev);
                    }
                    catch { /* ignore */ }
                }
                break;
            case MessageType.FILE_META:
            case MessageType.FILE_ACK:
            case MessageType.FILE_DONE:
                // v1 留给上层手动处理；可以扩展事件
                break;
            case MessageType.FILE_CHUNK:
                Files.WriteChunk(pkt.Payload);
                break;
            case MessageType.CTRL:
                break;
        }
    }

    #endregion

    #region 对外辅助：PC 主控时发出输入事件

    /// <summary>主控端（IControlPeer）：向对端发送输入事件</summary>
    public Task SendInputAsync(InputEvent ev)
    {
        if (!IsConnected || Direction != ControlDirection.IControlPeer) return Task.CompletedTask;
        ev.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Network.SendJsonAsync(MessageType.INPUT, ev);
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    #endregion

    public void Dispose()
    {
        DoDisconnect();
        Discovery.Stop();
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool> _canExecute;
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute; _canExecute = canExecute ?? (_ => true);
    }
    public bool CanExecute(object? parameter) => _canExecute(parameter);
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
