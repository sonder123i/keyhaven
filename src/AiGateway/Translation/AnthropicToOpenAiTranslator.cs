using System.Text.Json.Nodes;

namespace AiGateway.Translation;

// Anthropic Messages request → OpenAI chat/completions request.
// D4: this path only guarantees tool_use. thinking, cache_control, metadata and anything else Anthropic-only is dropped,
// because an OpenAI upstream would either reject it or silently ignore it — passing it through would be a lie.
internal sealed class AnthropicToOpenAiTranslator
{
    public JsonObject TranslateRequest(JsonObject anthropic, string model)
    {
        var openAi = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(anthropic),
        };

        Copy(anthropic, openAi, "max_tokens");
        Copy(anthropic, openAi, "temperature");
        Copy(anthropic, openAi, "top_p");
        Copy(anthropic, openAi, "stop_sequences", "stop");

        if (anthropic["tools"] is JsonArray { Count: > 0 } tools)
        {
            openAi["tools"] = TranslateTools(tools);
        }

        if (anthropic["tool_choice"] is JsonObject choice && TranslateToolChoice(choice) is { } toolChoice)
        {
            openAi["tool_choice"] = toolChoice;
        }

        if (anthropic["stream"]?.GetValue<bool>() == true)
        {
            openAi["stream"] = true;
            // RISK-03: without include_usage an OpenAI stream carries no usage at all, so message_delta would have to invent it.
            openAi["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return openAi;
    }

    private static JsonArray BuildMessages(JsonObject anthropic)
    {
        var messages = new JsonArray();

        if (TextOf(anthropic["system"]) is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        if (anthropic["messages"] is JsonArray turns)
        {
            foreach (var turn in turns.OfType<JsonObject>())
            {
                AppendTurn(messages, turn);
            }
        }

        return messages;
    }

    private static void AppendTurn(JsonArray messages, JsonObject turn)
    {
        var role = turn["role"]?.GetValue<string>();

        switch (turn["content"])
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
                break;
            case JsonArray blocks when role == "assistant":
                AppendAssistant(messages, blocks);
                break;
            case JsonArray blocks:
                AppendUser(messages, blocks);
                break;
        }
    }

    private static void AppendAssistant(JsonArray messages, JsonArray blocks)
    {
        var text = TextOf(blocks);
        var toolCalls = new JsonArray();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            if (block["type"]?.GetValue<string>() != "tool_use")
            {
                continue;
            }

            toolCalls.Add(new JsonObject
            {
                ["id"] = block["id"]?.GetValue<string>() ?? $"call_{toolCalls.Count}",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = block["name"]?.GetValue<string>() ?? string.Empty,
                    // OpenAI carries arguments as a JSON string, not an object.
                    ["arguments"] = block["input"]?.ToJsonString() ?? "{}",
                },
            });
        }

        if (text.Length == 0 && toolCalls.Count == 0)
        {
            return;
        }

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? JsonValue.Create(text) : null,
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        messages.Add(message);
    }

    private static void AppendUser(JsonArray messages, JsonArray blocks)
    {
        var parts = new JsonArray();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch (block["type"]?.GetValue<string>())
            {
                case "tool_result":
                    // One Anthropic tool_result becomes one OpenAI tool message, keyed back by tool_call_id.
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = block["tool_use_id"]?.GetValue<string>() ?? string.Empty,
                        ["content"] = TextOf(block["content"]),
                    });
                    break;
                case "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = block["text"]?.GetValue<string>() ?? string.Empty });
                    break;
                case "image" when ImageUrl(block) is { } url:
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        JsonNode content = parts.Count == 1 && parts[0] is JsonObject { } only && only["type"]?.GetValue<string>() == "text"
            ? JsonValue.Create(only["text"]?.GetValue<string>() ?? string.Empty)!
            : parts;
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
    }

    private static JsonArray TranslateTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool["name"]?.GetValue<string>() ?? string.Empty,
                    ["description"] = tool["description"]?.GetValue<string>() ?? string.Empty,
                    ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                },
            });
        }

        return result;
    }

    private static JsonNode? TranslateToolChoice(JsonObject choice) => choice["type"]?.GetValue<string>() switch
    {
        "auto" => JsonValue.Create("auto"),
        "any" => JsonValue.Create("required"),
        "none" => JsonValue.Create("none"),
        "tool" => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = choice["name"]?.GetValue<string>() ?? string.Empty },
        },
        _ => null,
    };

    private static string? ImageUrl(JsonObject image) => image["source"] switch
    {
        JsonObject { } source => source["type"]?.GetValue<string>() switch
        {
            "base64" => $"data:{source["media_type"]?.GetValue<string>()};base64,{source["data"]?.GetValue<string>()}",
            "url" => source["url"]?.GetValue<string>(),
            _ => null,
        },
        _ => null,
    };

    // Text out of a string or a block array; non-text blocks are dropped (that is where thinking / documents go).
    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n\n", blocks.OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "text")
            .Select(b => b["text"]?.GetValue<string>() ?? string.Empty)
            .Where(t => t.Length > 0)),
        _ => string.Empty,
    };

    private static void Copy(JsonObject from, JsonObject to, string name, string? asName = null)
    {
        if (from.TryGetPropertyValue(name, out var node) && node is not null)
        {
            to[asName ?? name] = node.DeepClone();
        }
    }
}