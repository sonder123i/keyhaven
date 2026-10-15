using System.Collections.ObjectModel;
using AiGateway.Configuration;
using AiGateway.ProxyKeys;
using AiGateway.Settings;

namespace AiGateway.App.ViewModels;

internal sealed class ActivationViewModel : ObservableObject
{
    private readonly IGatewaySettings _settings;
    private readonly IProxyKeyRegistry _keys;

    private ProviderConfig? _selectedProvider;
    private string _selectedModel = string.Empty;
    private string _currentText = string.Empty;
    private string _message = string.Empty;
    private string _proxyKeyText = string.Empty;

    public ActivationViewModel(IGatewaySettings settings, IProxyKeyRegistry keys)
    {
        _settings = settings;
        _keys = keys;
        Refresh();
    }

    public ObservableCollection<ProviderConfig> Providers { get; } = [];

    public ObservableCollection<string> Models { get; } = [];

    public ProviderConfig? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Equals(_selectedProvider, value))
            {
                Select(value);
            }
        }
    }

    public string SelectedModel { get => _selectedModel; set => Set(ref _selectedModel, value); }

    public string CurrentText { get => _currentText; private set => Set(ref _currentText, value); }

    // 主状态徽标靠这两条决定配色：没激活上游时整块变成告警色（今天那次白跑就是没人看见它）。
    public bool IsActive => _settings.Active is not null;

    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                Raise(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message.Length > 0;

    public string ProxyKeyText { get => _proxyKeyText; private set => Set(ref _proxyKeyText, value); }

    public void Refresh()
    {
        var keepId = _selectedProvider?.Id ?? _settings.Active?.ProviderId;
        Providers.Clear();
        foreach (var provider in _settings.Providers)
        {
            Providers.Add(provider);
        }

        Select(Providers.FirstOrDefault(p => p.Id == keepId) ?? Providers.FirstOrDefault());
        Message = string.Empty;
        UpdateCurrentText();
        RefreshKeyHint();
    }

    // 代理密钥是在「代理密钥」页签里签发的，所以那个页签动过之后要回到这里刷新一次提示。
    public void RefreshKeyHint()
    {
        var usable = _keys.List().Count(k => k.Enabled);
        ProxyKeyText = usable > 0
            ? $"代理密钥：{usable} 把可用 —— 客户端（Claude Code 等）拿它连本机网关。"
            : "还没有生成代理密钥 —— 点上面「代理密钥」页生成一把，否则客户端连不上这个网关。";
        Raise(nameof(IsActive));
    }

    public void Activate()
    {
        if (SelectedProvider is null)
        {
            Message = "还没有上游。去下面「上游与 Key」页签点「新建」，或从「一键预设」里选一家。";
            return;
        }

        try
        {
            _settings.Activate(SelectedProvider.Id, SelectedModel);
            Message = string.Empty;
            UpdateCurrentText();
        }
        catch (ArgumentException ex)
        {
            // 失败原因本来就写成给人看的（例如"还没保存 Key"），这里再补一句下一步做什么。
            Message = $"{ex.Message}（这一步没生效，当前仍是上面显示的那个上游。）";
        }

        Raise(nameof(IsActive));
    }

    private void Select(ProviderConfig? provider)
    {
        _selectedProvider = provider;
        Raise(nameof(SelectedProvider));
        Models.Clear();
        foreach (var model in provider?.Models ?? [])
        {
            Models.Add(model);
        }

        SelectedModel = _settings.Active is { } active && active.ProviderId == provider?.Id
            ? active.Model
            : Models.FirstOrDefault() ?? string.Empty;
    }

    private void UpdateCurrentText()
    {
        var active = _settings.Active;
        var provider = _settings.Providers.FirstOrDefault(p => p.Id == active?.ProviderId);
        CurrentText = active is null || provider is null
            ? "尚未激活任何上游 —— Claude Code 的请求会收到 503。"
            : $"当前：{provider.Name} · {active.Model}　（Claude Code 发来的任何模型名都会被换成这个）";
    }
}