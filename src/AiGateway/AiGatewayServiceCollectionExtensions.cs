using AiGateway.Configuration;
using AiGateway.Passthrough;
using AiGateway.ProxyKeys;
using AiGateway.Routing;
using AiGateway.RunLog;
using AiGateway.Secrets;
using AiGateway.Settings;
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
        services.AddSingleton<RunLogRing>();
        services.AddSingleton<IRunLog>(sp => sp.GetRequiredService<RunLogRing>());
        services.AddSingleton<PassthroughForwarder>();
        services.AddSingleton<IGatewaySettings, GatewaySettings>();

        // Never follow redirects: a redirect to another host would carry the injected x-api-key header there.
        // Remove the client's built-in loggers: at Trace level they record request headers, and RedactLoggedHeaders only
        // masks the formatted text — the structured log state still carries the real key verbatim (both measured).
        // No client-side timeout: the default 100 s would cut off long non-streaming answers; cancellation comes from the caller.
        services.AddHttpClient(UpstreamRequest.ClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        return services;
    }
}
