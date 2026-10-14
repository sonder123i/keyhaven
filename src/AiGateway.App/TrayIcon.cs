using System.Drawing;
using System.Windows.Forms;

namespace AiGateway.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIcon(Action open, Func<Task> exit)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开", null, (_, _) => open());
        _menu.Items.Add("退出", null, async (_, _) => await exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AI 本地代理网关",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
