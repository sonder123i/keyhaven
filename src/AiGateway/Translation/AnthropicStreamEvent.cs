using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiGateway.Translation;

// One Anthropic SSE frame: a named event plus its JSON payload.
internal sealed record AnthropicStreamEvent(string Name, JsonObject Payload)
{
    // Relaxed escaping keeps Chinese text readable on the wire instead of \uXXXX (same choice as the request/response translators).
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToSse() => $"event: {Name}\ndata: {Payload.ToJsonString(Json)}\n\n";
}