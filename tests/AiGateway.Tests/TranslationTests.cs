using System.Net;
using System.Text.Json.Nodes;
using AiGateway.RunLog;
using AiGateway.Translation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class TranslationTests
{
    private const string AnthropicRequest = """
        {"model":"claude-sonnet-4-5","max_tokens":1024,"stream":true,
         "system":[{"type":"text","text":"你是助手","cache_control":{"type":"ephemeral"}}],
         "thinking":{"type":"enabled","budget_tokens":1024},
         "messages":[
           {"role":"user","content":[{"type":"text","text":"看图"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]},
           {"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"好"},{"type":"tool_use","id":"toolu_1","name":"ls","input":{"path":"/tmp"}}]},
           {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"a.txt"}]},{"type":"text","text":"继续"}]}
         ],
         "tools":[{"name":"ls","description":"list","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
         "tool_choice":{"type":"tool","name":"ls"},
         "stop_sequences":["END"],"metadata":{"user_id":"u-1"},"temperature":0.7}
        """;

    private const string NonStreamingRequest = """
        {"model":"claude-sonnet-4-5","max_tokens":64,"stream":false,
         "system":"你是助手",
         "messages":[{"role":"user","content":"列出文件"}],
         "tools":[{"name":"ls","description":"list","input_schema":{"type":"object","properties":{}}}],
         "tool_choice":{"type":"auto"},"temperature":0.2}
        """;

    private const string StreamingRequest = """
        {"model":"claude-sonnet-4-5","max_tokens":8,"stream":true,"messages":[{"role":"user","content":"hi"}]}
        """;

    private const string Completion = """
        {"id":"chatcmpl-abc","object":"chat.completion","created":1,"model":"deepseek-chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"这里没有文件"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":7,"total_tokens":19}}
        """;

    private static JsonObject TranslateRequest(string json, string model = "gpt-4o-mini") =>
        new AnthropicToOpenAiTranslator().TranslateRequest(JsonNode.Parse(json)!.AsObject(), model);

    // ---- request direction (golden fixture) ----

    [Fact]
    public void A_tool_bearing_request_becomes_a_valid_chat_completions_request()
    {
        var openAi = TranslateRequest(AnthropicRequest);
        var messages = openAi["messages"]!.AsArray();

        Assert.Equal("gpt-4o-mini", openAi["model"]!.GetValue<string>());

        Assert.Equal(("system", "你是助手"), (messages[0]!["role"]!.GetValue<string>(), messages[0]!["content"]!.GetValue<string>()));

        var userParts = messages[1]!["content"]!.AsArray();
        Assert.Equal("看图", userParts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AAAA", userParts[1]!["image_url"]!["url"]!.GetValue<string>());

        // The thinking block is gone; the text and the tool call survive.
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("好", messages[2]!["content"]!.GetValue<string>());
        var call = messages[2]!["tool_calls"]!.AsArray()[0]!;
        Assert.Equal(("toolu_1", "ls"), (call["id"]!.GetValue<string>(), call["function"]!["name"]!.GetValue<string>()));
        var arguments = JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!;
        Assert.Equal("/tmp", arguments["path"]!.GetValue<string>());

        Assert.Equal(("tool", "toolu_1"), (messages[3]!["role"]!.GetValue<string>(), messages[3]!["tool_call_id"]!.GetValue<string>()));
        Assert.Equal("a.txt", messages[3]!["content"]!.GetValue<string>());
        Assert.Equal("继续", messages[4]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_choice_stop_and_sampling_map_to_their_OpenAI_equivalents()
    {
        var openAi = TranslateRequest(AnthropicRequest);

        var tool = openAi["tools"]!.AsArray()[0]!;
        Assert.Equal(("function", "ls", "list"), (
            tool["type"]!.GetValue<string>(),
            tool["function"]!["name"]!.GetValue<string>(),
            tool["function"]!["description"]!.GetValue<string>()));
        Assert.Equal("object", tool["function"]!["parameters"]!["type"]!.GetValue<string>());

        Assert.Equal("ls", openAi["tool_choice"]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("END", openAi["stop"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal((1024, 0.7), (openAi["max_tokens"]!.GetValue<int>(), openAi["temperature"]!.GetValue<double>()));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "required")]
    [InlineData("none", "none")]
    public void Simple_tool_choices_become_their_string_form(string anthropicType, string expected)
    {
        var openAi = TranslateRequest($$"""{"messages":[],"tool_choice":{"type":"{{anthropicType}}"},"stream":false}""");

        Assert.Equal(expected, openAi["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void Streaming_asks_for_usage_because_OpenAI_omits_it_otherwise()
    {
        var openAi = TranslateRequest(AnthropicRequest);

        Assert.True(openAi["stream"]!.GetValue<bool>());
        Assert.True(openAi["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public void Anthropic_only_fields_never_reach_the_upstream()
    {
        var serialized = TranslateRequest(AnthropicRequest).ToJsonString();

        Assert.DoesNotContain("thinking", serialized);
        Assert.DoesNotContain("metadata", serialized);
        Assert.DoesNotContain("cache_control", serialized);
        Assert.DoesNotContain("budget_tokens", serialized);
    }

    // ---- response direction (golden fixture) ----

    [Fact]
    public void A_completion_with_a_tool_call_becomes_an_Anthropic_message()
    {
        const string completion = """
            {"id":"chatcmpl-xyz","model":"gpt-4o-mini",
             "choices":[{"index":0,"message":{"role":"assistant","content":"done","tool_calls":[{"id":"call_1","type":"function","function":{"name":"ls","arguments":"{\"path\":\"/tmp\"}"}}]},"finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":12,"completion_tokens":7}}
            """;

        var message = new OpenAiToAnthropicTranslator().TranslateMessage(JsonNode.Parse(completion)!.AsObject(), "gpt-4o-mini")!;
        var content = message["content"]!.AsArray();

        Assert.Equal(("msg_chatcmpl-xyz", "message", "assistant", "gpt-4o-mini"), (
            message["id"]!.GetValue<string>(),
            message["type"]!.GetValue<string>(),
            message["role"]!.GetValue<string>(),
            message["model"]!.GetValue<string>()));
        Assert.Equal(("done", "tool_use"), (content[0]!["text"]!.GetValue<string>(), content[1]!["type"]!.GetValue<string>()));
        Assert.Equal(("call_1", "ls"), (content[1]!["id"]!.GetValue<string>(), content[1]!["name"]!.GetValue<string>()));
        Assert.Equal("/tmp", content[1]!["input"]!["path"]!.GetValue<string>());
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.Equal((12L, 7L), (message["usage"]!["input_tokens"]!.GetValue<long>(), message["usage"]!["output_tokens"]!.GetValue<long>()));
    }

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("content_filter", "end_turn")]
    public void Finish_reasons_map_to_Anthropic_stop_reasons(string finishReason, string expected)
    {
        var completion = JsonNode.Parse($$"""{"choices":[{"message":{"content":"x"},"finish_reason":"{{finishReason}}"}]}""")!.AsObject();

        Assert.Equal(expected, new OpenAiToAnthropicTranslator().TranslateMessage(completion, "m")!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Malformed_tool_arguments_become_an_empty_object_instead_of_crashing()
    {
        const string completion = """
            {"choices":[{"message":{"tool_calls":[{"id":"c","function":{"name":"ls","arguments":"{\"path\":"}}]},"finish_reason":"tool_calls"}]}
            """;

        var input = new OpenAiToAnthropicTranslator().TranslateMessage(JsonNode.Parse(completion)!.AsObject(), "m")!["content"]!.AsArray()[0]!["input"]!;

        Assert.Empty(input.AsObject());
    }

    [Fact]
    public void A_response_with_no_choices_is_not_a_message()
    {
        var completion = JsonNode.Parse("""{"choices":[]}""")!.AsObject();

        Assert.Null(new OpenAiToAnthropicTranslator().TranslateMessage(completion, "m"));
    }

    // ---- end to end over the stub upstream ----

    [Fact]
    public async Task An_OpenAI_upstream_gets_a_translated_request_with_the_key_as_a_bearer_token()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteJson(context, Completion), kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(NonStreamingRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("/chat/completions", seen.PathAndQuery);
        Assert.Equal($"Bearer {GatewayHarness.RealKey}", seen.Headers["Authorization"]);
        Assert.False(seen.Headers.ContainsKey("x-api-key"));
        // anthropic-* headers belong to the inbound protocol and must not be forwarded to an OpenAI upstream.
        Assert.False(seen.Headers.ContainsKey("anthropic-version"));
        Assert.Equal("deepseek-chat", JsonNode.Parse(seen.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_non_streaming_completion_comes_back_as_an_Anthropic_message()
    {
        await using var gw = await GatewayHarness.StartAsync(
            context => WriteJson(context, Completion), kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(NonStreamingRequest));

        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(("message", "assistant"), (message["type"]!.GetValue<string>(), message["role"]!.GetValue<string>()));
        Assert.Equal("这里没有文件", message["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", message["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Count_tokens_on_an_OpenAI_upstream_is_404_and_never_reaches_it()
    {
        await using var gw = await GatewayHarness.StartAsync(kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(NonStreamingRequest, "/v1/messages/count_tokens?beta=true"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues("x-should-retry").Single());
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task An_Anthropic_upstream_still_takes_the_passthrough_path()
    {
        await using var gw = await GatewayHarness.StartAsync();

        using var _ = await gw.Client.SendAsync(gw.Messages(StreamingRequest));

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("/anthropic/v1/messages?beta=true", seen.PathAndQuery);
        // Only the model changed — the raw Anthropic body was forwarded, not translated.
        var original = JsonNode.Parse(StreamingRequest)!.AsObject();
        var received = JsonNode.Parse(seen.Body)!.AsObject();
        original["model"] = received["model"]!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(original, received));
    }

    [Fact]
    public async Task An_upstream_error_is_rewrapped_in_the_Anthropic_shape()
    {
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "3";
            await WriteJson(context, """{"error":{"message":"rate limited","type":"rate_limit_exceeded"}}""");
        }, kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(NonStreamingRequest));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("3", response.Headers.RetryAfter?.ToString());
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(("rate_limit_error", "rate limited"), (
            error["error"]!["type"]!.GetValue<string>(),
            error["error"]!["message"]!.GetValue<string>()));
    }

    private static async Task WriteJson(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    [Fact]
    public async Task A_200_body_that_is_not_JSON_is_a_readable_502_not_a_crash()
    {
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>misconfigured gateway</body></html>");
        }, kind: "open_ai");

        using var response = await gw.Client.SendAsync(gw.Messages(NonStreamingRequest));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("api_error", error["error"]!["type"]!.GetValue<string>());
        Assert.Contains("not a JSON object", error["error"]!["message"]!.GetValue<string>());
    }

    // The run log's field means "time to the first upstream body byte"; reading the body whole would stamp it at the end.
    [Fact]
    public async Task First_byte_is_stamped_when_the_body_starts_not_when_it_ends()
    {
        const string completion = """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var release = new TaskCompletionSource();
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            // The body starts arriving at once and is held open for a second, so "first byte" and "last byte" are far apart.
            await context.Response.WriteAsync(completion[..20]);
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync(completion[20..]);
        }, kind: "open_ai");

        var inFlight = gw.Client.SendAsync(gw.Messages(NonStreamingRequest));
        await Task.Delay(1000);
        release.SetResult();
        (await inFlight).Dispose();

        var ring = gw.Server.Services.GetRequiredService<RunLogRing>();
        await WaitUntil(() => ring.Snapshot().Count > 0);
        var firstByte = Assert.Single(ring.Snapshot()).FirstByte;

        Assert.NotNull(firstByte);
        Assert.True(firstByte < TimeSpan.FromMilliseconds(500), $"FirstByte was {firstByte}");
    }

    private static Task WaitUntil(Func<bool> condition) => Task.Run(async () =>
    {
        while (!condition())
        {
            await Task.Delay(25);
        }
    }).WaitAsync(TimeSpan.FromSeconds(10));

    // RISK-06: Translation path cancellation must be tested explicitly (passthrough has it, but translation did not).
    [Fact]
    public async Task Client_hanging_up_cancels_the_upstream_request_in_translation()
    {
        var upstreamSawCancel = new TaskCompletionSource();
        
        await using var gw = await GatewayHarness.StartAsync(async context =>
        {
            // Simulate an OpenAI-compatible upstream that sends SSE
            context.Response.ContentType = "text/plain"; // OpenAI uses plain text for streams
            await context.Response.WriteAsync("data: {\"choices\": [{\"delta\": {}}]}\n\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                upstreamSawCancel.SetResult();
            }
        }, kind: "open_ai");

        // Send Anthropic request which will be translated to OpenAI
        using var response = await gw.Client.SendAsync(
            gw.Messages(StreamingRequest),
            HttpCompletionOption.ResponseHeadersRead);
            
        var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        response.Dispose();

        await upstreamSawCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}