using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using AiGateway.App.ViewModels;
using AiGateway.Hosting;
using AiGateway.Security;
using AiGateway.Settings;
using Microsoft.Extensions.DependencyInjection;

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
            _server = await StartGatewayAsync();
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show("配置文件无法读取，网关没有启动。\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_server is null)
        {
            await ExitAsync();
            return;
        }

        _window = new MainWindow(new MainViewModel(_server.Services, _server.BaseAddress!));
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

    private static async Task<GatewayServer?> StartGatewayAsync()
    {
        while (true)
        {
            var server = GatewayServer.Create(services => services.AddAiGatewaySecurity());
            try
            {
                await server.StartAsync();
                return server;
            }
            catch (GatewayPortInUseException ex)
            {
                var free = FreeLoopbackPort();
                var answer = MessageBox.Show(
                    $"端口 {ex.Port} 已被其他程序占用，网关没有启动。\n\n要改用空闲端口 {free} 吗？改用后，Claude Code 配置里的地址也要换成这个端口。",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    server.Services.GetRequiredService<IGatewaySettings>().SetPort(free);
                }

                await server.DisposeAsync();
                if (answer != MessageBoxResult.Yes)
                {
                    return null;
                }
            }
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
