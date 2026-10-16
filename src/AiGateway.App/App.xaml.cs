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
using Microsoft.Win32;

namespace AiGateway.App;

public partial class App : Application
{
    private const string Title = "钥湾 Keyhaven";

    private SingleInstanceGuard? _instance;
    private GatewayServer? _server;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public App()
    {
        // 高 DPI：工程同时用 WinForms（托盘），所以清单里的 dpiAware 声明会被分析器 WFAC010 拦下，
        // 它指定的替代就是这一句。必须落在「还没有任何窗口」之前 —— 构造早于生成的 Main 里剩下的一切。
        // 注意 ApplicationHighDpiMode 属性在这里是空操作：它只注入 WinForms 的 Program.Main，本工程没有那个入口。
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 必须在任何窗口 XAML 解析之前：token 都是 DynamicResource，换掉合并链里的那一个字典即可全局生效。
        ApplySystemTheme();

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

        var vm = new MainViewModel(_server.Services, _server.BaseAddress!);
        _window = new MainWindow(vm);
        _window.Closing += HideInsteadOfClosing;
        _tray = new TrayIcon(ShowWindow, ExitAsync, _window.ManualCheckUpdateAsync, vm.Activation, _server.BaseAddress!);
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    // 跟随系统亮暗偏好。没有手动开关 —— 那要往 config.json 里加一个字段，
    // 属于配置层（D2）的决定，不该由界面顺手加。
    private static bool IsSystemDark()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void ApplySystemTheme()
    {
        if (!IsSystemDark())
        {
            return;
        }

        Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri("Themes/Theme.Dark.xaml", UriKind.Relative),
        };
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
