using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.RunLog;
using Microsoft.AspNetCore.Http;

namespace AiGateway.Hosting;

// The shared plumbing both forwarders used to duplicate verbatim (S14 review): read the inbound body as a JSON
// object, stamp a run-log outcome, and answer in the one error shape Claude Code understands. Kept as static
// helpers rather than a base class on purpose — the forwarders' constructors are pinned by a guardrail, and the
// two paths stay readable on their own.
internal static class UpstreamForwarding
{
    public static void Mark(HttpContext context, RunLogOutcome outcome) =>
        RunRequestState.For(context)!.Outcome = outcome;

    public static async Task<JsonObject?> ReadJsonObject(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        try
        {
            return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Claude Code retries 5xx for about three minutes before showing anything (S8 measured 174 s); a gateway
    // configuration problem is not transient, so say so and let the message reach the user at once.
    public static Task Fail(HttpContext context, int statusCode, string type, string message, bool retryable = false)
    {
        if (!retryable)
        {
            context.Response.Headers["x-should-retry"] = "false";
        }

        return AnthropicErrors.Create(statusCode, type, message).ExecuteAsync(context);
    }
}