using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Hosting;
using AiGateway.Routing;
using AiGateway.RunLog;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AiGateway.Translation;

// The translation path (M2): an OpenAI-only upstream. Requests are rewritten into chat/completions, and the answer
// is made back into an Anthropic shape — one message when the client did not ask to stream, an event sequence via
// OpenAiStreamSession when it did.
internal sealed class OpenAiTranslationForwarder(
    IHttpClientFactory clients,
    AnthropicToOpenAiTranslator requestTranslator,
    OpenAiToAnthropicTranslator responseTranslator)
{
    private const string CountTokensPath = "/v1/messages/count_tokens";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task ForwardAsync(HttpContext context, UpstreamRoute route)
    {
        var aborted = context.RequestAborted;
        var state = RunRequestState.For(context)!;
        state.Provider = route.ProviderId;
        state.Model = route.Model;

        // OpenAI has no token-counting endpoint. A 404 makes Claude Code fall back to its character estimate (RISK-04).
        if (context.Request.Path == CountTokensPath)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status404NotFound, "not_found_error",
                "count_tokens is not available for an OpenAI upstream; the client estimates by characters instead.");
            return;
        }

        var body = await ReadJsonObject(context.Request, aborted);
        if (body is null)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status400BadRequest, "invalid_request_error", "Request body must be a JSON object.");
            return;
        }

        if (body["stream"]?.GetValue<bool>() == true)
        {
            await StreamAsync(context, route, body);
            return;
        }

        var upstreamBody = requestTranslator.TranslateRequest(body, route.Model);
        var started = Clock.Elapsed;
        using var request = BuildUpstreamRequest(context.Request, route, upstreamBody);

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(UpstreamRequest.ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            Mark(context, RunLogOutcome.ClientAborted);
            return;
        }
        catch (CryptographicException)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error",
                $"The stored key for '{route.ProviderId}' cannot be decrypted under this Windows account. Enter the key again.");
            return;
        }
        catch (HttpRequestException)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status502BadGateway, "api_error", $"Could not reach the upstream at {route.BaseUrl.Host}.", retryable: true);
            return;
        }

        using (response)
        {
            ForwardStatusHeaders(response, context);
            using var responseBody = await ReadBodyAsync(response, state, started, aborted);

            if (!response.IsSuccessStatusCode)
            {
                Mark(context, RunLogOutcome.Completed);
                await FailFromUpstream(context, response.StatusCode, TryParseObject(responseBody));
                return;
            }

            var completion = TryParseObject(responseBody);
            var message = completion is null ? null : responseTranslator.TranslateMessage(completion, route.Model);
            if (message is null)
            {
                Mark(context, RunLogOutcome.Completed);
                // A 200 whose body is not JSON (a misconfigured BaseUrl landing on an HTML page) is the upstream's
                // problem, not an unhandled crash: say so in the shape the client can read.
                await Fail(context, StatusCodes.Status502BadGateway, "api_error",
                    completion is null
                        ? "The OpenAI upstream returned a body that is not a JSON object."
                        : "The OpenAI upstream returned a response with no completion choices.");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(message.ToJsonString(BodyJson), aborted);
            Mark(context, RunLogOutcome.Completed);
        }
    }

    // The streaming path: read the upstream's SSE lines, run each chunk through a per-request session and write the
    // Anthropic frames back with a flush per upstream chunk. Any buffering here and Claude Code stops seeing increments.
    private async Task StreamAsync(HttpContext context, UpstreamRoute route, JsonObject body)
    {
        var aborted = context.RequestAborted;
        var state = RunRequestState.For(context)!;
        var started = Clock.Elapsed;
        using var request = BuildUpstreamRequest(context.Request, route, requestTranslator.TranslateRequest(body, route.Model));

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(UpstreamRequest.ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            Mark(context, RunLogOutcome.ClientAborted);
            return;
        }
        catch (CryptographicException)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error",
                $"The stored key for '{route.ProviderId}' cannot be decrypted under this Windows account. Enter the key again.");
            return;
        }
        catch (HttpRequestException)
        {
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status502BadGateway, "api_error", $"Could not reach the upstream at {route.BaseUrl.Host}.", retryable: true);
            return;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ForwardStatusHeaders(response, context);
                using var error = await ReadBodyAsync(response, state, started, aborted);
                Mark(context, RunLogOutcome.Completed);
                await FailFromUpstream(context, response.StatusCode, TryParseObject(error));
                return;
            }

            // Some OpenAI-compatible endpoints ignore stream:true and answer with one JSON completion. Read as a stream
            // that yields no events at all, that becomes an empty message — silently wrong, and the worst kind of
            // failure to diagnose — so refuse it loudly instead of answering nothing.
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                Mark(context, RunLogOutcome.Completed);
                await Fail(context, StatusCodes.Status502BadGateway, "api_error",
                    "The upstream did not return an event stream for a streaming request.");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            var session = new OpenAiStreamSession(route.Model);

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(aborted);
                using var reader = new StreamReader(source);
                while (await reader.ReadLineAsync(aborted) is { } line)
                {
                    // Only data lines carry payload; blank separators, event: names and comments are dropped.
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var payload = line[5..].TrimStart();
                    state.FirstByte ??= Clock.Elapsed - started;
                    if (payload == "[DONE]")
                    {
                        break;
                    }

                    // A malformed line — or a provider's non-JSON keep-alive — is skipped, not fatal: one bad line
                    // must not truncate a stream the client has already accepted (headers are long gone).
                    if (TryParseObject(payload) is { } chunk)
                    {
                        await EmitAsync(context.Response, session.OnChunk(chunk), aborted);
                    }
                }

                await EmitAsync(context.Response, session.OnEnd(), aborted);
                Mark(context, RunLogOutcome.Completed);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                Mark(context, RunLogOutcome.ClientAborted);
            }
            catch (IOException)
            {
                // Upstream died mid-stream after headers went out; the client just sees the stream end.
                Mark(context, RunLogOutcome.UpstreamInterrupted);
                throw;
            }
        }
    }

    private static async Task EmitAsync(HttpResponse response, IReadOnlyList<AnthropicStreamEvent> events, CancellationToken cancellationToken)
    {
        foreach (var streamEvent in events)
        {
            await response.WriteAsync(streamEvent.ToSse(), cancellationToken);
        }

        await response.Body.FlushAsync(cancellationToken);
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpRequest inbound, UpstreamRoute route, JsonObject body)
    {
        var target = new Uri(route.BaseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body.ToJsonString(BodyJson), Encoding.UTF8, "application/json"),
        };

        // Only the headers an OpenAI upstream understands. anthropic-* would be meaningless or rejected here,
        // and the inbound auth headers are the proxy key, which must never leave (the injector replaces them anyway).
        foreach (var (name, values) in inbound.Headers)
        {
            if (name.Equals("accept", StringComparison.OrdinalIgnoreCase) || name.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        request.Options.Set(UpstreamRequest.ProviderId, route.ProviderId);
        request.Options.Set(UpstreamRequest.BearerAuth, true);
        return request;
    }

    private static void ForwardStatusHeaders(HttpResponseMessage from, HttpContext context)
    {
        foreach (var name in new[] { "retry-after", "x-should-retry" })
        {
            if (from.Headers.TryGetValues(name, out var values))
            {
                context.Response.Headers[name] = new StringValues(values.ToArray());
            }
        }

        // A wrong stored key will not fix itself; tell Claude Code so it does not retry for minutes (RISK-17).
        if (from.StatusCode == HttpStatusCode.Unauthorized && !context.Response.Headers.ContainsKey("x-should-retry"))
        {
            context.Response.Headers["x-should-retry"] = "false";
        }
    }

    // The upstream's error body is OpenAI-shaped; the client only understands Anthropic-shaped errors, so rewrap it.
    // A body that is not JSON at all (an HTML error page, say) still becomes an Anthropic-shaped error, just without its message.
    private static Task FailFromUpstream(HttpContext context, HttpStatusCode status, JsonObject? errorBody)
    {
        var type = status switch
        {
            HttpStatusCode.Unauthorized => "authentication_error",
            HttpStatusCode.Forbidden => "permission_error",
            HttpStatusCode.NotFound => "not_found_error",
            HttpStatusCode.TooManyRequests => "rate_limit_error",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "invalid_request_error",
            _ => "api_error",
        };

        var message = errorBody?["error"]?["message"]?.GetValue<string>() ?? $"The upstream returned {(int)status}.";
        return AnthropicErrors.Create((int)status, type, message).ExecuteAsync(context);
    }

    // Buffer the body while stamping the first byte as it arrives: ReadAsStringAsync would stamp it only after the whole
    // body, which is not what the run log's field means. Parsing from the stream also tolerates a byte-order mark.
    private static async Task<MemoryStream> ReadBodyAsync(HttpResponseMessage response, RunRequestState state, TimeSpan started, CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            state.FirstByte ??= Clock.Elapsed - started;
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    // Parsing never throws out of here: a body or an SSE line that is not a JSON object is simply not usable.
    private static JsonObject? TryParseObject(Stream body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Mark(HttpContext context, RunLogOutcome outcome) => RunRequestState.For(context)!.Outcome = outcome;

    private static async Task<JsonObject?> ReadJsonObject(HttpRequest request, CancellationToken cancellationToken)
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

    private static Task Fail(HttpContext context, int statusCode, string type, string message, bool retryable = false)
    {
        if (!retryable)
        {
            context.Response.Headers["x-should-retry"] = "false";
        }

        return AnthropicErrors.Create(statusCode, type, message).ExecuteAsync(context);
    }
}