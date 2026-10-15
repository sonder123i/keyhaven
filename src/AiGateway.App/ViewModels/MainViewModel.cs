using AiGateway.ProxyKeys;
using AiGateway.RunLog;
using AiGateway.Secrets;
using AiGateway.Settings;
using AiGateway.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.App.ViewModels;

internal sealed class MainViewModel
{
    public MainViewModel(IServiceProvider services, Uri gatewayAddress)
    {
        var settings = services.GetRequiredService<IGatewaySettings>();
        var keys = services.GetRequiredService<IProxyKeyRegistry>();
        Activation = new ActivationViewModel(settings, keys);
        Providers = new ProvidersViewModel(settings, services.GetRequiredService<IRealKeyVault>(), services.GetRequiredService<IUpstreamDiagnostics>(), Activation.Refresh);
        ProxyKeys = new ProxyKeysViewModel(keys, gatewayAddress);
        RunLog = new RunLogViewModel(services.GetRequiredService<IRunLog>());
        Settings = new SettingsViewModel(settings, gatewayAddress);
        StatusText = $"网关运行中 · {ClaudeCodeSnippet.BaseUrl(gatewayAddress)} · 关闭窗口只会隐藏到托盘，网关继续运行";
    }

    public ActivationViewModel Activation { get; }

    public ProvidersViewModel Providers { get; }

    public ProxyKeysViewModel ProxyKeys { get; }

    public RunLogViewModel RunLog { get; }

    public SettingsViewModel Settings { get; }

    public string StatusText { get; }
}
