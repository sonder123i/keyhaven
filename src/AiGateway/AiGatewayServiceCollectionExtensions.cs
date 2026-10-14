using AiGateway.Configuration;
using AiGateway.ProxyKeys;
using AiGateway.Routing;
using AiGateway.Secrets;
using AiGateway.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway;

public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddAiGateway(this IServiceCollection services, string? configPath = null)
    {
        services.AddSingleton(_ => new ConfigStore(configPath ?? ConfigStore.DefaultPath));
        return services.AddStoreDependents();
    }

    internal static IServiceCollection AddGatewayCore(this IServiceCollection services, ConfigStore store)
    {
        services.AddSingleton(store);
        return services.AddStoreDependents();
    }

    private static IServiceCollection AddStoreDependents(this IServiceCollection services)
    {
        services.AddSingleton<ICiphertextStore>(sp => sp.GetRequiredService<ConfigStore>());
        services.AddSingleton<ProxyKeyAuthenticator>();
        services.AddSingleton<IProxyKeyRegistry, ProxyKeyRegistry>();
        services.AddSingleton<RouteResolver>();

        // Never follow redirects: a redirect to another host would carry the injected x-api-key header there.
        // Remove the client's built-in loggers: at Trace level they record request headers, and RedactLoggedHeaders only
        // masks the formatted text — the structured log state still carries the real key verbatim (both measured).
        services.AddHttpClient(UpstreamRequest.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        return services;
    }
}
