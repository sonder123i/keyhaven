using System.Collections.ObjectModel;
using AiGateway.ProxyKeys;

namespace AiGateway.App.ViewModels;

internal sealed record ProxyKeyRow(string Id, string Label, string Fingerprint, bool Enabled, string CreatedText)
{
    public string StatusText => Enabled ? "可用" : "已吊销";

    public override string ToString() => $"{Label}，指纹 {Fingerprint}，{StatusText}";
}

internal sealed class ProxyKeysViewModel : ObservableObject
{
    private readonly IProxyKeyRegistry _registry;
    private readonly Uri _gatewayAddress;

    private ProxyKeyRow? _selected;
    private string _newLabel = string.Empty;
    private string _message = string.Empty;

    public ProxyKeysViewModel(IProxyKeyRegistry registry, Uri gatewayAddress)
    {
        _registry = registry;
        _gatewayAddress = gatewayAddress;
        Refresh();
    }

    public ObservableCollection<ProxyKeyRow> Rows { get; } = [];

    public ProxyKeyRow? Selected { get => _selected; set => Set(ref _selected, value); }

    public string NewLabel { get => _newLabel; set => Set(ref _newLabel, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public IssuedProxyKey? Generate()
    {
        if (string.IsNullOrWhiteSpace(NewLabel))
        {
            Message = "先起个标签，比如「笔记本」，方便以后认出是哪把密钥。";
            return null;
        }

        var issued = _registry.Create(NewLabel.Trim());
        NewLabel = string.Empty;
        Refresh();
        Message = $"已生成「{issued.Label}」，指纹 {issued.Fingerprint}。";
        return issued;
    }

    public string SnippetFor(IssuedProxyKey issued) => ClaudeCodeSnippet.For(_gatewayAddress, issued.Plaintext);

    public void Revoke()
    {
        if (Selected is null)
        {
            return;
        }

        var label = Selected.Label;
        Message = _registry.Revoke(Selected.Id)
            ? $"已吊销「{label}」，用它的客户端从下一个请求起会被拒绝。吊销不可恢复，需要时请重新生成。"
            : $"「{label}」已经是吊销状态。";
        Refresh();
    }

    public void Delete()
    {
        if (Selected is null)
        {
            return;
        }

        var label = Selected.Label;
        _registry.Delete(Selected.Id);
        Refresh();
        Message = $"已删除「{label}」。";
    }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var key in _registry.List().OrderByDescending(k => k.CreatedAt))
        {
            Rows.Add(new ProxyKeyRow(key.Id, key.Label, key.Fingerprint.Value, key.Enabled, key.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }
    }
}
