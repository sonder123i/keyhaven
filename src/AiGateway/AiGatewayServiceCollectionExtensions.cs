using AiGateway.Configuration;
using AiGateway.ProxyKeys;
using AiGateway.Secrets;
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
        return services;
    }
}
