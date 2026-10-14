using System.Windows;
using AiGateway.App.ViewModels;

namespace AiGateway.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    internal MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
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

    private void OnGenerateKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Generate() is { } issued)
        {
            new IssuedKeyWindow(issued.Label, _vm.ProxyKeys.SnippetFor(issued)) { Owner = this }.ShowDialog();
        }
    }

    private void OnRevokeKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Selected is { } row && Confirm($"吊销「{row.Label}」？用它的客户端会立刻被拒绝，且不可恢复。"))
        {
            _vm.ProxyKeys.Revoke();
        }
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        if (_vm.ProxyKeys.Selected is { } row && Confirm($"删除「{row.Label}」？"))
        {
            _vm.ProxyKeys.Delete();
        }
    }

    private void OnSavePort(object sender, RoutedEventArgs e) => _vm.Settings.Save();

    // Every confirm guards something irreversible, so a stray Enter must answer No.
    private bool Confirm(string question) =>
        MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
