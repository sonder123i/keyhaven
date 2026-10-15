namespace AiGateway.Upstream;

// 只读诊断端口（给 UI 用）：探上游可达性、取可用模型清单。
// 两个方法都经"上游命名客户端"发出，所以真实 Key 照旧只在注入处理器一处解密、
// 只沿"上游客户端 → 上游"这一条边离开进程 —— 本端口自己拿不到明文。
public interface IUpstreamDiagnostics
{
    Task<ReachabilityReport> ProbeAsync(string providerId, CancellationToken cancellationToken = default);

    Task<ModelListReport> FetchModelsAsync(string providerId, CancellationToken cancellationToken = default);
}

// 可达 ≠ 配置正确：这份报告只回答"能不能到"，顺带用状态码说一句"凭据有没有被拒"。
public sealed record ReachabilityReport(bool Reachable, int? StatusCode, TimeSpan? RoundTrip, string? Problem);

// Supported=false 表示"这个上游不支持按 /models 拉"，不是"出错了" —— 两者文案不同。
public sealed record ModelListReport(bool Supported, IReadOnlyList<string> Models, string? Problem);