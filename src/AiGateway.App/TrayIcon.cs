using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AiGateway.App.ViewModels;

namespace AiGateway.App;

// 托盘是这个应用真正的「家」（关窗只是藏到托盘），所以它必须能回答一个问题：
// 「网关现在跑在谁上面、活着没有」。原来它只有 SystemIcons.Application + 一句静态标题，等于什么都没说。
internal sealed class TrayIcon : IDisposable
{
    // Shell_NotifyIcon 对 Text 的硬上限，超出会被静默截断，所以这里自己截。
    private const int TextLimit = 63;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _state;
    private readonly ActivationViewModel _activation;
    private readonly string _address;
    private readonly Icon _iconOk;
    private readonly Icon _iconIdle;
    private bool _disposed;

    public TrayIcon(Action open, Func<Task> exit, Func<Task> checkUpdate, ActivationViewModel activation, Uri gatewayAddress)
    {
        _activation = activation;
        _address = ClaudeCodeSnippet.BaseUrl(gatewayAddress);
        _iconOk = StateIcon(Color.FromArgb(0x3E, 0x8A, 0x53));
        _iconIdle = StateIcon(Color.FromArgb(0xB6, 0x7C, 0x16));

        // 第一项是状态行，不是命令 —— 打开菜单第一眼就该看到「现在跑在谁上面」。
        _state = new ToolStripMenuItem { Enabled = false };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_state);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("打开", null, (_, _) => open());
        _menu.Items.Add("复制网关地址", null, (_, _) => ClipboardText.TryCopy(_address));
        _menu.Items.Add("检查更新", null, async (_, _) => await checkUpdate());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, async (_, _) => await exit());

        _icon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();

        _activation.PropertyChanged += OnActivationChanged;
        Update();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activation.PropertyChanged -= OnActivationChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconOk.Dispose();
        _iconIdle.Dispose();
    }

    private void OnActivationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActivationViewModel.IsActive)
            or nameof(ActivationViewModel.CurrentName)
            or nameof(ActivationViewModel.CurrentModel))
        {
            Update();
        }
    }

    // 只有两态：跑着（绿）/ 还没激活任何上游（琥珀）。没有第三态 ——
    // 「出错」在这个应用里是转瞬即逝的一条消息，不是持续状态，硬造一个红图标只会误导。
    private void Update()
    {
        var active = _activation.IsActive;
        var where = $"{_activation.CurrentName} · {_activation.CurrentModel}";

        _icon.Icon = active ? _iconOk : _iconIdle;
        _state.Text = active ? $"当前：{where}" : "未激活：还没有上游在跑";
        _icon.Text = Fit(active ? $"钥湾 Keyhaven — {where}" : "钥湾 Keyhaven — 未激活");
    }

    private static string Fit(string text) =>
        text.Length <= TextLimit ? text : string.Concat(text.AsSpan(0, TextLimit - 1), "…");

    // 图标在运行时合成：底是已嵌入程序集的 Keyhaven 头像（keyhaven.ico，发布形态仍是「9 个文件」），
    // 右下角叠状态圆点 —— 托盘是用户验收「头像换上没」的第一眼位置，不能只给一块纯色。
    // 状态色刻意不跟应用主题走：托盘在系统栏里，固定用「强色版」状态色比跟随亮/暗主题更好认。
    private static Icon StateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var avatar = AvatarBitmap())
            {
                g.DrawImage(avatar, 0f, 0f, 32f, 32f);
            }

            // 白描边把点和头像分开，16px 下也读得出绿/琥珀。
            using (var backing = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            {
                g.FillEllipse(backing, 19.5f, 19.5f, 12f, 12f);
            }

            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, 21.5f, 21.5f, 8f, 8f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone 出一份自有句柄的 Icon，再销毁原始句柄 —— 不留下每进程一次的句柄泄漏。
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap AvatarBitmap()
    {
        var uri = new Uri("pack://application:,,,/Assets/keyhaven.ico");
        var info = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("缺少嵌入资源 Assets/keyhaven.ico");
        using var stream = info.Stream;
        using var icon = new Icon(stream, 32, 32);
        return icon.ToBitmap();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
