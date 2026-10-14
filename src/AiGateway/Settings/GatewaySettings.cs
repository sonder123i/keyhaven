using System.Text.RegularExpressions;
using AiGateway.Configuration;

namespace AiGateway.Settings;

internal sealed partial class GatewaySettings(ConfigStore store) : IGatewaySettings
{
    public int Port => store.Current.Port;

    public IReadOnlyList<ProviderConfig> Providers => store.Current.Providers;

    public ActiveSelection? Active => store.Current.Active;

    public string AddProvider(string name, UpstreamKind kind, string baseUrl, IReadOnlyList<string> models)
    {
        var id = string.Empty;
        store.Update(config =>
        {
            var provider = Validated(new ProviderConfig(UniqueId(name, config.Providers), name, kind, baseUrl, models));
            id = provider.Id;
            return config with { Providers = [.. config.Providers, provider] };
        });
        return id;
    }

    public void UpdateProvider(ProviderConfig provider) =>
        store.Update(config =>
        {
            if (!config.Providers.Any(p => p.Id == provider.Id))
            {
                throw new ArgumentException("找不到要修改的上游，它可能已被删除。");
            }

            var valid = Validated(provider);
            return config with { Providers = [.. config.Providers.Select(p => p.Id == valid.Id ? valid : p)] };
        });

    public bool RemoveProvider(string providerId)
    {
        var removed = false;
        store.Update(config =>
        {
            removed = config.Providers.Any(p => p.Id == providerId);
            return removed
                ? config with
                {
                    Providers = [.. config.Providers.Where(p => p.Id != providerId)],
                    Secrets = [.. config.Secrets.Where(s => s.ProviderId != providerId)],
                    Active = config.Active?.ProviderId == providerId ? null : config.Active,
                }
                : config;
        });
        return removed;
    }

    public void Activate(string providerId, string model) =>
        store.Update(config =>
        {
            if (!config.Providers.Any(p => p.Id == providerId))
            {
                throw new ArgumentException("找不到这个上游，它可能已被删除。");
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("请选择或填写模型名。");
            }

            if (!config.Secrets.Any(s => s.ProviderId == providerId))
            {
                throw new ArgumentException("这个上游还没有保存 Key，请先在「上游与 Key」里保存 Key 再切换。");
            }

            return config with { Active = new ActiveSelection(providerId, model.Trim()) };
        });

    public void SetPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须在 1 到 65535 之间。");
        }

        store.Update(config => config with { Port = port });
    }

    private static ProviderConfig Validated(ProviderConfig provider)
    {
        var name = provider.Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("请填写名称。");
        }

        if (!Uri.TryCreate(provider.BaseUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("地址必须是完整的网址，例如 https://api.deepseek.com/anthropic");
        }

        // Plain http would put the real key on the network in clear text; only a same-machine upstream may skip TLS.
        if (url.Scheme == "http" && !url.IsLoopback)
        {
            throw new ArgumentException("本机以外的地址必须用 https，否则 Key 会以明文在网络上传输。");
        }

        var models = provider.Models.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();
        if (models.Count == 0)
        {
            throw new ArgumentException("至少填写一个模型名。");
        }

        return provider with { Name = name, BaseUrl = url.ToString().TrimEnd('/'), Models = models };
    }

    // Ids are minted once and never change: the DPAPI entropy of a stored key is derived from its provider id.
    private static string UniqueId(string name, IReadOnlyList<ProviderConfig> existing)
    {
        var slug = NonSlugRun().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "provider";
        }

        var id = slug;
        for (var n = 2; existing.Any(p => p.Id == id); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugRun();
}
