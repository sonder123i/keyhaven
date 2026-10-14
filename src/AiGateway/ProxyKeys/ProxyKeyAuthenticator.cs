using System.Security.Cryptography;
using AiGateway.Configuration;
using Microsoft.AspNetCore.Http;

namespace AiGateway.ProxyKeys;

internal sealed class ProxyKeyAuthenticator(ConfigStore store)
{
    public bool IsAuthorized(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var presented = ProxyKeyHasher.Hash(presentedKey);
        var matched = false;
        foreach (var key in store.Current.ProxyKeys)
        {
            if (key.Enabled && key.AlgoVersion == ProxyKeyHasher.AlgoVersion)
            {
                matched |= CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(key.Sha256));
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
