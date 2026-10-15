using AiGateway.Configuration;

namespace AiGateway.App;

/// <summary>
/// 一个厂家的填写预设：只放"类型 + 地址 + 示例模型名"，不放 Key、不放推广链接。
/// 地址只写已核实过的；未核实的在 Note 里明标，别让用户以为我们验过。
/// 模型名刻意不预填进输入框（只做示例提示）—— 上游改名的速度比发版快。
/// </summary>
internal sealed record ProviderPreset(string Name, UpstreamKind Kind, string BaseUrl, string ExampleModel, string Note);

internal static class ProviderPresets
{
    // 只收厂家本体。cc-switch 那张 60+ 家的表里绝大多数是二次分销的中转商，
    // 且带联盟推广链接（?aff= / /invite/）—— 那两类一律不进来。
    internal static readonly IReadOnlyList<ProviderPreset> All =
    [
        new("DeepSeek（Anthropic 兼容，直通）", UpstreamKind.Anthropic, "https://api.deepseek.com/anthropic",
            "deepseek-flash", "已真机实测（M1，evidence.md §7）"),
        new("DeepSeek（OpenAI 兼容，翻译）", UpstreamKind.OpenAI, "https://api.deepseek.com",
            "deepseek-flash", "地址按官方文档（/v1 可省）；翻译路径未实测"),
        new("小米 MiMo（OpenAI 兼容，翻译）", UpstreamKind.OpenAI, "https://api.xiaomimimo.com/v1",
            "mimo-v2.6-pro", "已真机实测（M2，evidence.md §9）"),
        new("小米 MiMo（Anthropic 兼容，直通）", UpstreamKind.Anthropic, "https://api.xiaomimimo.com/anthropic",
            "mimo-v2.6-pro", "地址按官方文档；直通路径未实测"),
        new("Anthropic 官方（直通）", UpstreamKind.Anthropic, "https://api.anthropic.com",
            "", "通用约定地址；本机未实测。模型名以官方当前文档为准"),
        new("OpenAI 官方（翻译）", UpstreamKind.OpenAI, "https://api.openai.com/v1",
            "", "通用约定地址；本机未实测。模型名以官方当前文档为准"),
    ];
}