using AiGateway.Configuration;
using AiGateway.Routing;
using AiGateway.Secrets;

namespace AiGateway.Tests;

public sealed class RouteResolverTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ConfigStore _store;
    private readonly RouteResolver _resolver;

    public RouteResolverTests()
    {
        _store = new ConfigStore(_dir.File("config.json"));
        _resolver = new RouteResolver(_store);
    }

    public void Dispose() => _dir.Dispose();

    private static ProviderConfig Provider(string id, UpstreamKind kind = UpstreamKind.Anthropic) =>
        new(id, id, kind, $"https://{id}.example/anthropic", [$"{id}-model"]);

    private void StoreKeyFor(string providerId) => _store.Put(new CiphertextRecord(providerId, "dpapi-currentuser-v1", 1, [1]));

    [Fact]
    public void Nothing_active_means_no_route()
    {
        var result = _resolver.Resolve();

        Assert.Null(result.Route);
        Assert.Contains("No upstream is active", result.Problem);
    }

    [Fact]
    public void Active_provider_that_does_not_exist_means_no_route()
    {
        _store.Update(c => c with { Active = new ActiveSelection("ghost", "m") });

        Assert.Contains("'ghost' is not configured", _resolver.Resolve().Problem);
    }

    [Fact]
    public void Active_provider_without_a_key_means_no_route()
    {
        _store.Update(c => c with { Providers = [Provider("deepseek")], Active = new ActiveSelection("deepseek", "deepseek-chat") });

        Assert.Contains("No key is stored", _resolver.Resolve().Problem);
    }

    [Fact]
    public void Complete_selection_resolves_to_the_upstream_and_real_model()
    {
        _store.Update(c => c with { Providers = [Provider("deepseek")], Active = new ActiveSelection("deepseek", "deepseek-chat") });
        StoreKeyFor("deepseek");

        var route = _resolver.Resolve().Route;

        Assert.Equal(new UpstreamRoute("deepseek", UpstreamKind.Anthropic, new Uri("https://deepseek.example/anthropic"), "deepseek-chat"), route);
    }

    [Fact]
    public void Switching_takes_effect_on_the_next_resolve()
    {
        _store.Update(c => c with
        {
            Providers = [Provider("deepseek"), Provider("openai", UpstreamKind.OpenAI)],
            Active = new ActiveSelection("deepseek", "deepseek-chat"),
        });
        StoreKeyFor("deepseek");
        StoreKeyFor("openai");
        Assert.Equal("deepseek", _resolver.Resolve().Route?.ProviderId);

        _store.Update(c => c with { Active = new ActiveSelection("openai", "gpt-4.1") });

        var route = _resolver.Resolve().Route;
        Assert.Equal(("openai", UpstreamKind.OpenAI, "gpt-4.1"), (route?.ProviderId, route?.Kind, route?.Model));
    }
}
