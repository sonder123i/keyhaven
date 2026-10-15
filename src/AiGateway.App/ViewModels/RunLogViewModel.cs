using System.Collections.ObjectModel;
using AiGateway.RunLog;

namespace AiGateway.App.ViewModels;

internal sealed record RunLogRow(
    string Time,
    string Fingerprint,
    string Provider,
    string Model,
    string Status,
    string FirstByte,
    string Outcome)
{
    public override string ToString() => $"{Time} {Fingerprint} {Provider} {Model} {Status} {FirstByte} {Outcome}";
}

// Pull-only: the request path never touches this class, so no Dispatcher hop and no UI callback from Kestrel threads.
internal sealed class RunLogViewModel : ObservableObject
{
    private const string None = "—";

    private readonly IRunLog _log;
    private string _status = string.Empty;

    public RunLogViewModel(IRunLog log) => _log = log;

    public ObservableCollection<RunLogRow> Rows { get; } = [];

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void Refresh()
    {
        var entries = _log.Snapshot();
        Rows.Clear();
        foreach (var entry in entries)
        {
            Rows.Add(new RunLogRow(
                entry.StartedAt.ToLocalTime().ToString("HH:mm:ss"),
                entry.Fingerprint?.Value ?? None,
                entry.Provider ?? None,
                entry.Model ?? None,
                entry.StatusCode?.ToString() ?? None,
                entry.FirstByte is { } ms ? $"{(int)ms.TotalMilliseconds}ms" : None,
                OutcomeText(entry.Outcome)));
        }

        Status = entries.Count == 0
            ? "还没有请求。日志只在内存里，重启程序后清空。"
            : $"共 {entries.Count} 条（最多保留 {_log.Capacity} 条，重启清空）。";
    }

    private static string OutcomeText(RunLogOutcome outcome) => outcome switch
    {
        RunLogOutcome.ClientAborted => "客户端中止",
        RunLogOutcome.UpstreamInterrupted => "上游流中断",
        RunLogOutcome.UnhandledError => "异常",
        _ => "正常结束",
    };
}
