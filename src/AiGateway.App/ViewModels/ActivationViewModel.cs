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
    private string _currentName = string.Empty;
    private string _currentModel = string.Empty;
    private string _currentNote = string.Empty;
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

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Set(ref _selectedModel, value))
            {
                Raise(nameof(CanSwitch));
            }
        }
    }

    public string CurrentText { get => _currentText; private set => Set(ref _currentText, value); }

    // 状态脊上的排版：名称与模型分开给字号，身份块要一个首字符
    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }

    public string CurrentModel { get => _currentModel; private set => Set(ref _currentModel, value); }

    // 上游列表要用它标出「使用中」那一行：切上游后 INotifyPropertyChanged 会把标记带过去，不需要刷整张表
    public string? ActiveProviderId { get; private set; }

    // 括号说明单独一条：和主值同色同重会抢第一注视点，拆出来后由视图降成次级灰。
    public string CurrentNote { get => _currentNote; private set => Set(ref _currentNote, value); }

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

    // 「切到」的初始值是空的：预选当前上游会让「切到」与「当前」显示同一个名字、切换像空操作。
    public bool CanSwitch =>
        SelectedProvider is { } target &&
        (target.Id != _settings.Active?.ProviderId || SelectedModel != _settings.Active?.Model);

    public string ProxyKeyText { get => _proxyKeyText; private set => Set(ref _proxyKeyText, value); }

    public void Refresh()
    {
        // 目标预选是既有契约（两个测试依赖它，「一键切换」也靠它成立）：保留用户选过的，否则回到当前上游
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
        Raise(nameof(CanSwitch));
    }

    private void Select(ProviderConfig? provider)
    {
        _selectedProvider = provider;
        Raise(nameof(SelectedProvider));
        Raise(nameof(CanSwitch));
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
        ActiveProviderId = active?.ProviderId;
        Raise(nameof(ActiveProviderId));
        if (active is null || provider is null)
        {
            // 空态不摆一句陈述句在「最重要的主值」位置上 —— 那一格改成动作，整块本来就是可点的入口。
            CurrentName = "挑一个上游开始";
            CurrentModel = string.Empty;
            CurrentNote = "点这里去「上游与 Key」页挑一个；在那之前，Claude Code 的请求会收到 503。";
            // CurrentText 保留原措辞：它是纯文本摘要（只有测试读它），界面不再绑它。
            CurrentText = "尚未激活任何上游 —— Claude Code 的请求会收到 503。";
            return;
        }

        CurrentName = provider.Name;
        CurrentModel = active.Model;
        CurrentNote = "（Claude Code 发来的任何模型名都会被换成这个）";
        CurrentText = $"当前：{provider.Name} · {active.Model}　{CurrentNote}";
    }
}