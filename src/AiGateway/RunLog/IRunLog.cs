namespace AiGateway.RunLog;

public enum RunLogOutcome
{
    Completed,
    ClientAborted,
    UpstreamInterrupted,
    UnhandledError,
}

// Deliberately narrow: no error text, header, body or URL may ever join this record.
public sealed record RunLogEntry(
    DateTimeOffset StartedAt,
    ProxyKeys.ProxyKeyFingerprint? Fingerprint,
    string? Provider,
    string? Model,
    int? StatusCode,
    TimeSpan? FirstByte,
    RunLogOutcome Outcome);

public interface IRunLog
{
    int Capacity { get; }

    IReadOnlyList<RunLogEntry> Snapshot();
}
