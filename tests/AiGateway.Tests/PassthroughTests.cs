using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Hosting;
using AiGateway.ProxyKeys;
using AiGateway.Secrets;
using AiGateway.Security;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

internal sealed class GatewayHarness : IAsyncDisposable
{
    public const string RealKey = "sk-real-UPSTREAM-0123456789";

    private readonly TempDirectory _dir = new();

    private GatewayHarness(StubUpstream upstream) => Upstream = upstream;

    public StubUpstream Upstream { get; }

    public GatewayServer Server { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public string ProxyKey { get; private set; } = null!;

    public static async Task<GatewayHarness> StartAsync(Func<HttpContext, Task>? upstreamResponds = null, string kind = "anthropic", bool storeKey = true)
    {
        var harness = new GatewayHarness(await StubUpstream.StartAsync(upstreamResponds));
        var config = harness._dir.File("config.json");
        File.WriteAllText(config, $$"""
            {
              "port": 0,
              "providers": [ { "id": "deepseek", "name": "DeepSeek", "kind": "{{kind}}", "base_url": "{{harness.Upstream.BaseAddress}}anthropic", "models": ["deepseek-chat"] } ],
              "active": { "provider_id": "deepseek", "model": "deepseek-chat" }
            }
            """);

        harness.Server = GatewayServer.Create(services => services.AddAiGatewaySecurity(), config);
        await harness.Server.StartAsync();
        if (storeKey)
        {
            harness.Server.Services.GetRequiredService<IRealKeyVault>().Save("deepseek", RealKey);
        }

        harness.ProxyKey = harness.Server.Services.GetRequiredService<IProxyKeyRegistry>().Create("tests").Plaintext;
        harness.Client = new HttpClient { BaseAddress = harness.Server.BaseAddress };
        return harness;
    }

    public HttpRequestMessage Messages(string json, string path = "/v1/messages?beta=true")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-api-key", ProxyKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "interleaved-thinking-2025-05-14");
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
        await Upstream.DisposeAsync();
        _dir.Dispose();
    }
}

