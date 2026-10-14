using System.ComponentModel;
using System.IO;
using System.Windows;
using AiGateway.Hosting;
using AiGateway.Security;

namespace AiGateway.App;

public partial class App : Application
{
    private const string Title = "AI 本地代理网关";

    private SingleInstanceGuard? _instance;
    private GatewayServer? _server;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.AppMutexName);
        if (_instance is null)
        {
            MessageBox.Show("网关已经在运行了，请从系统托盘图标打开。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _server = GatewayServer.Create(services => services.AddAiGatewaySecurity());
            await _server.StartAsync();
        }
        catch (GatewayPortInUseException ex)
        {
            MessageBox.Show($"端口 {ex.Port} 已被其他程序占用，网关没有启动。\n\n请关闭占用它的程序，或编辑配置文件里的 port 字段后重新打开。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            await ExitAsync();
            return;
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show("配置文件无法读取，网关没有启动。\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
            return;
        }

        _window = new MainWindow(_server.BaseAddress!);
        _window.Closing += HideInsteadOfClosing;
        _tray = new TrayIcon(ShowWindow, ExitAsync);
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void HideInsteadOfClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _tray?.Dispose();
        _tray = null;

        if (_server is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _server.StopAsync(timeout.Token);
            await _server.DisposeAsync();
        }

        Shutdown();
    }
}
