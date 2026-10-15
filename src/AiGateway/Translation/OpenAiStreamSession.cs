using System.Text;
using System.Text.Json.Nodes;

namespace AiGateway.Translation;

// Turns an OpenAI chat.completion.chunk stream into Anthropic message events.
//
// This is the one protocol gap that is not an interface mismatch but missing information (findings §6): OpenAI's
// tool_calls are flat fragments located by an `index` and are never closed, while Anthropic demands an explicit
// block lifecycle (content_block_start → N × input_json_delta → content_block_stop) with blocks numbered in order.
// So the block boundaries have to be invented here, and the index cursor lives in this one object (RISK-05: one
// instance per request, never shared, so parallel conversations cannot interleave).
internal sealed class OpenAiStreamSession(string fallbackModel)
{
    private readonly List<ToolCall> _toolCalls = [];
    private readonly Dictionary<int, int> _slots = [];

    private string? _messageId;
    private string? _model;
    private bool _started;
    private int _nextIndex;
    private int _textIndex = -1;
    private string? _stopReason;
    private long _inputTokens;
    private long _outputTokens;

    public IReadOnlyList<AnthropicStreamEvent> OnChunk(JsonObject chunk)
    {
        var events = new List<AnthropicStreamEvent>();
        // Read the model first: message_start reports it, and it is only available on the chunk itself.
        if (chunk["model"]?.GetValue<string>() is { Length: > 0 } model)
        {
            _model = model;
        }

        if (!_started)
        {
            _messageId = chunk["id"]?.GetValue<string>();
            events.Add(MessageStart());
            _started = true;
        }

        // RISK-03: the final chunk carries the usage we asked for via stream_options.include_usage.
        if (chunk["usage"] is JsonObject usage)
        {
            _inputTokens = usage["prompt_tokens"]?.GetValue<long>() ?? _inputTokens;
            _outputTokens = usage["completion_tokens"]?.GetValue<long>() ?? _outputTokens;
        }

        if (chunk["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
        {
            if (choice["delta"] is JsonObject delta)
            {
                AppendDelta(events, delta);
            }

            if (choice["finish_reason"]?.GetValue<string>() is { } reason)
            {
                _stopReason = StopReason(reason);
            }
        }

        return events;
    }

    // Everything still open has to be closed before the message ends: OpenAI has no end marker for a block.
    public IReadOnlyList<AnthropicStreamEvent> OnEnd()
    {
        var events = new List<AnthropicStreamEvent>();
        if (!_started)
        {
            events.Add(MessageStart());
            _started = true;
        }

        foreach (var index in OpenBlocks().OrderBy(i => i))
        {
            events.Add(BlockStop(index));
        }

        events.Add(new AnthropicStreamEvent("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = _stopReason ?? "end_turn", ["stop_sequence"] = null },
            // The upstream only reveals usage at the end, so input_tokens was 0 in message_start and is corrected here.
            ["usage"] = new JsonObject { ["input_tokens"] = _inputTokens, ["output_tokens"] = _outputTokens },
        }));
        events.Add(new AnthropicStreamEvent("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return events;
    }

    private void AppendDelta(List<AnthropicStreamEvent> events, JsonObject delta)
    {
        if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
        {
            if (_textIndex < 0)
            {
                _textIndex = _nextIndex++;
                events.Add(BlockStart(_textIndex, new JsonObject { ["type"] = "text", ["text"] = "" }));
            }

            events.Add(ContentDelta(_textIndex, new JsonObject { ["type"] = "text_delta", ["text"] = text }));
        }

        if (delta["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls.OfType<JsonObject>())
            {
                AppendToolCall(events, call);
            }
        }
    }

    private void AppendToolCall(List<AnthropicStreamEvent> events, JsonObject call)
    {
        var upstreamIndex = call["index"]?.GetValue<int>() ?? 0;
        if (!_slots.TryGetValue(upstreamIndex, out var slot))
        {
            slot = _toolCalls.Count;
            _slots[upstreamIndex] = slot;
            _toolCalls.Add(new ToolCall());
        }

        var tool = _toolCalls[slot];
        if (call["id"]?.GetValue<string>() is { Length: > 0 } id)
        {
            tool.Id = id;
        }

        if (call["function"] is JsonObject function)
        {
            if (function["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                tool.Name = name;
            }

            if (function["arguments"]?.GetValue<string>() is { Length: > 0 } arguments)
            {
                tool.Arguments.Append(arguments);
            }
        }

        // Anthropic carries id + name inside content_block_start, so the block can only open once the name is known.
        // Arguments that arrive earlier are buffered and flushed below.
        if (tool.SessionIndex < 0 && tool.Name is { Length: > 0 })
        {
            if (_textIndex >= 0)
            {
                // Close the text block first: Anthropic blocks are numbered in order and never reopen.
                events.Add(BlockStop(_textIndex));
                _textIndex = -1;
            }

            tool.SessionIndex = _nextIndex++;
            events.Add(BlockStart(tool.SessionIndex, new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = tool.Id ?? $"toolu_{tool.SessionIndex}",
                ["name"] = tool.Name,
                ["input"] = new JsonObject(),
            }));
        }

        if (tool.SessionIndex >= 0)
        {
            FlushArguments(events, tool);
        }
    }

    private static void FlushArguments(List<AnthropicStreamEvent> events, ToolCall tool)
    {
        if (tool.Arguments.Length <= tool.Emitted)
        {
            return;
        }

        var pending = tool.Arguments.ToString(tool.Emitted, tool.Arguments.Length - tool.Emitted);
        tool.Emitted = tool.Arguments.Length;
        events.Add(ContentDelta(tool.SessionIndex, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = pending }));
    }

    private IEnumerable<int> OpenBlocks()
    {
        if (_textIndex >= 0)
        {
            yield return _textIndex;
        }

        foreach (var tool in _toolCalls)
        {
            if (tool.SessionIndex >= 0)
            {
                yield return tool.SessionIndex;
            }
        }
    }

    private AnthropicStreamEvent MessageStart() => new("message_start", new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = _messageId is { Length: > 0 } id ? "msg_" + id : "msg_" + Guid.NewGuid().ToString("N"),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = _model ?? fallbackModel,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            // The upstream's usage arrives at the end; the real numbers follow in message_delta.
            ["usage"] = new JsonObject { ["input_tokens"] = 0L, ["output_tokens"] = 0L },
        },
    });

    private static AnthropicStreamEvent BlockStart(int index, JsonObject block) => new("content_block_start", new JsonObject
    {
        ["type"] = "content_block_start",
        ["index"] = index,
        ["content_block"] = block,
    });

    private static AnthropicStreamEvent BlockStop(int index) => new("content_block_stop", new JsonObject
    {
        ["type"] = "content_block_stop",
        ["index"] = index,
    });

    private static AnthropicStreamEvent ContentDelta(int index, JsonObject delta) => new("content_block_delta", new JsonObject
    {
        ["type"] = "content_block_delta",
        ["index"] = index,
        ["delta"] = delta,
    });

    private static string StopReason(string finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        _ => "end_turn",
    };

    private sealed class ToolCall
    {
        public int SessionIndex { get; set; } = -1;

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        // How much of Arguments has already gone out as input_json_delta.
        public int Emitted { get; set; }
    }
}