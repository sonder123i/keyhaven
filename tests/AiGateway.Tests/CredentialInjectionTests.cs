using System.Net;
using AiGateway.Hosting;
using AiGateway.Secrets;
using AiGateway.Security;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AiGateway.Tests;

public sealed class CredentialInjectionTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string RealKey = "sk-real-UPSTREAM-0123456789";

    private readonly TempDirectory _dir = new();
    private readonly CapturingLoggerProvider _logs = new();
    private ServiceProvider _services = null!;
    private StubUpstream _upstream = null!;

    private HttpClient UpstreamClient =>
        _services.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamRequest.ClientName);

    public async Task InitializeAsync()
    {
        _upstream = await StubUpstream.StartAsync();
        _services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs))
            .AddAiGateway(_dir.File("config.json"))
            .AddAiGatewaySecurity()
            .BuildServiceProvider();
        _services.GetRequiredService<IRealKeyVault>().Save("deepseek", RealKey);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _upstream.DisposeAsync();
        _dir.Dispose();
    }

    private HttpRequestMessage NewRequest(Uri target, string? providerId = "deepseek")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent("{}") };
        if (providerId is not null)
        {
            request.Options.Set(UpstreamRequest.ProviderId, providerId);
        }

        return request;
    }

    private Uri Messages => new(_upstream.BaseAddress, "/v1/messages");

    [Fact]
    public async Task Upstream_receives_the_real_key_as_x_api_key()
    {
        using var response = await UpstreamClient.SendAsync(NewRequest(Messages));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RealKey, Assert.Single(_upstream.Requests).Headers["x-api-key"]);
    }

    [Fact]
    public async Task Inbound_credentials_are_replaced_never_forwarded()
    {
        var request = NewRequest(Messages);
        request.Headers.Add("x-api-key", "sk-aigw-the-proxy-key");
        request.Headers.Add("Authorization", "Bearer sk-aigw-the-proxy-key");

        using var _ = await UpstreamClient.SendAsync(request);

        var seen = Assert.Single(_upstream.Requests).Headers;
        Assert.Equal(RealKey, seen["x-api-key"]);
        Assert.False(seen.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Whoever_holds_the_request_cannot_read_the_key_after_sending()
    {
        var request = NewRequest(Messages);

        using var _ = await UpstreamClient.SendAsync(request);

        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.DoesNotContain(RealKey, request.ToString());
    }

    [Fact]
    public async Task Upstream_client_writes_nothing_to_logs_so_the_key_cannot_leak_there()
    {
        _services.GetRequiredService<ILoggerFactory>().CreateLogger("probe").LogTrace("capture is live");

        using var _ = await UpstreamClient.SendAsync(NewRequest(Messages));

        var logs = string.Join('\n', _logs.Lines);
        output.WriteLine(logs);

        Assert.Contains("capture is live", logs);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("HttpClient." + UpstreamRequest.ClientName));
        Assert.DoesNotContain(RealKey, logs);
    }

    [Fact]
    public async Task Redirects_are_returned_not_followed()
    {
        await using var elsewhere = await StubUpstream.StartAsync();
        await using var redirecting = await StubUpstream.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = new Uri(elsewhere.BaseAddress, "/collect").ToString();
            return Task.CompletedTask;
        });

        using var response = await UpstreamClient.SendAsync(NewRequest(new Uri(redirecting.BaseAddress, "/v1/messages")));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Empty(elsewhere.Requests);
    }

    [Fact]
    public async Task Request_without_a_provider_id_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpstreamClient.SendAsync(NewRequest(Messages, providerId: null)));

        Assert.DoesNotContain(RealKey, ex.Message);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Provider_without_a_stored_key_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpstreamClient.SendAsync(NewRequest(Messages, providerId: "unknown")));

        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Works_the_same_inside_the_real_gateway_host()
    {
        var configPath = _dir.File("host.json");
        File.WriteAllText(configPath, """{ "port": 0 }""");
        await using var server = GatewayServer.Create(services => services.AddAiGatewaySecurity(), configPath);
        server.Services.GetRequiredService<IRealKeyVault>().Save("deepseek", RealKey);

        var client = server.Services.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamRequest.ClientName);
        using var _ = await client.SendAsync(NewRequest(Messages));

        Assert.Equal(RealKey, Assert.Single(_upstream.Requests).Headers["x-api-key"]);
    }
}
