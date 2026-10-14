using System.Collections.ObjectModel;
using AiGateway.Configuration;
using AiGateway.Settings;

namespace AiGateway.App.ViewModels;

internal sealed class ActivationViewModel : ObservableObject
{
    private readonly IGatewaySettings _settings;

    private ProviderConfig? _selectedProvider;
    private string _selectedModel = string.Empty;
    private string _currentText = string.Empty;
    private string _message = string.Empty;

    public ActivationViewModel(IGatewaySettings settings)
    {
        _settings = settings;
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

    public string Message { get => _message; private set => Set(ref _message, value); }

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
    }

    public void Activate()
    {
        if (SelectedProvider is null)
        {
            Message = "还没有上游，请先在「上游与 Key」里添加一个。";
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
            Message = ex.Message;
        }
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
            ? "当前：尚未激活任何上游，Claude Code 的请求会收到 503。"
            : $"当前：{provider.Name} · {active.Model}　（Claude Code 发来的任何模型名都会被换成这个）";
    }
}
