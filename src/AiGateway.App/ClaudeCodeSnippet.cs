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

    // 设置页用：只给地址那半段。token 明文按 D8 只在生成时显示一次，不在这里回显。
    public static string AddressOnly(Uri gatewayAddress) => $$"""
        {
          "env": {
            "ANTHROPIC_BASE_URL": "{{BaseUrl(gatewayAddress)}}"
          }
        }
        """;
}
