using System.Collections.ObjectModel;
using AiGateway.Configuration;
using AiGateway.Secrets;
using AiGateway.Settings;
using AiGateway.Upstream;

namespace AiGateway.App.ViewModels;

internal sealed record KindOption(UpstreamKind Kind, string Text);

// 下拉里的一项：Preset 为 null 就是"自定义"（用户自己填）。
internal sealed record PresetOption(string Name, ProviderPreset? Preset)
{
    public override string ToString() => Name;
}

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
    private const string DefaultModelHint = "多个用逗号分隔。切换时从这里选；也可以在上方「模型」框里直接输入别的名字。";

    private readonly IGatewaySettings _settings;
    private readonly IRealKeyVault _vault;
    private readonly IUpstreamDiagnostics _diagnostics;
    private readonly Action _changed;

    private ProviderRow? _selected;
    private PresetOption? _selectedPreset;
    private string _name = string.Empty;
    private UpstreamKind _kind = UpstreamKind.Anthropic;
    private string _baseUrl = string.Empty;
    private string _models = string.Empty;
    private string _modelHint = DefaultModelHint;
    private string _message = string.Empty;

    public ProvidersViewModel(IGatewaySettings settings, IRealKeyVault vault, IUpstreamDiagnostics diagnostics, Action changed)
    {
        _settings = settings;
        _vault = vault;
        _diagnostics = diagnostics;
        _changed = changed;
        Refresh();
    }

    public ObservableCollection<ProviderRow> Rows { get; } = [];

    public IReadOnlyList<KindOption> Kinds { get; } =
        [new(UpstreamKind.Anthropic, KindText(UpstreamKind.Anthropic)), new(UpstreamKind.OpenAI, KindText(UpstreamKind.OpenAI))];

    public IReadOnlyList<PresetOption> Presets { get; } =
        [new("自定义（自己填）", null), .. ProviderPresets.All.Select(p => new PresetOption(p.Name, p))];

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

    public PresetOption? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (Set(ref _selectedPreset, value))
            {
                ApplyPreset(value?.Preset);
            }
        }
    }

    public string Name { get => _name; set => Set(ref _name, value); }

    public UpstreamKind Kind { get => _kind; set => Set(ref _kind, value); }

    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }

    public string Models { get => _models; set => Set(ref _models, value); }

    public string ModelHint { get => _modelHint; private set => Set(ref _modelHint, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool HasMessage => _message.Length > 0;

    // 列表为空时整块换成引导面板 —— 空表格什么都不说，是"第一次用看不懂"的主因。
    public bool IsEmpty => Rows.Count == 0;

    public bool CanSaveKey => Selected is not null;

    public static string KindText(UpstreamKind kind) => kind switch
    {
        UpstreamKind.Anthropic => "Anthropic 兼容（直通）",
        _ => "OpenAI（翻译）",
    };

    public void New()
    {
        Selected = null;
        SelectedPreset = Presets[0];
        Message = "填好名称、地址和模型后点「保存」。不知道填什么就从上面的「一键预设」里选一家。";
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

    // 只探"能不能到"，顺带用状态码说一句凭据有没有被拒；不发任何模型请求，所以不花钱。
    public async Task ProbeAsync()
    {
        if (Selected is null)
        {
            Message = "先在上面选中一个上游。";
            return;
        }

        Message = "正在测…";
        var report = await _diagnostics.ProbeAsync(Selected.Id);
        Message = report.Reachable
            ? $"地址可达：HTTP {report.StatusCode}，往返 {report.RoundTrip!.Value.TotalMilliseconds:0} ms。{report.Problem}　可达不等于 Key 正确 —— Key 对不对以真实请求为准。"
            : $"不可达：{report.Problem}";
    }

    // 只对 OpenAI 类上游有意义：拉回来的模型名填进「模型」框，由用户删减后再保存。
    public async Task FetchModelsAsync()
    {
        if (Selected is null)
        {
            Message = "先在上面选中一个上游。";
            return;
        }

        Message = "正在拉取…";
        var report = await _diagnostics.FetchModelsAsync(Selected.Id);
        if (report.Supported)
        {
            Models = string.Join(", ", report.Models);
            Message = $"拉到 {report.Models.Count} 个模型，已填进上面的「模型」框 —— 删掉不需要的再点「保存」。";
            return;
        }

        Message = report.Problem!;
    }

    // 给 async 的诊断调用兜底：只报类型名，不把异常正文丢到界面上（它可能带回上游回显的片段）。
    public void ReportDiagnosticFailure(Exception ex) => Message = $"诊断失败：{ex.GetType().Name}。可到「运行日志」页确认。";

    public void Refresh(string? selectId = null)
    {
        Rows.Clear();
        foreach (var provider in _settings.Providers)
        {
            Rows.Add(new ProviderRow(provider, _vault.Exists(provider.Id)));
        }

        Select(Rows.FirstOrDefault(r => r.Id == selectId));
        Raise(nameof(IsEmpty));
    }

    private void ApplyPreset(ProviderPreset? preset)
    {
        if (preset is null)
        {
            ModelHint = DefaultModelHint;
            return;
        }

        // 名称取括号前的短名（"DeepSeek（Anthropic 兼容，直通）" → "DeepSeek"），地址与类型照填。
        Name = preset.Name.Split('（')[0];
        Kind = preset.Kind;
        BaseUrl = preset.BaseUrl;
        ModelHint = preset.ExampleModel.Length > 0
            ? $"示例：{preset.ExampleModel} —— 只是示例，换成本厂家当前有效的模型名（上游改名比发版快）。{preset.Note}"
            : $"模型名以本厂家当前文档为准。{preset.Note}";
        Message = "已按预设填好类型与地址，模型名请自行确认。";
    }

    private void Select(ProviderRow? row)
    {
        _selected = row;
        Raise(nameof(Selected));
        Name = row?.Name ?? string.Empty;
        Kind = row?.Config.Kind ?? UpstreamKind.Anthropic;
        BaseUrl = row?.BaseUrl ?? string.Empty;
        Models = row?.ModelsText ?? string.Empty;
        ModelHint = DefaultModelHint;
        // 选中了某一行之后，预设下拉必须回到「自定义」—— 否则它会一直挂着上一条被点过的预设，
        // 看上去像在说"这一项来自那一家"，而字段其实已经是别的上游了。
        SelectedPreset = Presets[0];
        Raise(nameof(CanSaveKey));
    }
}