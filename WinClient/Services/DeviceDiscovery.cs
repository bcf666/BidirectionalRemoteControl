using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinClient.Models;

namespace WinClient.Services;

/// <summary>
/// 局域网 UDP 广播设备发现：
/// - 每 3 秒向 255.255.255.255:23000 发送 DISCOVER
/// - 监听同端口，维护在线设备列表（15 秒未收到剔除）
/// - 自动注册 Windows 防火墙例外
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

    /// <summary>本机在局域网中的 IP 地址（供显示/手动连接）</summary>
    public string LocalIpAddress { get; private set; } = string.Empty;

    public DiscoverPacket LocalInfo { get; } = new()
    {
        DeviceId   = Guid.NewGuid().ToString("D"),
        DeviceName = Environment.MachineName,
        DeviceType = "PC",
        ListenPort = Ports.TCP_DEFAULT,
        ProtocolVersion = 1
    };

    /// <summary>启动设备发现，返回 (是否成功, 错误信息)</summary>
    public async Task<(bool ok, string error)> StartAsync()
    {
        Stop();

        try
        {
            // 获取本机局域网 IP
            LocalIpAddress = GetLocalIpAddress();

            // 尝试添加防火墙例外（静默失败也不影响主流程）
            TryAddFirewallRule();

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

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        _udp = null;
        _cts = null;
    }

    /// <summary>获取本机局域网 IP 地址</summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { /* ignore */ }

        // 回退：遍历网络接口
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip.Address))
                        return ip.Address.ToString();
                }
            }
        }
        catch { /* ignore */ }

        return "127.0.0.1";
    }

    /// <summary>尝试添加 Windows 防火墙入站 UDP 规则</summary>
    private static void TryAddFirewallRule()
    {
        try
        {
            var ruleName = "RemoteControl-UDP-Discovery";
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo.FileName = "netsh";
                process.StartInfo.Arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=UDP localport={Ports.UDP_DISCOVER}";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.Start();
                process.WaitForExit(2000);
            }
        }
        catch { /* 静默失败，用户可手动添加 */ }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;

        // 获取所有可用网络接口的广播地址
        var broadcastEndPoints = GetAllBroadcastEndPoints();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = JsonSerializer.Serialize(LocalInfo);
                var bytes = Encoding.UTF8.GetBytes(json);

                // 向每个接口的广播地址发送
                foreach (var ep in broadcastEndPoints)
                {
                    try
                    {
                        if (_udp != null)
                            await _udp.SendAsync(bytes, bytes.Length, ep).ConfigureAwait(false);
                    }
                    catch { /* 忽略单个接口的失败 */ }
                }
            }
            catch { /* ignore network glitches */ }
            await Task.Delay(_broadcastMs, token).ConfigureAwait(false);
        }
    }

    /// <summary>获取所有网络接口的广播端点</summary>
    private static List<IPEndPoint> GetAllBroadcastEndPoints()
    {
        var result = new List<IPEndPoint>();

        // 添加默认广播
        result.Add(new IPEndPoint(IPAddress.Broadcast, Ports.UDP_DISCOVER));

        // 遍历每个接口获取子网广播地址
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ipProps in ni.GetIPProperties().UnicastAddresses)
                {
                    var ipAddr = ipProps.Address;
                    if (ipAddr.AddressFamily != AddressFamily.InterNetwork) continue;

                    var maskAddr = GetSubnetMask(ni, ipAddr);

                    // 计算广播地址
                    uint ipUint = BitConverter.ToUInt32(ipAddr.GetAddressBytes(), 0);
                    uint maskUint = BitConverter.ToUInt32(maskAddr.GetAddressBytes(), 0);
                    uint broadcastUint = ipUint | ~maskUint;
                    var broadcastBytes = BitConverter.GetBytes(broadcastUint);
                    var broadcastAddr = new IPAddress(broadcastBytes);

                    result.Add(new IPEndPoint(broadcastAddr, Ports.UDP_DISCOVER));
                }
            }
        }
        catch { /* ignore */ }

        return result;
    }

    /// <summary>根据网络接口和IP获取子网掩码</summary>
    private static IPAddress GetSubnetMask(NetworkInterface ni, IPAddress ipAddr)
    {
        try
        {
            var ipp = ni.GetIPProperties();
            foreach (var ipv4 in ipp.UnicastAddresses)
            {
                if (ipv4.Address.Equals(ipAddr))
                {
                    // 通过 PrefixLength 计算掩码
                    var prefixLen = ipv4.Address.GetAddressBytes() != null 
                        ? GetPrefixLength(ni, ipAddr) 
                        : 24;
                    return PrefixLengthToMask(prefixLen);
                }
            }
        }
        catch { /* ignore */ }
        return IPAddress.Parse("255.255.255.0");
    }

    /// <summary>获取前缀长度（简化实现）</summary>
    private static int GetPrefixLength(NetworkInterface ni, IPAddress ipAddr)
    {
        try
        {
            var properties = ni.GetIPProperties();
            // 通过 DHCP 或其他方式获取前缀长度
            // 简化：尝试使用 IPv4InterfaceStatistics
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.Equals(ipAddr))
                {
                    // 对于常见的 /24 或 /16 网络，使用默认值
                    // 实际场景中可以通过 WMI 或其他 API 获取
                    var bytes = ipAddr.GetAddressBytes();
                    if (bytes[0] == 10) return 24;           // 10.x.x.x
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 16; // 172.16-31.x.x
                    if (bytes[0] == 192 && bytes[1] == 168) return 24;  // 192.168.x.x
                    return 24; // 默认 /24
                }
            }
        }
        catch { /* ignore */ }
        return 24;
    }

    /// <summary>前缀长度转子网掩码</summary>
    private static IPAddress PrefixLengthToMask(int prefixLen)
    {
        uint mask = 0xFFFFFFFF;
        if (prefixLen > 0 && prefixLen < 32)
            mask = (uint.MaxValue << (32 - prefixLen));
        else if (prefixLen == 0)
            mask = 0;
        else if (prefixLen >= 32)
            mask = uint.MaxValue;
        var bytes = BitConverter.GetBytes(mask);
        // 确保网络字节序
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return new IPAddress(bytes);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
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
            await Task.Delay(_broadcastMs * 3, token).ConfigureAwait(false);
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
