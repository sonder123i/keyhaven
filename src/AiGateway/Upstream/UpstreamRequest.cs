namespace AiGateway.Upstream;

public static class UpstreamRequest
{
    public const string ClientName = "aigw-upstream";

    public static readonly HttpRequestOptionsKey<string> ProviderId = new("aigw.provider-id");

    // OpenAI-compatible upstreams expect Authorization: Bearer; Anthropic-compatible ones expect x-api-key.
    // Set by the forwarder that knows the upstream kind; the injector stays a single, kind-agnostic place that decrypts.
    public static readonly HttpRequestOptionsKey<bool> BearerAuth = new("aigw.bearer-auth");
}
