using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiGateway.Configuration;

namespace AiGateway.Upstream;

// 只读诊断：探上游可达性、取可用模型清单。
//
// 两个方法都经"上游命名客户端"发出 —— 所以真实 Key 照旧只在注入处理器一处解密、只沿
// "上游客户端 → 上游"这一条边离开进程。带 Key 也让它比纯可达性检查多答一件事：
// 401/403 说明"地址通了、但凭据被拒"（cc-switch 的检查刻意不带 Key，答不了这个）。
//
// 无状态：任何每请求的东西都不许挂在字段上（单例 + 并发 = 串流，S12 的 RISK-05 同款）。
internal sealed partial class UpstreamDiagnostics(ConfigStore store, IHttpClientFactory clients) : IUpstreamDiagnostics
{
    // 上游客户端刻意没有超时（默认 100 秒会掐断长回答），所以诊断自己掐。
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<ReachabilityReport> ProbeAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var (target, why) = Find(providerId);
        if (target is null)
        {
            return new ReachabilityReport(false, null, null, why);
        }

        var watch = Stopwatch.StartNew();
        var (response, problem) = await SendAsync(target, new Uri(target.BaseUrl), cancellationToken);
        using (response)
        {
            return response is null
                ? new ReachabilityReport(false, null, null, problem)
                : new ReachabilityReport(true, (int)response.StatusCode, watch.Elapsed, DescribeByStatus(response.StatusCode));
        }
    }

    public async Task<ModelListReport> FetchModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var (target, why) = Find(providerId);
        if (target is null)
        {
            return new ModelListReport(false, [], why);
        }

        if (target.Kind != UpstreamKind.OpenAI)
        {
            return new ModelListReport(false, [], "只有 OpenAI 类型的上游支持按 /models 拉模型清单；其他类型请手填。");
        }

        foreach (var candidate in ModelEndpoints(target.BaseUrl))
        {
            var (response, _) = await SendAsync(target, candidate, cancellationToken);
            using (response)
            {
                if (response is null || !response.IsSuccessStatusCode)
                {
                    continue;
                }

                var models = await ReadModelIdsAsync(response, cancellationToken);
                if (models.Count > 0)
                {
                    return new ModelListReport(true, models, null);
                }
            }
        }

        return new ModelListReport(false, [], "这个地址下没有可用的模型清单端点（检查地址里是否漏了 /v1）。");
    }

    // 候选顺序借 cc-switch 的做法（它那张表要应付任意 base，我们要小得多）：
    // 地址已带版本段就只试 {base}/models，否则先 {base}/v1/models 再退到 {base}/models。
    private static IEnumerable<Uri> ModelEndpoints(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        if (VersionSegment().IsMatch(root))
        {
            yield return new Uri(root + "/models");
            yield break;
        }

        yield return new Uri(root + "/v1/models");
        yield return new Uri(root + "/models");
    }

    private async Task<(HttpResponseMessage? Response, string? Problem)> SendAsync(ProviderConfig provider, Uri target, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("accept", "application/json");

            request.Options.Set(UpstreamRequest.ProviderId, provider.Id);
            // 按上游类型选鉴权头 —— 和转发器同一套约定：OpenAI 类上游认 Bearer，Anthropic 类认 x-api-key。
            request.Options.Set(UpstreamRequest.BearerAuth, provider.Kind == UpstreamKind.OpenAI);
            var response = await clients.CreateClient(UpstreamRequest.ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            return (response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"上游 {Budget.TotalSeconds:0} 秒内没有响应头。");
        }
        catch (CryptographicException)
        {
            return (null, $"为 '{provider.Id}' 存的 Key 在当前 Windows 账户下解不开，请重新录入。");
        }
        catch (InvalidOperationException ex)
        {
            // 注入处理器在"没设 provider id"或"没存 Key"时抛这个 —— 文案本来就写给人看。
            return (null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"连不上 {target.Host}：{ex.HttpRequestError}");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadModelIdsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(payload)?["data"] is not JsonArray data
                ? []
                : [.. data.OfType<JsonObject>()
                    .Select(m => m["id"]?.GetValue<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? DescribeByStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "地址通了，但凭据被拒（上游不认这个 Key）。",
        _ => null,
    };

    // 返回"找到的上游"或"为什么没有" —— 不把问题写进字段：单例 + 并发会把两个请求的
    // 原因串在一起（S12 的 RISK-05 同款）。
    private (ProviderConfig? Provider, string? Problem) Find(string providerId)
    {
        var config = store.Current;
        var provider = config.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            return (null, $"找不到上游 '{providerId}'，它可能已被删除。");
        }

        return config.Secrets.Any(s => s.ProviderId == providerId)
            ? (provider, null)
            : (null, $"上游 '{providerId}' 还没保存 Key，先保存再诊断。");
    }

    [GeneratedRegex(@"/v\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSegment();
}