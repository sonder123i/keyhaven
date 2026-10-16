using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AiGateway.App.ViewModels;

namespace AiGateway.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _logTimer;

    internal MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.RunLog.Refresh();
        VersionText.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        StateChanged += (_, _) => SyncMaximizeGlyph();

        // Poll while the log tab is on screen; the request path never pushes into the UI (FORBIDDEN-04).
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => { if (IsVisible && Tabs.SelectedItem == RunLogTab) _vm.RunLog.Refresh(); };
        _logTimer.Start();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // 代理密钥是在另一个页签签发的，切回来时顺手刷新顶栏那句状态。
        _vm.Activation.RefreshKeyHint();
        if (Tabs.SelectedItem == RunLogTab)
        {
            _vm.RunLog.Refresh();
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && Tabs.SelectedItem == RunLogTab)
        {
            _vm.RunLog.Refresh();
        }
    }

    private void OnActivate(object sender, RoutedEventArgs e) => _vm.Activation.Activate();

    // 这个工具的用家本来就是键盘驱动的：Ctrl+1..4 直达四个页签，且不占任何视觉空间。
    // （Alt 访问键要往标签里塞拉丁助记字母，会多出可见字符，与「清爽」相冲，故不做。）
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1,
        };

        if (index < 0 || index >= Tabs.Items.Count)
        {
            return;
        }

        Tabs.SelectedIndex = index;
        e.Handled = true;
    }

    // 身份块就是「现在跑在谁上面」，点它去改它 —— 不再另开一个交互层
    private void OnOpenProvidersTab(object sender, MouseButtonEventArgs e) => Tabs.SelectedItem = ProvidersTab;

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // 无边框标题栏的 ✕：与原来一致 —— 关窗口只是藏到托盘，网关继续跑（由 HideInsteadOfClosing 兜住）
    private void OnCloseToTray(object sender, RoutedEventArgs e) => Close();

    // 双击标题栏/贴边最大化也会改 WindowState，所以图标跟 StateChanged 走，不只跟按钮
    private void SyncMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewProvider(object sender, RoutedEventArgs e) => _vm.Providers.New();

    private void OnSaveProvider(object sender, RoutedEventArgs e) => _vm.Providers.Save();

    private void OnDeleteProvider(object sender, RoutedEventArgs e)
    {
        if (_vm.Providers.Selected is { } row && Confirm($"删除上游「{row.Name}」？它保存的 Key 也会一起删除。"))
        {
            _vm.Providers.Delete();
        }
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        _vm.Providers.SaveKey(KeyBox.Password);
        KeyBox.Clear();
    }

    // 诊断是 async 的：异常绝不能让它逃出 async void（那会直接杀掉进程）。
    private async void OnProbe(object sender, RoutedEventArgs e) => await Guarded(_vm.Providers.ProbeAsync);

    private async void OnFetchModels(object sender, RoutedEventArgs e) => await Guarded(_vm.Providers.FetchModelsAsync);

    private async Task Guarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _vm.Providers.ReportDiagnosticFailure(ex);
        }
    }

    private void OnGenerateKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Generate() is { } issued)
        {
            new IssuedKeyWindow(issued.Label, _vm.ProxyKeys.SnippetFor(issued)) { Owner = this }.ShowDialog();
        }

        _vm.Activation.RefreshKeyHint();
    }

    private void OnRevokeKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Selected is { } row && Confirm($"吊销「{row.Label}」？用它的客户端会立刻被拒绝，且不可恢复。"))
        {
            _vm.ProxyKeys.Revoke();
            _vm.Activation.RefreshKeyHint();
        }
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Selected is { } row && Confirm($"删除「{row.Label}」？"))
        {
            _vm.ProxyKeys.Delete();
            _vm.Activation.RefreshKeyHint();
        }
    }

    private void OnSavePort(object sender, RoutedEventArgs e) => _vm.Settings.Save();

    // 复制整段地址：走 ClipboardText（Win32 直写），不走 OLE —— 撞上占用会假失败、撞上卡死的剪贴板宿主还会卡住 UI
    private void OnCopyAddress(object sender, RoutedEventArgs e) =>
        _vm.Settings.ReportCopied(ClipboardText.TryCopy(_vm.Settings.Address));

    // 复制接入片段：同一个理由走 ClipboardText；两处复制共用一个「已复制/没成功」提示
    private void OnCopySnippet(object sender, RoutedEventArgs e) =>
        _vm.Settings.ReportCopied(ClipboardText.TryCopy(_vm.Settings.AddressSnippet));

    // Every confirm guards something irreversible, so a stray Enter must answer No.
    private bool Confirm(string question) =>
        MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
