using SysWin = System.Windows;
using WinClient.ViewModels;
using WinClient.Models;
using WpfApp = System.Windows.Application;

namespace WinClient;

public partial class App : WpfApp
{
    private RemoteSession? _session;

    protected override async void OnStartup(SysWin.StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Per-monitor DPI Aware：确保坐标计算精确
            try { System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2); } catch { /* ignore */ }

            _session = new RemoteSession();
            var win = new MainWindow { DataContext = _session };
            win.Closed += (_, _) =>
            {
                _session?.Dispose();
            };
            win.Show();

            // 开启局域网设备发现
            var (ok, error) = await _session.StartDiscoveryAsync();
            if (ok)
            {
                var ip = _session.Discovery.LocalIpAddress;
                _session.Status = $"设备发现已开启 · 本机 IP：{ip} · 端口 {Ports.UDP_DISCOVER}";
            }
            else
            {
                _session.Status = $"设备发现启动失败：{error}。请检查网络连接和防火墙设置。";
            }
        }
        catch (Exception ex)
        {
            SysWin.MessageBox.Show($"启动失败：{ex.Message}\n\n堆栈：{ex.StackTrace}", "双向远程控制 启动错误",
                SysWin.MessageBoxButton.OK, SysWin.MessageBoxImage.Error);
            Shutdown();
        }
    }
}
