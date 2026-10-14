using AiGateway.Configuration;

namespace AiGateway.Settings;

// The UI-facing port. Validation messages are user-facing (Chinese) because the WPF app shows them verbatim.
public interface IGatewaySettings
{
    int Port { get; }

    IReadOnlyList<ProviderConfig> Providers { get; }

    ActiveSelection? Active { get; }

    string AddProvider(string name, UpstreamKind kind, string baseUrl, IReadOnlyList<string> models);

    void UpdateProvider(ProviderConfig provider);

    bool RemoveProvider(string providerId);

    void Activate(string providerId, string model);

    void SetPort(int port);
}
