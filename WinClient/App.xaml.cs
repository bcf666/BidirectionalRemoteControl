using SysWin = System.Windows;
using WinClient.ViewModels;
using WpfApp = System.Windows.Application;

namespace WinClient;

public partial class App : WpfApp
{
    private RemoteSession? _session;

    protected override async void OnStartup(SysWin.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Per-monitor DPI Aware：确保坐标计算精确
        try { System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2); } catch { /* ignore */ }

        _session = new RemoteSession();
        var win = new MainWindow { DataContext = _session };
        win.Closed += (_, _) =>
        {
            _session.Dispose();
        };
        win.Show();

        // 开启局域网设备发现
        try
        {
            await _session.StartDiscoveryAsync();
            _session.Status = "设备发现已开启，正在搜索局域网内的远程设备…";
        }
        catch (Exception ex)
        {
            _session.Status = "设备发现启动失败：" + ex.Message;
        }
    }
}
