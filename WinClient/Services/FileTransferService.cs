using WinClient.Models;
using System.IO;

namespace WinClient.Services;

/// <summary>
/// 文件传输服务：
/// - 发送：SendFile(path) -> 发 FILE_META -> 等 FILE_ACK -> 循环 FILE_CHUNK -> 发 FILE_DONE
/// - 接收：收到 FILE_META 弹 UI 确认 -> 回 FILE_ACK -> 收 CHUNK -> 收到 FILE_DONE 校验
/// 文件分片格式（FILE_CHUNK Payload）：
///   [fileId 36 bytes UTF-8][offset 4 bytes BE][data ...]
/// </summary>
public class FileTransferService
{
    public const int CHUNK_SIZE = 65536; // 64KB

    private readonly INetworkTransport _net;
    public FileTransferService(INetworkTransport net) { _net = net; }

    #region 发送

    public async Task SendFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException(path);

        var fileId = Guid.NewGuid().ToString("D");
        var meta = new FileMetaMessage
        {
            FileId = fileId,
            Name = fi.Name,
            Size = fi.Length,
            ChunkSize = CHUNK_SIZE,
            LastModified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()
        };
        await _net.SendJsonAsync(MessageType.FILE_META, meta, ct);

        // TODO: 等待对端 FILE_ACK（这里首版简化：延迟 500ms 后直接开始发，实现联通性；真实项目用 TaskCompletionSource 订阅消息循环）
        await Task.Delay(500, ct).ContinueWith(_ => { });

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var buf = new byte[CHUNK_SIZE];
        long offset = 0;
        int read;
        while ((read = await fs.ReadAsync(buf, 0, buf.Length, ct)) > 0)
        {
            var payload = BuildChunkPayload(fileId, offset, buf, 0, read);
            await _net.SendBytesAsync(MessageType.FILE_CHUNK, payload, ct);
            offset += read;
            progress?.Report(fi.Length == 0 ? 1.0 : (double)offset / fi.Length);
        }
        await _net.SendJsonAsync(MessageType.FILE_DONE, new FileDoneMessage { FileId = fileId }, ct);
    }

    private static byte[] BuildChunkPayload(string fileId, long offset, byte[] data, int idx, int count)
    {
        var buf = new byte[36 + 4 + count];
        var idBytes = System.Text.Encoding.UTF8.GetBytes(fileId.PadRight(36).Substring(0, 36));
        Array.Copy(idBytes, buf, 36);
        // offset 首版用 uint4 即可（>= 4GB 文件后续扩展为 8 字节）
        uint offU = (uint)Math.Min(uint.MaxValue, offset);
        buf[36 + 0] = (byte)((offU >> 24) & 0xFF);
        buf[36 + 1] = (byte)((offU >> 16) & 0xFF);
        buf[36 + 2] = (byte)((offU >>  8) & 0xFF);
        buf[36 + 3] = (byte)( offU        & 0xFF);
        Array.Copy(data, idx, buf, 40, count);
        return buf;
    }

    #endregion

    #region 接收（被动，由上层在收到 FILE_META 时保存句柄，收到 CHUNK 时写入）

    private readonly Dictionary<string, (FileStream fs, string path)> _receiving = new();

    public async Task StartReceiving(FileMetaMessage meta, string saveDir)
    {
        Directory.CreateDirectory(saveDir);
        var savePath = Path.Combine(saveDir, SanitizeFileName(meta.Name));
        // 去重名
        var i = 1;
        while (File.Exists(savePath))
        {
            savePath = Path.Combine(saveDir, $"{Path.GetFileNameWithoutExtension(meta.Name)}_{i}{Path.GetExtension(meta.Name)}");
            i++;
        }
        var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
        _receiving[meta.FileId] = (fs, savePath);
        await _net.SendJsonAsync(MessageType.FILE_ACK, new FileAckMessage { FileId = meta.FileId, Accept = true, SavePath = savePath });
    }

    public void WriteChunk(byte[] payload)
    {
        if (payload.Length < 40) return;
        var fileId = System.Text.Encoding.UTF8.GetString(payload, 0, 36).TrimEnd();
        if (!_receiving.TryGetValue(fileId, out var entry)) return;
        uint offset = ((uint)payload[36] << 24) | ((uint)payload[37] << 16) | ((uint)payload[38] << 8) | payload[39];
        entry.fs.Seek(offset, SeekOrigin.Begin);
        entry.fs.Write(payload, 40, payload.Length - 40);
    }

    public string? FinishFile(string fileId)
    {
        if (!_receiving.TryGetValue(fileId, out var entry)) return null;
        entry.fs.Flush();
        entry.fs.Dispose();
        _receiving.Remove(fileId);
        return entry.path;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim();
    }

    #endregion
}
