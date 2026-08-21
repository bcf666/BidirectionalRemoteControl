using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using WinClient.Models;

namespace WinClient.Services;

/// <summary>
/// GDI 截屏服务，按设定帧率抓取屏幕 -> JPEG 字节数组 (MJPEG)
/// 首版简化实现：Graphics.CopyFromScreen（不依赖 SharpDX）
/// </summary>
public class ScreenCaptureService : IDisposable
{
    private CancellationTokenSource? _cts;
    private int _targetFps = 20;
    private int _jpegQuality = 80;
    private int _targetWidth = 1280;
    private int _targetHeight = 720;
    private readonly ImageCodecInfo? _jpegCodec;
    private readonly EncoderParameters _encParams;

    public event Action<byte[], int, int>? FrameCaptured;  // jpeg, w, h

    public ScreenCaptureService()
    {
        _jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encParams = new EncoderParameters(1);
        ApplyQualityParam();
    }

    private void ApplyQualityParam()
    {
        _encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);
    }

    public void Configure(int targetWidth, int targetHeight, int fps, int jpegQuality)
    {
        _targetWidth  = Math.Max(160, targetWidth);
        _targetHeight = Math.Max(120, targetHeight);
        _targetFps    = Math.Clamp(fps, 1, 60);
        _jpegQuality  = Math.Clamp(jpegQuality, 10, 100);
        ApplyQualityParam();
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoopAsync(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
        while (!ct.IsCancellationRequested)
        {
            var start = DateTime.Now;
            try
            {
                var (jpeg, w, h) = CaptureJpeg();
                if (jpeg != null)
                    FrameCaptured?.Invoke(jpeg, w, h);
            }
            catch { /* never crash */ }
            var elapsed = DateTime.Now - start;
            var wait = frameInterval - elapsed;
            if (wait.TotalMilliseconds > 1)
                await Task.Delay(wait, ct).ContinueWith(_=>{});
        }
    }

    /// <summary>Capture full primary screen -> scaled -> JPEG bytes.</summary>
    private (byte[]? jpeg, int w, int h) CaptureJpeg()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        int outW = _targetWidth, outH = _targetHeight;
        // 保持原始比例按短边适配（letterbox）—— 先按简单缩放
        var scale = Math.Min((double)outW / full.Width, (double)outH / full.Height);
        int finalW = Math.Max(1, (int)Math.Round(full.Width * scale));
        int finalH = Math.Max(1, (int)Math.Round(full.Height * scale));

        using var scaled = new Bitmap(finalW, finalH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(full, 0, 0, finalW, finalH);
        }

        using var ms = new MemoryStream();
        if (_jpegCodec != null) scaled.Save(ms, _jpegCodec, _encParams);
        else                    scaled.Save(ms, ImageFormat.Jpeg);
        return (ms.ToArray(), finalW, finalH);
    }

    public void Dispose() => Stop();
}
