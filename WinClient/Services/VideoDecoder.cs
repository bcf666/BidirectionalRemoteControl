using System.IO;
using WpfApp = System.Windows.Application;
using SysWin = System.Windows;
using SysWinMedia = System.Windows.Media;
using SysWinMediaImaging = System.Windows.Media.Imaging;

namespace WinClient.Services;

/// <summary>
/// MJPEG 解码器：把收到的 JPEG 字节解码并输出可绑定到 WPF Image 的 WriteableBitmap
/// 调用 Decode(jpegBytes) 后通过 Bitmap 属性绑定。
/// </summary>
public class VideoDecoder
{
    private SysWinMediaImaging.WriteableBitmap? _bmp;
    public SysWinMediaImaging.WriteableBitmap? Bitmap => _bmp;

    public event Action? FrameUpdated;

    public void Decode(byte[] jpeg)
    {
        if (jpeg == null || jpeg.Length == 0) return;
        try
        {
            using var ms = new MemoryStream(jpeg);
            var bi = new SysWinMediaImaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = SysWinMediaImaging.BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();

            var dispatcher = WpfApp.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.Invoke(() =>
            {
                var w = bi.PixelWidth; var h = bi.PixelHeight;
                if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
                {
                    _bmp = new SysWinMediaImaging.WriteableBitmap(w, h, 96, 96, SysWinMedia.PixelFormats.Bgr24, null);
                }
                var temp = new SysWinMediaImaging.FormatConvertedBitmap();
                temp.BeginInit();
                temp.Source = bi;
                temp.DestinationFormat = SysWinMedia.PixelFormats.Bgr24;
                temp.EndInit();
                temp.Freeze();
                var stride = w * 3;
                var pixels = new byte[stride * h];
                temp.CopyPixels(pixels, stride, 0);
                _bmp.Lock();
                _bmp.WritePixels(new SysWin.Int32Rect(0, 0, w, h), pixels, stride, 0);
                _bmp.AddDirtyRect(new SysWin.Int32Rect(0, 0, w, h));
                _bmp.Unlock();
                FrameUpdated?.Invoke();
            });
        }
        catch
        {
            // 损坏帧忽略
        }
    }
}

public static class JpegEncoder
{
    public static byte[] EncodeFromBitmapSource(SysWinMediaImaging.BitmapSource src, int quality = 80)
    {
        quality = Math.Clamp(quality, 1, 100);
        var enc = new SysWinMediaImaging.JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(SysWinMediaImaging.BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
