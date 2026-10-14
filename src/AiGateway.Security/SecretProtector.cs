using System.Security.Cryptography;
using System.Text;

namespace AiGateway.Security;

internal static class SecretProtector
{
    internal const string ProtectorId = "dpapi-currentuser-v1";

    internal static byte[] Protect(string providerId, string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return ProtectedData.Protect(bytes, Entropy(providerId), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string Unprotect(string providerId, byte[] ciphertext)
    {
        var bytes = ProtectedData.Unprotect(ciphertext, Entropy(providerId), DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // Entropy binds each ciphertext to its provider id, so a provider's id must never change once a key is stored.
    private static byte[] Entropy(string providerId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("AiGateway/provider-key/v1/" + providerId));
}
