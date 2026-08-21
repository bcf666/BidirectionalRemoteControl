using System.Globalization;
using System.IO;
using SysWin = System.Windows;
using SysWinData = System.Windows.Data;
using SysWinCtl = System.Windows.Controls;
using SysWinInp = System.Windows.Input;
using SysWinMedia = System.Windows.Media;
using SysWinMediaImaging = System.Windows.Media.Imaging;
using SysWinShapes = System.Windows.Shapes;
using Models = WinClient.Models;
using VM = WinClient.ViewModels;
using Drawing = System.Drawing;

namespace WinClient;

#region Converters
public class DeviceTypeVisibilityConverter : SysWinData.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cur = (value as string) ?? "";
        var expected = parameter as string ?? "";
        return cur.Equals(expected, StringComparison.OrdinalIgnoreCase) ? SysWin.Visibility.Visible : SysWin.Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => SysWinData.Binding.DoNothing;
}
#endregion

public partial class MainWindow : SysWin.Window
{
    public VM.RemoteSession Session => (VM.RemoteSession)DataContext;

    public MainWindow()
    {
        InitializeComponent();
    }

    private SysWin.Point _last = new(-1, -1);

    private (float nx, float ny, bool inside) GetNormalizedXY(SysWinCtl.Image img, SysWinInp.MouseEventArgs e)
    {
        var src = img.Source as SysWinMediaImaging.BitmapSource;
        var pos = e.GetPosition(img);
        var w = img.ActualWidth;
        var h = img.ActualHeight;
        if (src != null && w > 0 && h > 0)
        {
            var imgW = src.PixelWidth;
            var imgH = src.PixelHeight;
            var scale = Math.Min(w / imgW, h / imgH);
            var drawW = imgW * scale;
            var drawH = imgH * scale;
            var offX = (w - drawW) / 2;
            var offY = (h - drawH) / 2;
            pos.X -= offX;
            pos.Y -= offY;
            if (pos.X < 0 || pos.Y < 0 || pos.X > drawW || pos.Y > drawH)
                return (0, 0, false);
            return ((float)(pos.X / drawW), (float)(pos.Y / drawH), true);
        }
        if (w > 0 && h > 0)
        {
            return ((float)(pos.X / w), (float)(pos.Y / h), pos.X >= 0 && pos.Y >= 0 && pos.X <= w && pos.Y <= h);
        }
        return (0, 0, false);
    }

    private string ParseButtonTag(SysWinInp.MouseButton b) => b switch
    {
        SysWinInp.MouseButton.Left   => "LEFT",
        SysWinInp.MouseButton.Right  => "RIGHT",
        SysWinInp.MouseButton.Middle => "MIDDLE",
        SysWinInp.MouseButton.XButton1 => "X1",
        SysWinInp.MouseButton.XButton2 => "X2",
        _ => "LEFT"
    };

    private async void RemoteScreenImage_OnMouseMove(object sender, SysWinInp.MouseEventArgs e)
    {
        var (x, y, ok) = GetNormalizedXY(RemoteScreenImage, e);
        if (!ok) { _last = new SysWin.Point(-1, -1); return; }
        if (Math.Abs(_last.X - x) < 0.001 && Math.Abs(_last.Y - y) < 0.001) return;
        _last = new SysWin.Point(x, y);
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        await Session.SendInputAsync(new Models.InputEvent { Type = "MOUSE_MOVE", X = x, Y = y });
    }

    private async void RemoteScreenImage_OnMouseDown(object sender, SysWinInp.MouseButtonEventArgs e)
    {
        RemoteScreenImage.Focus();
        var (x, y, ok) = GetNormalizedXY(RemoteScreenImage, e);
        if (!ok) return;
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        await Session.SendInputAsync(new Models.InputEvent
        {
            Type = "MOUSE_DOWN",
            X = x, Y = y,
            Button = ParseButtonTag(e.ChangedButton)
        });
    }

    private async void RemoteScreenImage_OnMouseUp(object sender, SysWinInp.MouseButtonEventArgs e)
    {
        var (x, y, ok) = GetNormalizedXY(RemoteScreenImage, e);
        if (!ok) return;
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        await Session.SendInputAsync(new Models.InputEvent
        {
            Type = "MOUSE_UP",
            X = x, Y = y,
            Button = ParseButtonTag(e.ChangedButton)
        });
    }

    private async void RemoteScreenImage_OnMouseWheel(object sender, SysWinInp.MouseWheelEventArgs e)
    {
        var (x, y, ok) = GetNormalizedXY(RemoteScreenImage, e);
        if (!ok) return;
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        await Session.SendInputAsync(new Models.InputEvent
        {
            Type = "MOUSE_WHEEL",
            X = x, Y = y,
            Delta = e.Delta,
            Axis = "V"
        });
    }

    private async void RemoteScreenImage_OnKeyDown(object sender, SysWinInp.KeyEventArgs e)
    {
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        var vk = (int)SysWinInp.KeyInterop.VirtualKeyFromKey(e.Key);
        await Session.SendInputAsync(new Models.InputEvent { Type = "KEY_DOWN", Vk = vk });
    }

    private async void RemoteScreenImage_OnKeyUp(object sender, SysWinInp.KeyEventArgs e)
    {
        if (Session.Direction != VM.ControlDirection.IControlPeer || !Session.IsConnected) return;
        var vk = (int)SysWinInp.KeyInterop.VirtualKeyFromKey(e.Key);
        await Session.SendInputAsync(new Models.InputEvent { Type = "KEY_UP", Vk = vk });
    }

    private async void RemoteScreenImage_OnDrop(object sender, SysWin.DragEventArgs e)
    {
        if (!Session.IsConnected) return;
        if (!e.Data.GetDataPresent(SysWin.DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(SysWin.DataFormats.FileDrop)!;
        foreach (var f in files)
        {
            if (File.Exists(f))
            {
                var prog = new Progress<double>(p => Session.Status = $"发送文件 {Path.GetFileName(f)}：{(int)(p * 100)}%");
                try { await Session.Files.SendFileAsync(f, prog); }
                catch (Exception ex) { Session.Status = $"文件发送失败：{ex.Message}"; }
            }
        }
    }
}
