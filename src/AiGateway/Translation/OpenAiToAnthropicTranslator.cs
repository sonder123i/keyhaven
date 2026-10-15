using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiGateway.Translation;

// Non-streaming OpenAI chat.completion → Anthropic message.
// Returns null when the upstream returned no choices at all: that is not a message, and the caller turns it into an error.
internal sealed class OpenAiToAnthropicTranslator
{
    public JsonObject? TranslateMessage(JsonObject completion, string model)
    {
        if (completion["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
        {
            return null;
        }

        var message = choice["message"] as JsonObject ?? new JsonObject();
        var content = new JsonArray();

        if (message["content"]?.GetValue<string>() is { Length: > 0 } text)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call["id"]?.GetValue<string>() ?? $"toolu_{content.Count}",
                    ["name"] = call["function"]?["name"]?.GetValue<string>() ?? string.Empty,
                    ["input"] = Arguments(call["function"]?["arguments"]),
                });
            }
        }

        var result = new JsonObject
        {
            ["id"] = MessageId(completion["id"]?.GetValue<string>()),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = completion["model"]?.GetValue<string>() ?? model,
            ["content"] = content,
            ["stop_reason"] = StopReason(choice["finish_reason"]?.GetValue<string>()),
            ["stop_sequence"] = null,
        };

        if (completion["usage"] is JsonObject usage)
        {
            result["usage"] = new JsonObject
            {
                ["input_tokens"] = usage["prompt_tokens"]?.GetValue<long>() ?? 0,
                ["output_tokens"] = usage["completion_tokens"]?.GetValue<long>() ?? 0,
            };
        }

        return result;
    }

    // Arguments arrive as a JSON string. A truncated or malformed one becomes an empty object rather than a crash —
    // the tool call still reaches the client, just with nothing to run.
    private static JsonNode Arguments(JsonNode? arguments)
    {
        if (arguments?.GetValue<string>() is not { Length: > 0 } text)
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string MessageId(string? openAiId) =>
        string.IsNullOrEmpty(openAiId) ? "msg_" + Guid.NewGuid().ToString("N") : "msg_" + openAiId;

    private static string StopReason(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        _ => "end_turn",
    };
}