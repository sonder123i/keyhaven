using AiGateway.App;
using AiGateway.Configuration;

namespace AiGateway.Tests;

// 预设表是纯数据，但它会进 UI、被用户当成"厂家官方给的地址"。
// 所以守三条：不能夹带推广/中转商的痕迹、地址必须是"API 根"而不是端点、必须 https（回环除外）。
public class ProviderPresetTests
{
    private static readonly string[] Forbidden = ["?aff=", "/invite/", "utm_", "ref=", "affiliate"];

    [Fact]
    public void Presets_exist_and_are_uniquely_named()
    {
        Assert.NotEmpty(ProviderPresets.All);
        Assert.Equal(ProviderPresets.All.Count, ProviderPresets.All.Select(p => p.Name).Distinct().Count());
        Assert.All(ProviderPresets.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            // 每条都要写明实测状态 —— 别让用户以为我们验过
            Assert.False(string.IsNullOrWhiteSpace(p.Note));
        });
    }

    [Fact]
    public void No_preset_carries_a_promotion_or_affiliate_trail()
    {
        foreach (var preset in ProviderPresets.All)
        {
            var haystack = $"{preset.Name} {preset.BaseUrl} {preset.ExampleModel} {preset.Note}".ToLowerInvariant();
            Assert.DoesNotContain(Forbidden, marker => haystack.Contains(marker));
        }
    }

    [Fact]
    public void Every_base_url_is_the_api_root_not_an_endpoint()
    {
        Assert.All(ProviderPresets.All, p =>
        {
            Assert.DoesNotContain("/chat/completions", p.BaseUrl);
            Assert.DoesNotContain("/v1/messages", p.BaseUrl);
        });
    }

    [Fact]
    public void Every_base_url_is_https_or_loopback()
    {
        Assert.All(ProviderPresets.All, p =>
        {
            Assert.True(Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url), $"{p.Name}: {p.BaseUrl} 不是绝对地址");
            Assert.True(url!.Scheme == "https" || url.IsLoopback, $"{p.Name}: 非回环地址必须 https");
        });
    }

    [Fact]
    public void Both_upstream_kinds_are_represented()
    {
        Assert.Contains(ProviderPresets.All, p => p.Kind == UpstreamKind.Anthropic);
        Assert.Contains(ProviderPresets.All, p => p.Kind == UpstreamKind.OpenAI);
    }

    // 模型名刻意不预填进输入框；这里只保证"给了示例的看起来像模型名"（不是 URL、不留空白）
    [Fact]
    public void Example_models_are_names_not_promises()
    {
        Assert.All(ProviderPresets.All.Where(p => p.ExampleModel.Length > 0), p =>
        {
            Assert.DoesNotContain("http", p.ExampleModel);
            Assert.Equal(p.ExampleModel.Trim(), p.ExampleModel);
        });
    }
}