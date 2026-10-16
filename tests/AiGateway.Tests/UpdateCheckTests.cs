using System.Net;
using AiGateway.App;
using Xunit;

namespace AiGateway.Tests;

// 更新检查的护栏：只提示不自更；网络 / 解析故障一律退化成 Failed，绝不抛异常（启动时是静默跑）。
// 全部走 StubHandler，不打真网络。
public class UpdateCheckTests
{
    private static readonly Version Current = new(1, 0, 0);

    [Theory]
    [InlineData("v1.1.0", true)]
    [InlineData("v1.0.1", true)]
    [InlineData("v2.0.0", true)]
    [InlineData("1.0.1", true)]
    [InlineData("v1.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("v0.9.9", false)]
    [InlineData("v1.0.0-rc.1", false)]
    [InlineData("vabc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Tag_compared_against_current_version(string? tag, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewerThan(tag, Current));

    [Fact]
    public async Task Newer_release_is_reported_with_download_page()
    {
        var result = await CheckAsync(ClientReturning(Json("v1.2.0", url: "https://github.com/sonder123i/keyhaven/releases/tag/v1.2.0")));

        Assert.Equal(UpdateCheck.Status.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/sonder123i/keyhaven/releases/tag/v1.2.0", result.ReleaseUrl!.ToString());
    }

    [Fact]
    public async Task Same_version_is_up_to_date()
    {
        var result = await CheckAsync(ClientReturning(Json("v1.0.0")));

        Assert.Equal(UpdateCheck.Status.UpToDate, result.Status);
    }

    [Fact]
    public async Task Draft_release_is_ignored()
    {
        var result = await CheckAsync(ClientReturning(Json("v9.9.9", draft: true)));

        Assert.Equal(UpdateCheck.Status.UpToDate, result.Status);
    }

    [Fact]
    public async Task Prerelease_is_ignored()
    {
        var result = await CheckAsync(ClientReturning(Json("v9.9.9", prerelease: true)));

        Assert.Equal(UpdateCheck.Status.UpToDate, result.Status);
    }

    [Fact]
    public async Task Unparsable_body_fails_without_throwing()
    {
        var broken = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>rate limited</html>") };
        var result = await CheckAsync(ClientReturning(broken));

        Assert.Equal(UpdateCheck.Status.Failed, result.Status);
    }

    [Fact]
    public async Task Http_error_is_failed_not_exception()
    {
        var result = await CheckAsync(ClientReturning(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal(UpdateCheck.Status.Failed, result.Status);
    }

    [Fact]
    public async Task Request_is_get_without_body_or_local_information()
    {
        HttpRequestMessage? seen = null;
        using var http = UpdateCheck.NewClient(Current, new StubHandler(request =>
        {
            seen = request;
            return Json("v1.0.0");
        }));

        await CheckAsync(http);

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal(UpdateCheck.LatestApiUrl, seen.RequestUri!.ToString());
        Assert.Null(seen.Content);
        // 除 GitHub API 强制的 User-Agent 外不带任何请求头 —— 这条就是"零本地信息"的护栏。
        Assert.Single(seen.Headers);
        Assert.Single(seen.Headers.UserAgent);
    }

    private static Task<UpdateCheck.Result> CheckAsync(HttpClient http) => UpdateCheck.RunAsync(http, Current);

    private static HttpClient ClientReturning(HttpResponseMessage response) =>
        UpdateCheck.NewClient(Current, new StubHandler(_ => response));

    private static HttpResponseMessage Json(string tag, bool draft = false, bool prerelease = false, string? url = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url ?? "https://github.com/sonder123i/keyhaven/releases/tag/v1.0.0"}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}}}"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_reply(request));
    }
}
