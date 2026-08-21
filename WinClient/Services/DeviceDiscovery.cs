using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinClient.Models;

namespace WinClient.Services;

/// <summary>
/// 局域网 UDP 广播设备发现：
/// - 每 3 秒向 255.255.255.255:23000 发送 DISCOVER
/// - 监听同端口，维护在线设备列表（15 秒未收到剔除）
/// </summary>
public class DeviceDiscovery : IDisposable
{
    private readonly int _udpPort     = Ports.UDP_DISCOVER;
    private readonly int _broadcastMs = 3000;
    private readonly int _timeoutMs   = 15000;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public ObservableCollection<OnlineDevice> Devices { get; } = new();

    public DiscoverPacket LocalInfo { get; } = new()
    {
        DeviceId   = Guid.NewGuid().ToString("D"),
        DeviceName = Environment.MachineName,
        DeviceType = "PC",
        ListenPort = Ports.TCP_DEFAULT,
        ProtocolVersion = 1
    };

    public async Task StartAsync()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _udp = new UdpClient();
        _udp.EnableBroadcast = true;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));

        // 发送循环
        _ = Task.Run(() => BroadcastLoopAsync(token), token);
        // 接收循环
        _ = Task.Run(() => ReceiveLoopAsync(token), token);
        // 下线清理
        _ = Task.Run(() => CleanupLoopAsync(token), token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        _udp = null;
        _cts = null;
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;
        var ep = new IPEndPoint(IPAddress.Broadcast, _udpPort);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = JsonSerializer.Serialize(LocalInfo);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _udp.SendAsync(bytes, bytes.Length, ep);
            }
            catch { /* ignore network glitches */ }
            await Task.Delay(_broadcastMs, token).ContinueWith(_=>{});
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var pkt = JsonSerializer.Deserialize<DiscoverPacket>(json);
                if (pkt == null || pkt.DeviceId == LocalInfo.DeviceId) continue;
                if (pkt.Type?.Equals("DISCOVER", StringComparison.OrdinalIgnoreCase) == false) continue;

                var ip = result.RemoteEndPoint.Address.ToString();
                AddOrUpdate(pkt, ip);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore bad packets */ }
        }
    }

    private void AddOrUpdate(DiscoverPacket pkt, string ip)
    {
        lock (_lock)
        {
            var exist = Devices.FirstOrDefault(d => d.DeviceId == pkt.DeviceId);
            if (exist == null)
            {
                exist = new OnlineDevice
                {
                    DeviceId = pkt.DeviceId,
                    DeviceName = pkt.DeviceName,
                    DeviceType = pkt.DeviceType,
                    IpAddress = ip,
                    ListenPort = pkt.ListenPort
                };
                // UI thread-safe
                if (System.Windows.Application.Current?.Dispatcher != null)
                    System.Windows.Application.Current.Dispatcher.Invoke(() => Devices.Add(exist));
                else
                    Devices.Add(exist);
            }
            exist.IpAddress  = ip;
            exist.ListenPort = pkt.ListenPort;
            exist.DeviceName = pkt.DeviceName;
            exist.DeviceType = pkt.DeviceType;
            exist.LastSeen   = DateTime.Now;
        }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_broadcastMs * 3, token).ContinueWith(_=>{});
            if (token.IsCancellationRequested) break;
            var cutoff = DateTime.Now.AddMilliseconds(-_timeoutMs);
            lock (_lock)
            {
                var remove = Devices.Where(d => d.LastSeen < cutoff).ToList();
                if (remove.Count == 0) continue;
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var r in remove) Devices.Remove(r);
                    });
                }
                else
                {
                    foreach (var r in remove) Devices.Remove(r);
                }
            }
        }
    }

    public void Dispose() => Stop();
}
