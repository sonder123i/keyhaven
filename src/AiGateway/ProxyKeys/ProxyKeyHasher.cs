using System.Security.Cryptography;
using System.Text;

namespace AiGateway.ProxyKeys;

internal static class ProxyKeyHasher
{
    internal const int AlgoVersion = 1;

    // Plain SHA-256 with no salt or stretching is deliberate: proxy keys are 256-bit random values minted by this app,
    // so there is nothing to brute-force offline, and a slow hash would only add cost to every request.
    internal static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    internal static string HashHex(string key) => Convert.ToHexString(Hash(key)).ToLowerInvariant();
}
