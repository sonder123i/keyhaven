using System.Windows;
using System.Windows.Controls;
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

        // Poll while the log tab is on screen; the request path never pushes into the UI (FORBIDDEN-04).
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => { if (IsVisible && Tabs.SelectedItem == RunLogTab) _vm.RunLog.Refresh(); };
        _logTimer.Start();
    }

    private void OnRefreshLog(object sender, RoutedEventArgs e) => _vm.RunLog.Refresh();

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

    // Every confirm guards something irreversible, so a stray Enter must answer No.
    private bool Confirm(string question) =>
        MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
