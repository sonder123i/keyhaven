namespace AiGateway.App;

internal static class ClaudeCodeSnippet
{
    public static string BaseUrl(Uri gatewayAddress) => gatewayAddress.GetLeftPart(UriPartial.Authority);

    public static string For(Uri gatewayAddress, string proxyKey) => $$"""
        {
          "env": {
            "ANTHROPIC_BASE_URL": "{{BaseUrl(gatewayAddress)}}",
            "ANTHROPIC_AUTH_TOKEN": "{{proxyKey}}"
          }
        }
        """;
}
