using AiGateway.Configuration;
using AiGateway.Secrets;

namespace AiGateway.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Missing_file_yields_defaults_without_creating_it()
    {
        var path = _dir.File("config.json");

        var store = new ConfigStore(path);

        Assert.Equal(8000, store.Current.Port);
        Assert.Empty(store.Current.Providers);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Update_persists_and_a_new_store_reads_the_same_state()
    {
        var path = _dir.File("config.json");
        var record = new CiphertextRecord("deepseek", "dpapi-currentuser-v1", 3, [1, 2, 3, 250]);

        var store = new ConfigStore(path);
        store.Update(c => c with
        {
            Port = 8123,
            Providers = [new ProviderConfig("deepseek", "DeepSeek", UpstreamKind.Anthropic, "https://api.deepseek.com/anthropic", ["deepseek-chat"])],
            ProxyKeys = [new ProxyKeyRecord("k1", "laptop", "ab12", 1, true, DateTimeOffset.UnixEpoch)],
            Active = new ActiveSelection("deepseek", "deepseek-chat"),
        });
        store.Put(record);

        var reloaded = new ConfigStore(path).Current;

        Assert.Equal(8123, reloaded.Port);
        Assert.Equal(UpstreamKind.Anthropic, Assert.Single(reloaded.Providers).Kind);
        Assert.True(Assert.Single(reloaded.ProxyKeys).Enabled);
        Assert.Equal("deepseek-chat", reloaded.Active?.Model);
        var secret = Assert.Single(reloaded.Secrets);
        Assert.Equal((record.ProviderId, record.ProtectorId, record.KeyVersion), (secret.ProviderId, secret.ProtectorId, secret.KeyVersion));
        Assert.Equal(record.Ciphertext, secret.Ciphertext);
    }

    [Fact]
    public void Write_swaps_the_snapshot_and_leaves_no_temp_file()
    {
        var path = _dir.File("config.json");
        var store = new ConfigStore(path);
        var before = store.Current;

        store.Update(c => c with { Port = 9000 });

        Assert.NotSame(before, store.Current);
        Assert.Equal(8000, before.Port);
        Assert.Equal(9000, store.Current.Port);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Put_replaces_the_previous_secret_of_the_same_provider()
    {
        var store = new ConfigStore(_dir.File("config.json"));

        store.Put(new CiphertextRecord("p", "x", 1, [1]));
        store.Put(new CiphertextRecord("p", "x", 2, [2]));

        Assert.Equal(2, Assert.Single(store.Current.Secrets).KeyVersion);
    }

    [Fact]
    public void Remove_reports_whether_anything_was_removed()
    {
        var store = new ConfigStore(_dir.File("config.json"));
        store.Put(new CiphertextRecord("p", "x", 1, [1]));

        Assert.True(store.Remove("p"));
        Assert.False(store.Remove("p"));
        Assert.Null(store.Find("p"));
    }

    [Fact]
    public void Corrupt_file_fails_loudly_with_its_path()
    {
        var path = _dir.File("config.json");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<InvalidDataException>(() => new ConfigStore(path));

        Assert.Contains(path, ex.Message);
    }
}
