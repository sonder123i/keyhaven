using System.Security.Cryptography;
using AiGateway.Configuration;
using Microsoft.AspNetCore.Http;

namespace AiGateway.ProxyKeys;

internal sealed class ProxyKeyAuthenticator(ConfigStore store)
{
    // Returns the matched key's fingerprint (S9: the run log needs it and nothing else), or null.
    // The loop never exits early: every enabled record is compared, so no timing signal leaks which one matched.
    public ProxyKeyFingerprint? Authorize(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        var presented = ProxyKeyHasher.Hash(presentedKey);
        ProxyKeyFingerprint? matched = null;
        foreach (var key in store.Current.ProxyKeys)
        {
            if (key.Enabled && key.AlgoVersion == ProxyKeyHasher.AlgoVersion)
            {
                if (CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(key.Sha256)))
                {
                    matched = new ProxyKeyFingerprint(key.Sha256);
                }
            }
        }

        return matched;
    }

    public static string? ReadPresentedKey(HttpRequest request)
    {
        var apiKey = request.Headers["x-api-key"].ToString();
        if (apiKey.Length > 0)
        {
            return apiKey;
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
