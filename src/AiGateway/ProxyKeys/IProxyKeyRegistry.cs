namespace AiGateway.ProxyKeys;

public readonly record struct ProxyKeyFingerprint
{
    internal ProxyKeyFingerprint(string sha256Hex) => Value = sha256Hex[..8];

    public string Value { get; }

    public override string ToString() => Value;
}

// A class, not a record: a record's generated ToString would print Plaintext into any log that formats this object.
public sealed class IssuedProxyKey(string id, string label, string plaintext, ProxyKeyFingerprint fingerprint)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string Plaintext { get; } = plaintext;

    public ProxyKeyFingerprint Fingerprint { get; } = fingerprint;
}

public sealed record ProxyKeySummary(string Id, string Label, ProxyKeyFingerprint Fingerprint, bool Enabled, DateTimeOffset CreatedAt);

public interface IProxyKeyRegistry
{
    IssuedProxyKey Create(string label);

    IReadOnlyList<ProxyKeySummary> List();

    bool Revoke(string id);

    bool Delete(string id);
}
