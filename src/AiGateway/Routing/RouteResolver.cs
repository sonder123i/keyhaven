using AiGateway.Configuration;

namespace AiGateway.Routing;

internal sealed record UpstreamRoute(string ProviderId, UpstreamKind Kind, Uri BaseUrl, string Model);

internal sealed record RouteResult(UpstreamRoute? Route, string? Problem)
{
    public static RouteResult Found(UpstreamRoute route) => new(route, null);

    public static RouteResult Unavailable(string problem) => new(null, problem);
}

internal sealed class RouteResolver(ConfigStore store)
{
    public RouteResult Resolve()
    {
        var config = store.Current;

        if (config.Active is not { } active)
        {
            return RouteResult.Unavailable("No upstream is active in the gateway.");
        }

        var provider = config.Providers.FirstOrDefault(p => p.Id == active.ProviderId);
        if (provider is null)
        {
            return RouteResult.Unavailable($"The active provider '{active.ProviderId}' is not configured.");
        }

        if (!config.Secrets.Any(s => s.ProviderId == provider.Id))
        {
            return RouteResult.Unavailable($"No key is stored for the active provider '{provider.Id}'.");
        }

        return RouteResult.Found(new UpstreamRoute(provider.Id, provider.Kind, new Uri(provider.BaseUrl), active.Model));
    }
}
