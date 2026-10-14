using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiGateway.Hosting;
using AiGateway.ProxyKeys;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public sealed class ProxyKeyTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private GatewayServer _server = null!;
    private HttpClient _http = null!;

    private string ConfigPath => _dir.File("config.json");

    private IProxyKeyRegistry Registry => _server.Services.GetRequiredService<IProxyKeyRegistry>();

    public async Task InitializeAsync()
    {
        File.WriteAllText(ConfigPath, """{ "port": 0 }""");
        _server = GatewayServer.Create(configPath: ConfigPath);
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = _server.BaseAddress };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _dir.Dispose();
    }

    private Task<HttpResponseMessage> PostMessages(Action<HttpRequestHeaders>? auth = null, string path = "/v1/messages")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"model":"claude-sonnet","max_tokens":1,"messages":[]}""", Encoding.UTF8, "application/json"),
        };
        auth?.Invoke(request.Headers);
        return _http.SendAsync(request);
    }

    private static async Task<(string Type, string ErrorType)> ReadAnthropicError(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("type").GetString()!, json.RootElement.GetProperty("error").GetProperty("type").GetString()!);
    }

    [Fact]
    public async Task Request_without_a_key_gets_an_Anthropic_shaped_401()
    {
        var response = await PostMessages();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(("error", "authentication_error"), await ReadAnthropicError(response));
    }

    [Fact]
    public async Task Unknown_key_is_rejected_and_not_echoed_back()
    {
        const string forged = "sk-aigw-forged-0123456789";

        var response = await PostMessages(h => h.Add("x-api-key", forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(forged, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Issued_key_is_accepted_via_x_api_key()
    {
        var key = Registry.Create("laptop");

        var response = await PostMessages(h => h.Add("x-api-key", key.Plaintext));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(("error", "api_error"), await ReadAnthropicError(response));
    }

    [Fact]
    public async Task Issued_key_is_accepted_via_bearer_token()
    {
        var key = Registry.Create("laptop");

        var response = await PostMessages(h => h.Authorization = new AuthenticationHeaderValue("Bearer", key.Plaintext));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_rejected_on_the_very_next_request()
    {
        var key = Registry.Create("laptop");
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PostMessages(h => h.Add("x-api-key", key.Plaintext))).StatusCode);

        Assert.True(Registry.Revoke(key.Id));

        var rejected = await PostMessages(h => h.Add("x-api-key", key.Plaintext));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("false", string.Join(",", rejected.Headers.TryGetValues("x-should-retry", out var hint) ? hint : []));
    }

    [Fact]
    public async Task Revoking_one_key_leaves_the_others_working()
    {
        var laptop = Registry.Create("laptop");
        var desktop = Registry.Create("desktop");

        Registry.Revoke(laptop.Id);

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PostMessages(h => h.Add("x-api-key", desktop.Plaintext))).StatusCode);
    }

    [Fact]
    public void Revoke_reports_only_real_state_changes()
    {
        var key = Registry.Create("laptop");

        Assert.False(Registry.Revoke("no-such-id"));
        Assert.True(Registry.Revoke(key.Id));
        Assert.False(Registry.Revoke(key.Id));
    }

    [Fact]
    public async Task Unknown_paths_need_a_key_too_so_they_reveal_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/v1/models")).StatusCode);

        var key = Registry.Create("laptop");
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", key.Plaintext);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Health_stays_open()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void Only_the_hash_reaches_disk()
    {
        var key = Registry.Create("laptop");

        var json = File.ReadAllText(ConfigPath);

        Assert.DoesNotContain(key.Plaintext, json);
        Assert.Contains($"\"sha256\": \"{ProxyKeyHasher.HashHex(key.Plaintext)}\"", json);
        Assert.Contains("\"algo_version\": 1", json);
        Assert.Contains("\"enabled\": true", json);
    }

    [Fact]
    public void Issued_keys_are_high_entropy_and_distinct()
    {
        var a = Registry.Create("a");
        var b = Registry.Create("b");

        Assert.StartsWith(ProxyKeyRegistry.Prefix, a.Plaintext);
        Assert.Equal(ProxyKeyRegistry.Prefix.Length + 43, a.Plaintext.Length);
        Assert.NotEqual(a.Plaintext, b.Plaintext);
    }

    [Fact]
    public void Formatting_an_issued_key_never_prints_the_plaintext()
    {
        var key = Registry.Create("laptop");

        Assert.DoesNotContain(key.Plaintext, key.ToString());
        Assert.DoesNotContain(key.Plaintext, key.Fingerprint.ToString());
        Assert.Equal(ProxyKeyHasher.HashHex(key.Plaintext)[..8], key.Fingerprint.Value);
    }

    [Fact]
    public void List_gives_fingerprints_and_status_only()
    {
        var key = Registry.Create("laptop");
        Registry.Revoke(key.Id);

        var summary = Assert.Single(Registry.List());

        Assert.Equal((key.Id, "laptop", key.Fingerprint, false), (summary.Id, summary.Label, summary.Fingerprint, summary.Enabled));
    }

    [Fact]
    public async Task Deleted_key_is_gone_and_rejected()
    {
        var key = Registry.Create("laptop");

        Assert.True(Registry.Delete(key.Id));

        Assert.Empty(Registry.List());
        Assert.False(Registry.Delete(key.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMessages(h => h.Add("x-api-key", key.Plaintext))).StatusCode);
    }

    [Fact]
    public async Task Hashes_from_an_unknown_algorithm_version_are_not_trusted()
    {
        await _server.DisposeAsync();
        const string plaintext = "sk-aigw-from-a-future-version";
        File.WriteAllText(ConfigPath, $$"""
            { "port": 0, "proxy_keys": [ { "id": "k", "label": "x", "sha256": "{{ProxyKeyHasher.HashHex(plaintext)}}", "algo_version": 2, "enabled": true, "created_at": "2026-01-01T00:00:00+00:00" } ] }
            """);
        _server = GatewayServer.Create(configPath: ConfigPath);
        await _server.StartAsync();
        _http.Dispose();
        _http = new HttpClient { BaseAddress = _server.BaseAddress };

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMessages(h => h.Add("x-api-key", plaintext))).StatusCode);
    }
}
