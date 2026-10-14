using System.Security.Cryptography;
using AiGateway.Configuration;

namespace AiGateway.ProxyKeys;

internal sealed class ProxyKeyRegistry(ConfigStore store) : IProxyKeyRegistry
{
    internal const string Prefix = "sk-aigw-";

    public IssuedProxyKey Create(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var plaintext = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = ProxyKeyHasher.HashHex(plaintext);
        var record = new ProxyKeyRecord(Guid.NewGuid().ToString("N"), label, hash, ProxyKeyHasher.AlgoVersion, Enabled: true, DateTimeOffset.UtcNow);

        store.Update(config => config with { ProxyKeys = [.. config.ProxyKeys, record] });

        return new IssuedProxyKey(record.Id, label, plaintext, new ProxyKeyFingerprint(hash));
    }

    public bool Revoke(string id)
    {
        var revoked = false;
        store.Update(config =>
        {
            revoked = config.ProxyKeys.Any(k => k.Id == id && k.Enabled);
            return revoked
                ? config with { ProxyKeys = [.. config.ProxyKeys.Select(k => k.Id == id ? k with { Enabled = false } : k)] }
                : config;
        });
        return revoked;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
