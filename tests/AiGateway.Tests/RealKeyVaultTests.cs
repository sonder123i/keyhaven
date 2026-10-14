using System.Text;
using AiGateway.Secrets;
using AiGateway.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public sealed class RealKeyVaultTests : IDisposable
{
    private const string FakeKey = "sk-test-PLAINTEXT-0123456789";

    private readonly TempDirectory _dir = new();
    private readonly ServiceProvider _services;

    public RealKeyVaultTests()
    {
        _services = new ServiceCollection()
            .AddAiGateway(_dir.File("config.json"))
            .AddAiGatewaySecurity()
            .BuildServiceProvider();
    }

    private IRealKeyVault Vault => _services.GetRequiredService<IRealKeyVault>();

    public void Dispose()
    {
        _services.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Saved_key_never_reaches_disk_in_plaintext()
    {
        Vault.Save("deepseek", FakeKey);

        var json = File.ReadAllText(_dir.File("config.json"));

        Assert.DoesNotContain(FakeKey, json);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(FakeKey)), json);
        Assert.Contains("\"protector_id\": \"dpapi-currentuser-v1\"", json);
        Assert.Contains("\"key_version\": 1", json);
    }

    [Fact]
    public void Stored_ciphertext_opens_with_dpapi_for_the_same_provider()
    {
        Vault.Save("deepseek", FakeKey);

        var record = _services.GetRequiredService<ICiphertextStore>().Find("deepseek");

        Assert.NotNull(record);
        Assert.Equal(FakeKey, SecretProtector.Unprotect("deepseek", record.Ciphertext));
    }

    [Fact]
    public void Replacing_a_key_bumps_its_version()
    {
        Vault.Save("deepseek", FakeKey);
        Vault.Save("deepseek", FakeKey + "-rotated");

        Assert.Equal(2, _services.GetRequiredService<ICiphertextStore>().Find("deepseek")?.KeyVersion);
    }

    [Fact]
    public void Exists_and_Delete_follow_the_stored_state()
    {
        Assert.False(Vault.Exists("deepseek"));

        Vault.Save("deepseek", FakeKey);
        Assert.True(Vault.Exists("deepseek"));

        Assert.True(Vault.Delete("deepseek"));
        Assert.False(Vault.Exists("deepseek"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keys_are_rejected(string blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => Vault.Save("deepseek", blank));
    }
}
