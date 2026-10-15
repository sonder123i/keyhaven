using AiGateway.Configuration;

namespace AiGateway.Routing;

internal sealed record UpstreamRoute(string ProviderId, UpstreamKind Kind, Uri BaseUrl, string Model);

// ProviderId travels with the result so callers can label a request from the same snapshot the routing used,
// including when there is no route at all — no second read of the configuration.
internal sealed record RouteResult(UpstreamRoute? Route, string? ProviderId, string? Problem)
{
    public static RouteResult Found(UpstreamRoute route) => new(route, route.ProviderId, null);

    public static RouteResult Unavailable(string? providerId, string problem) => new(null, providerId, problem);
}

internal sealed class RouteResolver(ConfigStore store)
{
    public RouteResult Resolve()
    {
        var config = store.Current;

        if (config.Active is not { } active)
        {
            return RouteResult.Unavailable(null, "No upstream is active in the gateway.");
        }

        var provider = config.Providers.FirstOrDefault(p => p.Id == active.ProviderId);
        if (provider is null)
        {
            return RouteResult.Unavailable(active.ProviderId, $"The active provider '{active.ProviderId}' is not configured.");
        }

        if (!config.Secrets.Any(s => s.ProviderId == provider.Id))
        {
            return RouteResult.Unavailable(provider.Id, $"No key is stored for the active provider '{provider.Id}'.");
        }

        return RouteResult.Found(new UpstreamRoute(provider.Id, provider.Kind, new Uri(provider.BaseUrl), active.Model));
    }
}
