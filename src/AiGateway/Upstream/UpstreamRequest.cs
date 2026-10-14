namespace AiGateway.Upstream;

public static class UpstreamRequest
{
    public const string ClientName = "aigw-upstream";

    public static readonly HttpRequestOptionsKey<string> ProviderId = new("aigw.provider-id");
}
