using AiGateway.ProxyKeys;
using AiGateway.RunLog;
using Microsoft.AspNetCore.Http;

namespace AiGateway.Hosting;

// Per-request collection point: auth and the forwarder fill it in, ObserveRun finalises it into the ring.
// It must never hold request/response objects, headers, bodies or error objects.
internal sealed class RunRequestState
{
    private const string ItemKey = "aigw.run";

    public ProxyKeyFingerprint? Fingerprint { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public TimeSpan? FirstByte { get; set; }

    public RunLogOutcome? Outcome { get; set; }

    public static RunRequestState? For(HttpContext context)
    {
        if (!context.Items.TryGetValue(ItemKey, out var existing) || existing is null)
        {
            existing = new RunRequestState();
            context.Items[ItemKey] = existing;
        }

        return (RunRequestState)existing;
    }
}
