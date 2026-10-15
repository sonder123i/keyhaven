using System.Net;
using AiGateway.Configuration;
using AiGateway.ProxyKeys;
using AiGateway.RunLog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class RunLogPipelineTests
{
    private const string Body = """{"model":"claude-sonnet-4-5","max_tokens":8,"stream":true,"messages":[{"role":"user","content":"hi"}]}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static RunLogRing Ring(GatewayHarness gw) => gw.Server.Services.GetRequiredService<RunLogRing>();

    private static async Task<RunLogEntry> SingleEntry(GatewayHarness gw)
    {
        await WaitUntil(() => Ring(gw).Snapshot().Count > 0);
        return Assert.Single(Ring(gw).Snapshot());
    }

    private static Task WaitUntil(Func<bool> condition) => Task.Run(async () =>
    {
        while (!condition())
        {
            await Task.Delay(25);
        }
    }).WaitAsync(Patience);

    [Fact]
    public async Task A_configuration_failure_still_names_the_active_provider()
    {
        await using var gw = await GatewayHarness.StartAsync(storeKey: false);

        using var response = await gw.Client.SendAsync(gw.Messages(Body));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var entry = await SingleEntry(gw);
        Assert.Equal("deepseek", entry.Provider);
        Assert.Null(entry.Model);
        Assert.Equal(503, entry.StatusCode);
    }

    [Fact]
    public void The_forwarder_cannot_re_read_configuration_because_it_has_none()
    {
        var parameters = typeof(AiGateway.Passthrough.PassthroughForwarder)
            .GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name);

        Assert.Equal(["RouteResolver", "IHttpClientFactory"], parameters);
    }

    [Fact]
    public async Task A_successful_request_logs_exactly_one_line_with_fingerprint_route_and_status()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var response = await gw.Client.SendAsync(gw.Messages(Body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await SingleEntry(gw);
        var issued = gw.Server.Services.GetRequiredService<IProxyKeyRegistry>().List().Single();
        Assert.Equal(issued.Fingerprint.Value, entry.Fingerprint?.Value);
        Assert.Equal(("deepseek", "deepseek-chat", 200), (entry.Provider, entry.Model, entry.StatusCode));
        Assert.Equal(RunLogOutcome.Completed, entry.Outcome);
        Assert.NotNull(entry.FirstByte);
    }

    [Fact]
    public async Task Health_is_never_logged()
    {
        await using var gw = await GatewayHarness.StartAsync();

        await gw.Client.GetAsync("/health");

        Assert.Empty(Ring(gw).Snapshot());
    }

    [Fact]
    public async Task A_rejected_key_is_logged_once_without_a_fingerprint()
    {
        await using var gw = await GatewayHarness.StartAsync();
        var request = gw.Messages(Body);
        request.Headers.Remove("x-api-key");

        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.Client.SendAsync(request)).StatusCode);

        var entry = await SingleEntry(gw);
        Assert.Null(entry.Fingerprint);
        Assert.Equal((401, RunLogOutcome.Completed), (entry.StatusCode, entry.Outcome));
    }

    [Fact]
    public async Task An_unknown_path_is_logged_once()
    {
        await using var gw = await GatewayHarness.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", gw.ProxyKey);

        Assert.Equal(HttpStatusCode.NotFound, (await gw.Client.SendAsync(request)).StatusCode);

        var entry = await SingleEntry(gw);
        Assert.Equal((404, RunLogOutcome.Completed), (entry.StatusCode, entry.Outcome));
    }

    [Fact]
    public async Task A_whole_sse_stream_still_produces_one_line()
    {
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: message_start\ndata: {}\n\n");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("event: message_stop\ndata: {}\n\n");
        });

        await (await gw.Client.SendAsync(gw.Messages(Body))).Content.ReadAsStringAsync();

        var entry = await SingleEntry(gw);
        Assert.Equal(RunLogOutcome.Completed, entry.Outcome);
    }

    [Fact]
    public async Task A_client_hanging_up_is_logged_once_as_aborted()
    {
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
            }
        });

        var response = await gw.Client.SendAsync(gw.Messages(Body), HttpCompletionOption.ResponseHeadersRead);
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            await stream.ReadAsync(new byte[16]);
        }
        response.Dispose();

        var entry = await SingleEntry(gw);
        Assert.Equal(RunLogOutcome.ClientAborted, entry.Outcome);
        // Headers had already reached the client, so 200 is what it saw.
        Assert.Equal(200, entry.StatusCode);
    }

    [Fact]
    public async Task A_mid_flight_switch_does_not_change_the_logged_model()
    {
        var release = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: message_start\ndata: {}\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("event: message_stop\ndata: {}\n\n");
        });

        var inFlight = gw.Client.SendAsync(gw.Messages(Body), HttpCompletionOption.ResponseHeadersRead);
        await Task.Delay(200);
        gw.Server.Services.GetRequiredService<ConfigStore>().Update(c => c with { Active = new ActiveSelection("deepseek", "deepseek-reasoner") });
        release.SetResult();
        (await inFlight).Dispose();

        var entry = await SingleEntry(gw);
        Assert.Equal("deepseek-chat", entry.Model);
    }

    [Fact]
    public async Task First_byte_is_only_counted_when_upstream_body_arrives()
    {
        var release = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("data: {}\n\n");
        });

        var inFlight = gw.Client.SendAsync(gw.Messages(Body), HttpCompletionOption.ResponseHeadersRead);
        await Task.Delay(300);
        Assert.Empty(Ring(gw).Snapshot());

        release.SetResult();
        (await inFlight).Dispose();

        var entry = await SingleEntry(gw);
        Assert.True(entry.FirstByte > TimeSpan.Zero);
    }

    [Fact]
    public async Task No_line_contains_any_key_or_the_body()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var response = await gw.Client.SendAsync(gw.Messages(Body));
        await response.Content.ReadAsStringAsync();
        var entry = await SingleEntry(gw);

        var printed = string.Join('|', entry.ToString(), entry.Fingerprint?.Value, entry.Provider, entry.Model);
        Assert.DoesNotContain(GatewayHarness.RealKey, printed);
        Assert.DoesNotContain(gw.ProxyKey, printed);
    }
}