public class PassthroughTests
{
    private const string Body = """
        {"model":"claude-sonnet-4-5","max_tokens":64,"stream":true,
         "system":[{"type":"text","text":"你是助手","cache_control":{"type":"ephemeral"}}],
         "thinking":{"type":"enabled","budget_tokens":1024},
         "messages":[{"role":"user","content":"列出文件 <a & b>"}],
         "tools":[{"name":"ls","description":"list","input_schema":{"type":"object","properties":{}}}],
         "metadata":{"user_id":"u-1"},"temperature":0.7}
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task<(string Type, string Message)> ReadError(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (json["error"]!["type"]!.GetValue<string>(), json["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Forwards_to_the_active_upstream_with_path_query_and_only_the_real_key()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("/anthropic/v1/messages?beta=true", seen.PathAndQuery);
        Assert.Equal(GatewayHarness.RealKey, seen.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", seen.Headers["anthropic-version"]);
        Assert.Equal("interleaved-thinking-2025-05-14", seen.Headers["anthropic-beta"]);
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(seen.Headers.Values, v => v.Contains(gw.ProxyKey));
    }

    [Fact]
    public async Task Only_the_model_changes_everything_else_arrives_intact()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var _ = await gw.Client.SendAsync(gw.Messages(Body));

        var sent = JsonNode.Parse(Body)!.AsObject();
        var received = JsonNode.Parse(Assert.Single(gw.Upstream.Requests).Body)!.AsObject();
        Assert.Equal("deepseek-chat", received["model"]!.GetValue<string>());
        sent.Remove("model");
        received.Remove("model");
        Assert.True(JsonNode.DeepEquals(sent, received));
        Assert.Contains("你是助手", Assert.Single(gw.Upstream.Requests).Body);
    }

    [Fact]
    public async Task Headers_outside_the_allow_list_stay_behind()
    {
        await using var gw = await GatewayHarness.StartAsync();
        var request = gw.Messages(Body);
        request.Headers.Add("Cookie", "session=secret");
        request.Headers.Add("x-stainless-os", "Windows");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gw.ProxyKey);

        using var _ = await gw.Client.SendAsync(request);

        var seen = Assert.Single(gw.Upstream.Requests).Headers;
        Assert.False(seen.ContainsKey("Cookie"));
        Assert.False(seen.ContainsKey("x-stainless-os"));
        Assert.False(seen.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Count_tokens_is_forwarded_and_an_upstream_404_passes_through()
    {
        await using var gw = await GatewayHarness.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        using var response = await gw.Client.SendAsync(gw.Messages(Body, "/v1/messages/count_tokens?beta=true"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("/anthropic/v1/messages/count_tokens?beta=true", Assert.Single(gw.Upstream.Requests).PathAndQuery);
    }

    [Fact]
    public async Task Upstream_errors_pass_through_with_status_body_and_retry_after()
    {
        const string upstreamError = """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""";
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "7";
            context.Response.Headers["x-should-retry"] = "true";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(upstreamError);
        });

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("7", response.Headers.RetryAfter?.ToString());
        Assert.Equal("true", ShouldRetry(response));
        Assert.Equal(upstreamError, await response.Content.ReadAsStringAsync());
        Assert.Single(gw.Upstream.Requests);
    }

    [Theory]
    [InlineData(null, "false")]
    [InlineData("true", "true")]
    public async Task Upstream_401_is_not_retried_unless_the_upstream_says_so(string? upstreamHint, string expected)
    {
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            if (upstreamHint is not null)
            {
                context.Response.Headers["x-should-retry"] = upstreamHint;
            }

            await context.Response.WriteAsync("""{"error":{"message":"Your api key: ****ECHO is invalid","type":"authentication_error"}}""");
        });

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(expected, ShouldRetry(response));
        Assert.Contains("****ECHO", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stream_reaches_the_client_event_by_event_not_all_at_the_end()
    {
        var release = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: message_start\ndata: {\"n\":1}\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("event: message_stop\ndata: {\"n\":2}\n\n");
        });

        using var response = await gw.Client.SendAsync(gw.Messages(Body), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];

        var first = Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer).AsTask().WaitAsync(Patience));
        Assert.Contains("message_start", first);
        Assert.DoesNotContain("message_stop", first);

        release.SetResult();
        var rest = await new StreamReader(stream).ReadToEndAsync().WaitAsync(Patience);
        Assert.Contains("message_stop", rest);
    }

    [Fact]
    public async Task Client_hanging_up_cancels_the_upstream_request()
    {
        var upstreamSawCancel = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: message_start\ndata: {}\n\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                upstreamSawCancel.SetResult();
            }
        });

        var response = await gw.Client.SendAsync(gw.Messages(Body), HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadAsync(new byte[4096]).AsTask().WaitAsync(Patience);

        response.Dispose();

        await upstreamSawCancel.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task Upstream_client_has_no_timeout_so_long_answers_are_not_cut()
    {
        await using var gw = await GatewayHarness.StartAsync();

        var client = gw.Server.Services.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamRequest.ClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task Nothing_active_gives_an_Anthropic_shaped_503()
    {
        await using var gw = await GatewayHarness.StartAsync();
        gw.Server.Services.GetRequiredService<Configuration.ConfigStore>().Update(c => c with { Active = null });

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("api_error", (await ReadError(response)).Type);
        Assert.Equal("false", ShouldRetry(response));
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task OpenAI_style_upstream_is_refused_until_translation_exists()
    {
        await using var gw = await GatewayHarness.StartAsync(kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("false", ShouldRetry(response));
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Key_that_cannot_be_decrypted_gives_a_readable_503()
    {
        await using var gw = await GatewayHarness.StartAsync(storeKey: false);
        gw.Server.Services.GetRequiredService<ICiphertextStore>().Put(new CiphertextRecord("deepseek", "dpapi-currentuser-v1", 1, [1, 2, 3]));

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("cannot be decrypted", (await ReadError(response)).Message);
        Assert.Equal("false", ShouldRetry(response));
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Body_that_is_not_a_json_object_is_rejected()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var response = await gw.Client.SendAsync(gw.Messages("[1,2,3]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", (await ReadError(response)).Type);
        Assert.Equal("false", ShouldRetry(response));
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Unreachable_upstream_gives_a_502()
    {
        var gw = await GatewayHarness.StartAsync();
        await gw.Upstream.DisposeAsync();

        using var response = await gw.Client.SendAsync(gw.Messages(Body));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(ShouldRetry(response));
        Assert.DoesNotContain(GatewayHarness.RealKey, await response.Content.ReadAsStringAsync());
        gw.Client.Dispose();
        await gw.Server.DisposeAsync();
    }

    // Claude Code retries 5xx for minutes unless told not to; the gateway's own configuration errors are not transient.
    private static string? ShouldRetry(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-should-retry", out var values) ? string.Join(",", values) : null;
}
