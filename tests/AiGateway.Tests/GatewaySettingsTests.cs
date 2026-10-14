using AiGateway.Configuration;
using AiGateway.Routing;
using AiGateway.Secrets;
using AiGateway.Settings;

namespace AiGateway.Tests;

public sealed class GatewaySettingsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ConfigStore _store;
    private readonly GatewaySettings _settings;

    public GatewaySettingsTests()
    {
        _store = new ConfigStore(_dir.File("config.json"));
        _settings = new GatewaySettings(_store);
    }

    public void Dispose() => _dir.Dispose();

    private string AddDeepSeek(string name = "DeepSeek") =>
        _settings.AddProvider(name, UpstreamKind.Anthropic, "https://api.deepseek.com/anthropic/", [" deepseek-chat ", "deepseek-chat", "deepseek-reasoner"]);

    [Fact]
    public void New_providers_get_readable_unique_ids_and_clean_fields()
    {
        var first = AddDeepSeek();
        var second = AddDeepSeek();
        var chinese = _settings.AddProvider("深度求索", UpstreamKind.Anthropic, "https://api.deepseek.com/anthropic", ["m"]);

        Assert.Equal(["deepseek", "deepseek-2", "provider"], [first, second, chinese]);
        var saved = _settings.Providers[0];
        Assert.Equal("https://api.deepseek.com/anthropic", saved.BaseUrl);
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], saved.Models);
    }

    [Theory]
    [InlineData("", "https://api.deepseek.com/anthropic", "m", "名称")]
    [InlineData("X", "not a url", "m", "网址")]
    [InlineData("X", "ftp://example.com", "m", "网址")]
    [InlineData("X", "http://api.deepseek.com/anthropic", "m", "https")]
    [InlineData("X", "https://api.deepseek.com/anthropic", " , ", "模型")]
    public void Invalid_providers_are_refused_with_a_readable_reason(string name, string url, string models, string reasonMentions)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            _settings.AddProvider(name, UpstreamKind.Anthropic, url, models.Split(',')));

        Assert.Contains(reasonMentions, ex.Message);
        Assert.Empty(_settings.Providers);
    }

    [Fact]
    public void Plain_http_is_allowed_only_for_an_upstream_on_this_machine()
    {
        _settings.AddProvider("Local stub", UpstreamKind.Anthropic, "http://127.0.0.1:9999/anthropic", ["m"]);

        Assert.Single(_settings.Providers);
    }

    [Fact]
    public void Renaming_keeps_the_id_that_the_stored_key_is_bound_to()
    {
        var id = AddDeepSeek();

        _settings.UpdateProvider(_settings.Providers[0] with { Name = "DeepSeek 官方" });

        Assert.Equal((id, "DeepSeek 官方"), (_settings.Providers[0].Id, _settings.Providers[0].Name));
        Assert.Throws<ArgumentException>(() => _settings.UpdateProvider(_settings.Providers[0] with { Id = "ghost" }));
    }

    [Fact]
    public void Removing_a_provider_drops_its_key_and_deactivates_it()
    {
        var id = AddDeepSeek();
        _store.Put(new CiphertextRecord(id, "dpapi-currentuser-v1", 1, [1]));
        _settings.Activate(id, "deepseek-chat");

        Assert.True(_settings.RemoveProvider(id));

        Assert.Empty(_store.Current.Secrets);
        Assert.Null(_settings.Active);
        Assert.False(_settings.RemoveProvider(id));
    }

    [Fact]
    public void Activation_needs_an_existing_provider_a_model_and_a_stored_key()
    {
        var id = AddDeepSeek();

        Assert.Contains("找不到", Assert.Throws<ArgumentException>(() => _settings.Activate("ghost", "m")).Message);
        Assert.Contains("模型", Assert.Throws<ArgumentException>(() => _settings.Activate(id, " ")).Message);
        Assert.Contains("Key", Assert.Throws<ArgumentException>(() => _settings.Activate(id, "deepseek-chat")).Message);

        _store.Put(new CiphertextRecord(id, "dpapi-currentuser-v1", 1, [1]));
        _settings.Activate(id, " deepseek-reasoner ");

        Assert.Equal(new ActiveSelection(id, "deepseek-reasoner"), _settings.Active);
        Assert.Equal("deepseek-reasoner", new RouteResolver(_store).Resolve().Route?.Model);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Port_outside_the_valid_range_is_refused(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _settings.SetPort(port));
        Assert.Equal(8000, _settings.Port);
    }

    [Fact]
    public void Port_change_is_persisted()
    {
        _settings.SetPort(8123);

        Assert.Equal(8123, new ConfigStore(_dir.File("config.json")).Current.Port);
    }
}
