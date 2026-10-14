using AiGateway.Settings;

namespace AiGateway.App.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly IGatewaySettings _settings;
    private readonly int _runningPort;

    private string _portText;
    private string _message = string.Empty;

    public SettingsViewModel(IGatewaySettings settings, Uri gatewayAddress)
    {
        _settings = settings;
        _runningPort = gatewayAddress.Port;
        _portText = settings.Port.ToString();
        AddressText = $"当前监听：{ClaudeCodeSnippet.BaseUrl(gatewayAddress)}　地址固定为本机 127.0.0.1，只有端口可改。";
    }

    public string AddressText { get; }

    public string PortText { get => _portText; set => Set(ref _portText, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public void Save()
    {
        if (!int.TryParse(PortText.Trim(), out var port))
        {
            Message = "端口必须是数字。";
            return;
        }

        try
        {
            _settings.SetPort(port);
        }
        catch (ArgumentOutOfRangeException)
        {
            Message = "端口必须在 1 到 65535 之间。";
            return;
        }

        Message = port == _runningPort
            ? "已保存，和当前端口相同。"
            : $"已保存。下次启动网关时改用端口 {port}；届时 Claude Code 配置里的地址也要改成新端口。";
    }
}
