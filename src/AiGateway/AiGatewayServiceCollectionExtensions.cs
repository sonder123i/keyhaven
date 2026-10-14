using AiGateway.Configuration;
using AiGateway.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway;

public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddAiGateway(this IServiceCollection services, string? configPath = null)
    {
        services.AddSingleton(_ => new ConfigStore(configPath ?? ConfigStore.DefaultPath));
        services.AddSingleton<ICiphertextStore>(sp => sp.GetRequiredService<ConfigStore>());
        return services;
    }

    internal static IServiceCollection AddGatewayCore(this IServiceCollection services, ConfigStore store)
    {
        services.AddSingleton(store);
        services.AddSingleton<ICiphertextStore>(store);
        return services;
    }
}
