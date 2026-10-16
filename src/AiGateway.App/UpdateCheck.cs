using System.Net.Http;
using System.Text.Json;

namespace AiGateway.App;

// 更新只做「提示」不做「自更」：这个工具的卖点是真 Key 只留本机，替用户自动执行从网络拉回的字节自相矛盾。
// 说清楚代价：这是本应用唯一一处非上游外呼 —— 匿名 GET GitHub Releases，零请求体，
// 只带 GitHub API 强制要求的 User-Agent，不带任何本地信息。
internal static class UpdateCheck
{
    internal const string LatestApiUrl = "https://api.github.com/repos/sonder123i/keyhaven/releases/latest";

    internal static readonly Uri DownloadPage = new("https://github.com/sonder123i/keyhaven/releases/latest");

    internal enum Status
    {
        UpToDate,
        UpdateAvailable,
        Failed,
    }

    internal sealed record Result(Status Status, string LatestVersion, Uri? ReleaseUrl);

    // handler 可注入：测试要能验证「请求长什么样」，又不打真网络。
    internal static HttpClient NewClient(Version current, HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Keyhaven/{current.ToString(3)}");
        return http;
    }

    // 任何网络 / 解析故障都退化成 Failed 而不是异常：启动时的静默检查绝不能变成弹窗或崩溃。
    internal static async Task<Result> RunAsync(HttpClient http, Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(LatestApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new Result(Status.Failed, string.Empty, null);
            }

            using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            // 渠道只有一条 stable：draft / prerelease 不算新版。
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return new Result(Status.UpToDate, string.Empty, null);
            }

            var tag = root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
            var url = root.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.GetString() is { } link
                && Uri.TryCreate(link, UriKind.Absolute, out var parsed)
                ? parsed
                : DownloadPage;

            if (!TryParseVersion(tag, out var latest))
            {
                return new Result(Status.UpToDate, string.Empty, null);
            }

            return IsNewer(latest, current)
                ? new Result(Status.UpdateAvailable, latest.ToString(3), url)
                : new Result(Status.UpToDate, latest.ToString(3), null);
        }
        catch (Exception)
        {
            return new Result(Status.Failed, string.Empty, null);
        }
    }

    internal static bool IsNewerThan(string? tag, Version current) =>
        TryParseVersion(tag, out var latest) && IsNewer(latest, current);

    // tag 形如 v1.2.3（1~3 段数字，v 前缀可省）；看不出来的一律当"没有新版"，不猜。
    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag!.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    // 程序集版本是 1.0.0.0（四段），tag 是 1.0.0（三段）——都归一成三段再比，
    // 否则 Version 的 -1/0 修订位会把相等说成"当前更新"。
    private static bool IsNewer(Version latest, Version current) =>
        new Version(latest.Major, latest.Minor, latest.Build)
        > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
}
