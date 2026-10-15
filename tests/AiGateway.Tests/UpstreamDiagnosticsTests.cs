using System.Net;
using AiGateway.Secrets;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

// ③ 诊断端口：只答两件事 —— 「能不能到」与「这家能不能按 /models 列模型」。
// 关键在于它仍旧经上游命名客户端发出，所以这里同时守一条：探测带真 Key，但报告里不出现它。
public class UpstreamDiagnosticsTests
{
    private static IUpstreamDiagnostics Diagnostics(GatewayHarness gw) =>
        gw.Server.Services.GetRequiredService<IUpstreamDiagnostics>();

    private static async Task WriteAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    [Fact]
    public async Task An_401_still_counts_as_reachable_and_says_the_credential_was_rejected()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteAsync(context, StatusCodes.Status401Unauthorized, """{"error":{"message":"nope"}}"""),
            kind: "open_ai");

        var report = await Diagnostics(gw).ProbeAsync("deepseek");

        Assert.True(report.Reachable);
        Assert.Equal(401, report.StatusCode);
        Assert.NotNull(report.RoundTrip);
        Assert.Contains("凭据被拒", report.Problem);
    }

    [Fact]
    public async Task A_dead_endpoint_is_unreachable()
    {
        var gw = await GatewayHarness.StartAsync(kind: "open_ai");
        await gw.Upstream.DisposeAsync();

        var report = await Diagnostics(gw).ProbeAsync("deepseek");

        Assert.False(report.Reachable);
        Assert.Null(report.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(report.Problem));
        gw.Client.Dispose();
        await gw.Server.DisposeAsync();
    }

    [Fact]
    public async Task An_upstream_without_a_stored_key_is_refused_with_a_readable_reason()
    {
        await using var gw = await GatewayHarness.StartAsync(kind: "open_ai", storeKey: false);

        var report = await Diagnostics(gw).ProbeAsync("deepseek");

        Assert.False(report.Reachable);
        Assert.Contains("保存 Key", report.Problem);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Models_come_back_as_ids_and_the_version_segment_is_tried_first()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteAsync(context, StatusCodes.Status200OK, """{"data":[{"id":"m-a"},{"id":"m-b"}]}"""),
            kind: "open_ai");

        var report = await Diagnostics(gw).FetchModelsAsync("deepseek");

        Assert.True(report.Supported);
        Assert.Equal(["m-a", "m-b"], report.Models);
        Assert.Null(report.Problem);
        // 地址没有版本段时，先试 {base}/v1/models
        Assert.Equal("/v1/models", Assert.Single(gw.Upstream.Requests).PathAndQuery);
    }

    [Fact]
    public async Task A_version_segment_in_the_base_url_is_not_doubled()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteAsync(context, StatusCodes.Status200OK, """{"data":[{"id":"m-a"}]}"""),
            kind: "open_ai", basePathSuffix: "/v1");

        var report = await Diagnostics(gw).FetchModelsAsync("deepseek");

        Assert.True(report.Supported);
        Assert.Equal("/v1/models", Assert.Single(gw.Upstream.Requests).PathAndQuery);
    }

    [Fact]
    public async Task A_provider_with_no_model_endpoint_says_so_instead_of_failing_silently()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteAsync(context, StatusCodes.Status404NotFound, "{}"),
            kind: "open_ai");

        var report = await Diagnostics(gw).FetchModelsAsync("deepseek");

        Assert.False(report.Supported);
        Assert.Empty(report.Models);
        Assert.Contains("/v1", report.Problem);
        Assert.Equal(2, gw.Upstream.Requests.Count);   // 两个候选都试过了
    }

    [Fact]
    public async Task An_Anthropic_upstream_is_told_it_has_no_model_endpoint_contract()
    {
        await using var gw = await GatewayHarness.StartAsync();   // kind: anthropic

        var report = await Diagnostics(gw).FetchModelsAsync("deepseek");

        Assert.False(report.Supported);
        Assert.Contains("OpenAI", report.Problem);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task The_probe_carries_the_real_key_but_the_report_never_shows_it()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteAsync(context, StatusCodes.Status200OK, "{}"), kind: "open_ai");

        var report = await Diagnostics(gw).ProbeAsync("deepseek");

        var seen = Assert.Single(gw.Upstream.Requests).Headers;
        Assert.Equal($"Bearer {GatewayHarness.RealKey}", seen["Authorization"]);
        Assert.DoesNotContain(GatewayHarness.RealKey, string.Join('|', report.Reachable, report.StatusCode, report.RoundTrip, report.Problem));
    }

    [Fact]
    public async Task An_unknown_provider_is_reported_as_such()
    {
        await using var gw = await GatewayHarness.StartAsync(kind: "open_ai");

        var report = await Diagnostics(gw).ProbeAsync("not-there");

        Assert.False(report.Reachable);
        Assert.Contains("找不到上游", report.Problem);
        Assert.Empty(gw.Upstream.Requests);
    }
}