using System.Runtime.InteropServices;
using System.Windows;

namespace AiGateway.App;

public partial class IssuedKeyWindow : Window
{
    internal IssuedKeyWindow(string label, string snippet)
    {
        InitializeComponent();
        Heading.Text = $"已生成「{label}」的代理密钥";
        SnippetBox.Text = snippet;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SnippetBox.Text);
            CopyStatus.Text = "已复制到剪贴板";
        }
        catch (COMException)
        {
            CopyStatus.Text = "剪贴板正被别的程序占用，请在框里全选后手动复制";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
