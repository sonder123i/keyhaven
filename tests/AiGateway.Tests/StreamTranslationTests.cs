using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Translation;
using Microsoft.AspNetCore.Http;

namespace AiGateway.Tests;

// The streaming path: OpenAI chat.completion.chunk → Anthropic message events. The fixture below is the shape
// OpenAI-compatible providers emit on the wire (text first, then parallel tool_calls located by `index`, each
// arguments fragment appended, then an empty-delta chunk carrying finish_reason, a choices:[] usage chunk, [DONE]).
public class StreamTranslationTests
{
    private const string StreamingRequest = """
        {"model":"claude-sonnet-4-5","max_tokens":256,"stream":true,
         "messages":[{"role":"user","content":"北京天气？"}],
         "tools":[{"name":"get_weather","description":"天气","input_schema":{"type":"object","properties":{"city":{"type":"string"}}}}]}
        """;

    private static readonly string[] Chunks =
    [
        """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"deepseek-chat","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"让我"},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"查一下"},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"ci"}}]},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"北京\"}"}}]},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"get_time","arguments":"{\"tz\""}}]},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":":\"+08\"}"}}]},"finish_reason":null}]}""",
        """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":31,"completion_tokens":12,"total_tokens":43}}""",
        "[DONE]",
    ];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string Sse(IEnumerable<string> payloads) => string.Concat(payloads.Select(p => $"data: {p}\n\n"));

    private static List<AnthropicStreamEvent> Run(IEnumerable<string> payloads)
    {
        var session = new OpenAiStreamSession("fallback-model");
        var events = new List<AnthropicStreamEvent>();
        foreach (var payload in payloads.Where(p => p != "[DONE]"))
        {
            events.AddRange(session.OnChunk(JsonNode.Parse(payload)!.AsObject()));
        }

        events.AddRange(session.OnEnd());
        return events;
    }

    private static int Index(AnthropicStreamEvent streamEvent, string property = "index") => streamEvent.Payload[property]!.GetValue<int>();

    private static string Arguments(int index, List<AnthropicStreamEvent> events) => string.Concat(events
        .Where(e => e.Name == "content_block_delta" && Index(e) == index && e.Payload["delta"]!["type"]!.GetValue<string>() == "input_json_delta")
        .Select(e => e.Payload["delta"]!["partial_json"]!.GetValue<string>()));

    // ---- golden fixture ----

    [Fact]
    public void A_chunk_stream_becomes_a_well_formed_Anthropic_event_sequence()
    {
        var events = Run(Chunks);

        Assert.Equal(
        [
            "message_start",
            "content_block_start", "content_block_delta", "content_block_delta",
            "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_delta",
            "content_block_start", "content_block_delta", "content_block_delta",
            "content_block_stop", "content_block_stop",
            "message_delta", "message_stop",
        ], events.Select(e => e.Name));

        var start = events[0].Payload["message"]!;
        Assert.Equal(("msg_chatcmpl-1", "message", "assistant", "deepseek-chat"), (
            start["id"]!.GetValue<string>(), start["type"]!.GetValue<string>(),
            start["role"]!.GetValue<string>(), start["model"]!.GetValue<string>()));
    }

    [Fact]
    public void Blocks_are_numbered_in_order_and_every_start_has_one_matching_stop()
    {
        var events = Run(Chunks);

        var starts = events.Where(e => e.Name == "content_block_start").Select(e => Index(e)).ToList();
        var stops = events.Where(e => e.Name == "content_block_stop").Select(e => Index(e)).ToList();

        Assert.Equal([0, 1, 2], starts);
        Assert.Equal(starts.OrderBy(i => i), stops);
        Assert.Equal([0, 1, 2], events
            .Where(e => e.Name == "content_block_delta")
            .Select(e => Index(e))
            .Distinct()
            .OrderBy(i => i));
    }

