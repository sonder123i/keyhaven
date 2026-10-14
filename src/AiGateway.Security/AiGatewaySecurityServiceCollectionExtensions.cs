using AiGateway.Secrets;
using AiGateway.Upstream;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Security;

public static class AiGatewaySecurityServiceCollectionExtensions
{
    public static IServiceCollection AddAiGatewaySecurity(this IServiceCollection services)
    {
        services.AddSingleton<IRealKeyVault, RealKeyVault>();
        services.AddTransient<CredentialInjectingHandler>();
        services.AddHttpClient(UpstreamRequest.ClientName).AddHttpMessageHandler<CredentialInjectingHandler>();
        return services;
    }
}
