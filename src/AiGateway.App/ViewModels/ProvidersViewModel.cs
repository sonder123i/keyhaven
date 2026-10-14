using System.Collections.ObjectModel;
using AiGateway.Configuration;
using AiGateway.Secrets;
using AiGateway.Settings;

namespace AiGateway.App.ViewModels;

internal sealed record KindOption(UpstreamKind Kind, string Text);

internal sealed record ProviderRow(ProviderConfig Config, bool HasKey)
{
    public string Id => Config.Id;

    public string Name => Config.Name;

    public string KindText => ProvidersViewModel.KindText(Config.Kind);

    public string BaseUrl => Config.BaseUrl;

    public string ModelsText => string.Join(", ", Config.Models);

    public string KeyText => HasKey ? "已保存" : "未保存";

    public override string ToString() => $"{Name}，{KindText}，Key {KeyText}";
}

internal sealed class ProvidersViewModel : ObservableObject
{
    private readonly IGatewaySettings _settings;
    private readonly IRealKeyVault _vault;
    private readonly Action _changed;

    private ProviderRow? _selected;
    private string _name = string.Empty;
    private UpstreamKind _kind = UpstreamKind.Anthropic;
    private string _baseUrl = string.Empty;
    private string _models = string.Empty;
    private string _message = string.Empty;

    public ProvidersViewModel(IGatewaySettings settings, IRealKeyVault vault, Action changed)
    {
        _settings = settings;
        _vault = vault;
        _changed = changed;
        Refresh();
    }

    public ObservableCollection<ProviderRow> Rows { get; } = [];

    public IReadOnlyList<KindOption> Kinds { get; } =
        [new(UpstreamKind.Anthropic, KindText(UpstreamKind.Anthropic)), new(UpstreamKind.OpenAI, KindText(UpstreamKind.OpenAI))];

    public ProviderRow? Selected
    {
        get => _selected;
        set
        {
            if (!Equals(_selected, value))
            {
                Select(value);
            }
        }
    }

    public string Name { get => _name; set => Set(ref _name, value); }

    public UpstreamKind Kind { get => _kind; set => Set(ref _kind, value); }

    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }

    public string Models { get => _models; set => Set(ref _models, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool CanSaveKey => Selected is not null;

    public static string KindText(UpstreamKind kind) => kind switch
    {
        UpstreamKind.Anthropic => "Anthropic 兼容（直通）",
        _ => "OpenAI（翻译，暂未支持）",
    };

    public void New()
    {
        Selected = null;
        Message = "填好名称、地址和模型后点「保存」。";
    }

    public void Save()
    {
        var models = Models.Split([',', '，', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (Selected is null)
            {
                var id = _settings.AddProvider(Name, Kind, BaseUrl, models);
                Refresh(id);
                Message = "已添加。下一步：在下面保存这个上游的 Key。";
            }
            else
            {
                _settings.UpdateProvider(Selected.Config with { Name = Name, Kind = Kind, BaseUrl = BaseUrl, Models = models });
                Refresh(Selected.Id);
                Message = "已保存修改。";
            }

            _changed();
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
    }

    public void Delete()
    {
        if (Selected is null)
        {
            return;
        }

        _settings.RemoveProvider(Selected.Id);
        Refresh();
        Message = "已删除这个上游和它保存的 Key。";
        _changed();
    }

    public void SaveKey(string key)
    {
        if (Selected is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            Message = "请先输入 Key。";
            return;
        }

        _vault.Save(Selected.Id, key.Trim());
        Refresh(Selected.Id);
        Message = "Key 已加密保存在本机，界面里不会再显示它。";
        _changed();
    }

    public void Refresh(string? selectId = null)
    {
        Rows.Clear();
        foreach (var provider in _settings.Providers)
        {
            Rows.Add(new ProviderRow(provider, _vault.Exists(provider.Id)));
        }

        Select(Rows.FirstOrDefault(r => r.Id == selectId));
    }

    private void Select(ProviderRow? row)
    {
        _selected = row;
        Raise(nameof(Selected));
        Name = row?.Name ?? string.Empty;
        Kind = row?.Config.Kind ?? UpstreamKind.Anthropic;
        BaseUrl = row?.BaseUrl ?? string.Empty;
        Models = row?.ModelsText ?? string.Empty;
        Raise(nameof(CanSaveKey));
    }
}
