using System.Security.Cryptography;
using AiGateway.Security;

namespace AiGateway.Tests;

public class SecretProtectorTests
{
    private const string FakeKey = "sk-test-0123456789abcdef";

    [Fact]
    public void Round_trips_for_the_same_provider()
    {
        var ciphertext = SecretProtector.Protect("deepseek", FakeKey);

        Assert.Equal(FakeKey, SecretProtector.Unprotect("deepseek", ciphertext));
    }

    [Fact]
    public void Ciphertext_of_one_provider_cannot_be_opened_as_another()
    {
        var ciphertext = SecretProtector.Protect("provider-a", FakeKey);

        Assert.ThrowsAny<CryptographicException>(() => SecretProtector.Unprotect("provider-b", ciphertext));
    }
}
