using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinClient.Models;

namespace WinClient.Services;

/// <summary>
/// TCP 消息封包 (大端 4字节长度 + 1字节类型 + payload)
/// 同时支持二进制 payload (VIDEO / FILE_CHUNK) 和 JSON payload
/// </summary>
public class Packet
{
    public MessageType Type { get; set; }
    public byte[]      Payload { get; set; } = Array.Empty<byte>();
}

public interface INetworkTransport
{
    event Action? Disconnected;
    event Action<Packet>? PacketReceived;
    bool   IsConnected { get; }
    Task   ConnectAsync(string host, int port, CancellationToken ct = default);
    Task   StartServerAsync(int port, CancellationToken ct = default);
    Task   SendPacketAsync(Packet packet, CancellationToken ct = default);
    Task   SendJsonAsync<T>(MessageType type, T payload, CancellationToken ct = default);
    Task   SendBytesAsync(MessageType type, byte[] payload, CancellationToken ct = default);
    void   Disconnect();
}

public class NetworkTransport : INetworkTransport, IDisposable
{
    private TcpClient? _client;
    private TcpListener? _listener;
    private NetworkStream? _stream;
    private CancellationTokenSource? _recvCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _client?.Connected == true;
    public event Action? Disconnected;
    public event Action<Packet>? PacketReceived;

    #region Server

    public async Task StartServerAsync(int port, CancellationToken ct = default)
    {
        StopServer();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        try
        {
            // 首版接受 1 个客户端（1v1 远程控制）
            _client = await _listener.AcceptTcpClientAsync(ct);
            _stream = _client.GetStream();
            StartReceiveLoop();
        }
        catch
        {
            StopServer();
            throw;
        }
    }

    private void StopServer()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    #endregion

    #region Client

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Disconnect();
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
        _stream = _client.GetStream();
        StartReceiveLoop();
    }

    #endregion

    #region Send

    public async Task SendPacketAsync(Packet packet, CancellationToken ct = default)
    {
        if (_stream == null || !IsConnected) throw new IOException("Not connected");
        var header = new byte[5];
        var len = packet.Payload?.Length ?? 0;
        // big-endian length
        header[0] = (byte)((len >> 24) & 0xFF);
        header[1] = (byte)((len >> 16) & 0xFF);
        header[2] = (byte)((len >>  8) & 0xFF);
        header[3] = (byte)( len        & 0xFF);
        header[4] = (byte)packet.Type;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (len > 0 && packet.Payload != null)
                await _stream.WriteAsync(packet.Payload, 0, len, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendJsonAsync<T>(MessageType type, T payload, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return SendPacketAsync(new Packet { Type = type, Payload = bytes }, ct);
    }

    public Task SendBytesAsync(MessageType type, byte[] payload, CancellationToken ct = default)
        => SendPacketAsync(new Packet { Type = type, Payload = payload }, ct);

    #endregion

    #region Receive

    private void StartReceiveLoop()
    {
        _recvCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_recvCts.Token), _recvCts.Token);
        // 心跳发送
        _ = Task.Run(() => PingLoopAsync(_recvCts.Token), _recvCts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var header = new byte[5];
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                var n = await ReadExactlyAsync(_stream, header, 5, ct).ConfigureAwait(false);
                if (n < 5) break;
                var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                len = len < 0 ? 0 : len;
                var type = (MessageType)header[4];

                byte[] payload;
                if (len == 0)
                {
                    payload = Array.Empty<byte>();
                }
                else
                {
                    payload = new byte[len];
                    var got = await ReadExactlyAsync(_stream, payload, len, ct).ConfigureAwait(false);
                    if (got < len) break;
                }

                try
                {
                    PacketReceived?.Invoke(new Packet { Type = type, Payload = payload });
                }
                catch { /* handler exception shouldn't break loop */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* connection lost */ }
        finally
        {
            RaiseDisconnected();
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            var n = await s.ReadAsync(buffer, total, count - total, ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            await Task.Delay(5000, ct).ContinueWith(_=>{});
            if (ct.IsCancellationRequested || !IsConnected) break;
            try
            {
                await SendJsonAsync(MessageType.PING, new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
            }
            catch { break; }
        }
    }

    #endregion

    public void Disconnect()
    {
        try
        {
            if (IsConnected)
            {
                try { SendPacketAsync(new Packet { Type = MessageType.BYE }).Wait(200); } catch { /* ignore */ }
            }
        }
        catch { }
        try { _recvCts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null; _client = null;
    }

    private void RaiseDisconnected()
    {
        try { Disconnected?.Invoke(); } catch { }
    }

    public void Dispose() => Disconnect();
}
