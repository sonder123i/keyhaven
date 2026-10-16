using AiGateway.Settings;

namespace AiGateway.App.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly IGatewaySettings _settings;
    private readonly int _runningPort;

    private string _portText;
    private string _message = string.Empty;
    private bool _messageIsError;

    public SettingsViewModel(IGatewaySettings settings, Uri gatewayAddress)
    {
        _settings = settings;
        _runningPort = gatewayAddress.Port;
        _portText = settings.Port.ToString();
        Address = ClaudeCodeSnippet.BaseUrl(gatewayAddress);
        AddressSnippet = ClaudeCodeSnippet.AddressOnly(gatewayAddress);
        AddressText = $"当前监听：{Address}　地址固定为本机 127.0.0.1，只有端口可改。";
    }

    // 地址单独给一份：它要等宽显示、整段可复制，不能埋在句子里
    public string Address { get; }

    // 接入片段（只含地址那半段）：给一段真代码，比把地址塞进一句散文里更好抄
    public string AddressSnippet { get; }

    public string AddressText { get; }

    public string PortText { get => _portText; set => Set(ref _portText, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    // 需要用户处理的失败：界面据此换成告警色。成功与中性提示一律 False。
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    private void Note(string text) { MessageIsError = false; Message = text; }

    private void Fail(string text) { MessageIsError = true; Message = text; }

    public void ReportCopied(bool ok)
    {
        if (ok)
        {
            Note("已复制到剪贴板。");
            return;
        }

        Fail("复制没成功：剪贴板正被其他程序占用，再点一次试试。");
    }

    public void Save()
    {
        if (!int.TryParse(PortText.Trim(), out var port))
        {
            Fail("端口必须是数字。");
            return;
        }

        try
        {
            _settings.SetPort(port);
        }
        catch (ArgumentOutOfRangeException)
        {
            Fail("端口必须在 1 到 65535 之间。");
            return;
        }

        Note(port == _runningPort
            ? "已保存，和当前端口相同。"
            : $"已保存。下次启动网关时改用端口 {port}；届时 Claude Code 配置里的地址也要改成新端口。");
    }
}
