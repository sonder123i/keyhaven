using AiGateway.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Security;

public static class AiGatewaySecurityServiceCollectionExtensions
{
    public static IServiceCollection AddAiGatewaySecurity(this IServiceCollection services)
    {
        services.AddSingleton<IRealKeyVault, RealKeyVault>();
        return services;
    }
}
