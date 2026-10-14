using AiGateway.Secrets;

namespace AiGateway.Configuration;

internal sealed record GatewayConfig
{
    public int SchemaVersion { get; init; } = 1;

    public int Port { get; init; } = 8000;

    public IReadOnlyList<ProviderConfig> Providers { get; init; } = [];

    public IReadOnlyList<CiphertextRecord> Secrets { get; init; } = [];

    public IReadOnlyList<ProxyKeyRecord> ProxyKeys { get; init; } = [];

    public ActiveSelection? Active { get; init; }
}

internal enum UpstreamKind
{
    Anthropic,
    OpenAI,
}

internal sealed record ProviderConfig(string Id, string Name, UpstreamKind Kind, string BaseUrl, IReadOnlyList<string> Models);

internal sealed record ProxyKeyRecord(string Id, string Label, string Sha256, int AlgoVersion, bool Enabled, DateTimeOffset CreatedAt);

internal sealed record ActiveSelection(string ProviderId, string Model);
