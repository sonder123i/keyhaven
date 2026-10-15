using System.Reflection;
using System.Text.Json.Nodes;
using AiGateway.App;
using AiGateway.App.ViewModels;
using AiGateway.Configuration;
using AiGateway.ProxyKeys;
using AiGateway.RunLog;
using AiGateway.Secrets;
using AiGateway.Security;
using AiGateway.Settings;
using AiGateway.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public sealed class ViewModelTests : IDisposable
{
    private const string RealKey = "sk-real-SHOULD-NOT-LINGER-0123456789";
    private static readonly Uri Gateway = new("http://127.0.0.1:8000/");

    private readonly TempDirectory _dir = new();
    private readonly ServiceProvider _services;

    public ViewModelTests()
    {
        _services = new ServiceCollection()
            .AddAiGateway(_dir.File("config.json"))
            .AddAiGatewaySecurity()
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _dir.Dispose();
    }

    private IGatewaySettings Settings => _services.GetRequiredService<IGatewaySettings>();

    private ProvidersViewModel NewProviders(Action? changed = null) =>
        new(Settings, _services.GetRequiredService<IRealKeyVault>(), _services.GetRequiredService<IUpstreamDiagnostics>(), changed ?? (() => { }));

    private static bool Retains(object root, string secret)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        bool Walk(object? value, int depth)
        {
            if (value is null || depth > 8)
            {
                return false;
            }

            if (value is string text)
            {
                return text.Contains(secret);
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is MemberInfo || value is Pointer || (!type.IsValueType && !seen.Add(value)))
            {
                return false;
            }

            if (value is System.Collections.IEnumerable items && items.Cast<object?>().Any(item => Walk(item, depth + 1)))
            {
                return true;
            }

            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsPointer && Walk(field.GetValue(value), depth + 1))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return Walk(root, 0);
    }

    private sealed class Planted
    {
        private readonly List<object> _inner = [new { deep = new[] { RealKey } }];

        public int Count => _inner.Count;
    }

    [Fact]
    public void The_retention_scan_finds_a_secret_that_really_is_there()
    {
        Assert.True(Retains(new Planted(), RealKey));
    }

    [Fact]
    public void Saving_a_key_stores_it_and_leaves_no_trace_in_the_view_model()
    {
        var vm = NewProviders();
        vm.Name = "DeepSeek";
        vm.BaseUrl = "https://api.deepseek.com/anthropic";
        vm.Models = "deepseek-chat, deepseek-reasoner";
        vm.Save();

        vm.SaveKey(RealKey);

        Assert.True(_services.GetRequiredService<IRealKeyVault>().Exists("deepseek"));
        Assert.Equal("已保存", Assert.Single(vm.Rows).KeyText);
        Assert.False(Retains(vm, RealKey));
    }

    [Fact]
    public void Invalid_input_is_explained_and_nothing_is_saved()
    {
        var vm = NewProviders();
        vm.Name = "Remote over http";
        vm.BaseUrl = "http://api.deepseek.com/anthropic";
        vm.Models = "m";

        vm.Save();

        Assert.Contains("https", vm.Message);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Deleting_a_provider_deletes_its_key_and_clears_the_form()
    {
        var vm = NewProviders();
        vm.Name = "DeepSeek";
        vm.BaseUrl = "https://api.deepseek.com/anthropic";
        vm.Models = "deepseek-chat";
        vm.Save();
        vm.SaveKey(RealKey);

        vm.Delete();

        Assert.Empty(vm.Rows);
        Assert.False(_services.GetRequiredService<IRealKeyVault>().Exists("deepseek"));
        Assert.Equal((string.Empty, false), (vm.Name, vm.CanSaveKey));
    }

    [Fact]
    public void Switching_needs_a_key_first_and_then_takes_effect()
    {
        var activation = new ActivationViewModel(Settings, _services.GetRequiredService<IProxyKeyRegistry>());
        var providers = NewProviders(activation.Refresh);
        providers.Name = "DeepSeek";
        providers.BaseUrl = "https://api.deepseek.com/anthropic";
        providers.Models = "deepseek-chat, deepseek-reasoner";
        providers.Save();

        Assert.Equal("deepseek", activation.SelectedProvider?.Id);
        activation.Activate();
        Assert.Contains("Key", activation.Message);
        Assert.Null(Settings.Active);

        providers.SaveKey(RealKey);
        Assert.Equal(string.Empty, activation.Message);
        activation.SelectedModel = "deepseek-reasoner";
        activation.Activate();

        Assert.Equal(new ActiveSelection("deepseek", "deepseek-reasoner"), Settings.Active);
        Assert.Contains("DeepSeek · deepseek-reasoner", activation.CurrentText);
        Assert.Equal(string.Empty, activation.Message);
    }

    [Fact]
    public void Proxy_key_list_shows_only_fingerprints_and_revocation_status()
    {
        var vm = new ProxyKeysViewModel(_services.GetRequiredService<IProxyKeyRegistry>(), Gateway);

        Assert.Null(vm.Generate());
        Assert.Contains("标签", vm.Message);

        vm.NewLabel = "笔记本";
        var issued = vm.Generate()!;

        var row = Assert.Single(vm.Rows);
        Assert.Equal((issued.Fingerprint.Value, "可用"), (row.Fingerprint, row.StatusText));
        Assert.False(Retains(vm, issued.Plaintext));

        vm.Selected = row;
        vm.Revoke();
        Assert.Equal("已吊销", Assert.Single(vm.Rows).StatusText);

        vm.Selected = vm.Rows[0];
        vm.Delete();
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Snippet_is_valid_json_pointing_at_the_gateway_without_a_trailing_slash()
    {
        var json = JsonNode.Parse(ClaudeCodeSnippet.For(Gateway, "sk-aigw-abc"))!["env"]!;

        Assert.Equal("http://127.0.0.1:8000", json["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("sk-aigw-abc", json["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
    }

    [Fact]
    public void The_run_log_page_shows_fingerprint_and_outcome_but_never_a_key()
    {
        var issued = _services.GetRequiredService<IProxyKeyRegistry>().Create("笔记本");
        var ring = new RunLogRing();
        ring.Append(new RunLogEntry(
            DateTimeOffset.UtcNow, issued.Fingerprint, "deepseek", "deepseek-chat", 200,
            TimeSpan.FromMilliseconds(900), RunLogOutcome.ClientAborted));
        var vm = new RunLogViewModel(ring);

        vm.Refresh();

        var row = Assert.Single(vm.Rows);
        Assert.Equal((issued.Fingerprint.Value, "deepseek", "deepseek-chat", "200", "900ms", "客户端中止"),
            (row.Fingerprint, row.Provider, row.Model, row.Status, row.FirstByte, row.Outcome));
        Assert.DoesNotContain(issued.Plaintext, string.Join('|', vm.Rows.Select(r => r.ToString())));
        Assert.False(Retains(vm, issued.Plaintext));
    }

    [Theory]
    [InlineData("abc", "数字")]
    [InlineData("70000", "65535")]
    [InlineData("8000", "相同")]
    [InlineData("8123", "下次启动")]
    public void Port_setting_explains_what_happens(string input, string messageMentions)
    {
        var vm = new SettingsViewModel(Settings, Gateway) { PortText = input };

        vm.Save();

        Assert.Contains(messageMentions, vm.Message);
    }

    [Fact]
    public void Picking_a_preset_fills_kind_and_address_but_leaves_the_model_to_the_user()
    {
        var vm = NewProviders();

        vm.SelectedPreset = DeepSeekPreset(vm);

        Assert.Equal(UpstreamKind.Anthropic, vm.Kind);
        Assert.Equal("https://api.deepseek.com/anthropic", vm.BaseUrl);
        Assert.Equal("DeepSeek", vm.Name);
        // 模型名不预填；提示里给示例，并带上"实测状态"那句话
        Assert.Empty(vm.Models);
        Assert.Contains("deepseek-flash", vm.ModelHint);
        Assert.Contains("实测", vm.ModelHint);
    }

    [Fact]
    public void An_empty_list_shows_the_guided_state_instead_of_a_blank_table()
    {
        var vm = NewProviders();

        Assert.True(vm.IsEmpty);

        vm.SelectedPreset = DeepSeekPreset(vm);
        vm.Models = "deepseek-flash";
        vm.Save();

        Assert.False(vm.IsEmpty);
        Assert.Equal("已添加。下一步：在下面保存这个上游的 Key。", vm.Message);
    }

    [Fact]
    public void The_top_badge_flips_to_attention_until_something_is_activated()
    {
        var activation = new ActivationViewModel(Settings, _services.GetRequiredService<IProxyKeyRegistry>());

        Assert.False(activation.IsActive);
        Assert.Contains("尚未激活", activation.CurrentText);
        // 一把代理密钥都没有时，顶部那句必须说出来 —— 否则客户端连不上而界面一片安静
        Assert.Contains("还没有生成代理密钥", activation.ProxyKeyText);

        _services.GetRequiredService<IProxyKeyRegistry>().Create("笔记本");
        activation.RefreshKeyHint();
        Assert.Contains("1 把可用", activation.ProxyKeyText);
    }

    [Fact]
    public void A_failed_switch_says_it_did_not_take_effect()
    {
        var activation = new ActivationViewModel(Settings, _services.GetRequiredService<IProxyKeyRegistry>());
        var providers = NewProviders(activation.Refresh);
        providers.SelectedPreset = DeepSeekPreset(providers);
        providers.Models = "deepseek-flash";
        providers.Save();

        // 还没存 Key，切换必须被拒 —— 而且要说清"这一步没生效"
        activation.SelectedModel = "deepseek-flash";
        activation.Activate();

        Assert.Null(Settings.Active);
        Assert.True(activation.HasMessage);
        Assert.Contains("这一步没生效", activation.Message);
        Assert.False(activation.IsActive);
    }

    private static PresetOption DeepSeekPreset(ProvidersViewModel vm) =>
        vm.Presets.Single(p => p.Preset?.Name.StartsWith("DeepSeek（Anthropic") == true);
}