    [Fact]
    public void Text_and_parallel_tool_calls_keep_their_own_blocks()
    {
        var events = Run(Chunks);

        var blocks = events.Where(e => e.Name == "content_block_start").Select(e => e.Payload["content_block"]!).ToList();
        Assert.Equal("text", blocks[0]["type"]!.GetValue<string>());
        Assert.Equal("让我查一下", string.Concat(events
            .Where(e => e.Name == "content_block_delta" && Index(e) == 0)
            .Select(e => e.Payload["delta"]!["text"]!.GetValue<string>())));

        // Each tool call gets its own block, with the upstream id and name in content_block_start.
        Assert.Equal(("call_a", "get_weather"), (blocks[1]["id"]!.GetValue<string>(), blocks[1]["name"]!.GetValue<string>()));
        Assert.Equal(("call_b", "get_time"), (blocks[2]["id"]!.GetValue<string>(), blocks[2]["name"]!.GetValue<string>()));
        Assert.Equal("{\"city\":\"北京\"}", Arguments(1, events));
        Assert.Equal("{\"tz\":\"+08\"}", Arguments(2, events));
    }

    [Fact]
    public void The_final_usage_is_put_on_message_delta_because_it_cannot_be_known_at_the_start()
    {
        var events = Run(Chunks);

        var delta = events.Single(e => e.Name == "message_delta");
        Assert.Equal("tool_use", delta.Payload["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Equal((31L, 12L), (
            delta.Payload["usage"]!["input_tokens"]!.GetValue<long>(),
            delta.Payload["usage"]!["output_tokens"]!.GetValue<long>()));

        // message_start cannot know the input size yet, so it announces zeros and message_delta corrects them.
        Assert.Equal(0L, events[0].Payload["message"]!["usage"]!["input_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void A_tool_block_waits_for_the_name_before_opening()
    {
        // Arguments arrive before the name: the block must not open yet, and the buffered text goes out once it does.
        string[] payloads =
        [
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"arguments":"{\"a\""}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"ls","arguments":":1}"}}]},"finish_reason":null}]}""",
        ];

        var events = Run(payloads);

        Assert.Equal(
        [
            "message_start", "content_block_start", "content_block_delta",
            "content_block_stop", "message_delta", "message_stop",
        ], events.Select(e => e.Name));
        Assert.Equal("ls", events[1].Payload["content_block"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"a\":1}", events[2].Payload["delta"]!["partial_json"]!.GetValue<string>());
    }

    [Fact]
    public void Truncated_tool_arguments_go_out_verbatim_and_the_stop_reason_says_so()
    {
        // Policy for an unfinished argument JSON: forward the fragments exactly as they arrived (Anthropic itself
        // does the same when max_tokens cuts a tool call off) — never invent a closing brace, never drop the call.
        string[] payloads =
        [
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"ls","arguments":"{\"path\":\""}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""",
        ];

        var events = Run(payloads);

        Assert.Equal("{\"path\":\"", Arguments(0, events));
        Assert.Equal("max_tokens", events.Single(e => e.Name == "message_delta").Payload["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_is_carried_between_requests()
    {
        var first = Run(Chunks);
        var second = Run(Chunks);

        // A static or shared cursor would keep counting on the second run; two fresh sessions must look identical.
        Assert.Equal(
            first.Select(e => $"{e.Name}:{e.Payload["index"]}"),
            second.Select(e => $"{e.Name}:{e.Payload["index"]}"));
    }

    // ---- end to end over the stub upstream ----

    [Fact]
    public async Task A_translated_stream_reaches_the_client_event_by_event()
    {
        var release = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Sse(Chunks[..8]));
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync(Sse(Chunks[8..]));
        }, kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(StreamingRequest), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[8192];
        var first = Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer).AsTask().WaitAsync(Patience));
        Assert.Contains("message_start", first);
        Assert.DoesNotContain("message_stop", first);

        release.SetResult();
        var rest = await new StreamReader(stream).ReadToEndAsync().WaitAsync(Patience);

        Assert.Contains("message_stop", rest);
        // Every translated event made it out, and the OpenAI framing did not.
        Assert.Contains("input_json_delta", first + rest);
        Assert.DoesNotContain("chat.completion.chunk", first + rest);
        Assert.DoesNotContain("[DONE]", first + rest);
    }

    [Fact]
    public async Task An_error_on_the_streaming_path_is_rewrapped_too()
    {
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"message":"bad key","type":"invalid_api_key"}}""");
        }, kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(StreamingRequest));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues("x-should-retry").Single());
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("authentication_error", error["error"]!["type"]!.GetValue<string>());
    }
}