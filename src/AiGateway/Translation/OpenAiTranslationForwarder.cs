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

// The translation path (M2): an OpenAI-only upstream. Requests are rewritten into chat/completions and the
// non-streaming answer is made back into an Anthropic message. Streaming re-encoding is the tool-call block
// lifecycle and lands with S12, so a stream request is refused here rather than sent and mistranslated.
internal sealed class OpenAiTranslationForwarder(
    IHttpClientFactory clients,
    AnthropicToOpenAiTranslator requestTranslator,
    OpenAiToAnthropicTranslator responseTranslator)
{
    private const string CountTokensPath = "/v1/messages/count_tokens";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task ForwardAsync(HttpContext context, RouteResult resolved)
    {
        var aborted = context.RequestAborted;
        var state = RunRequestState.For(context)!;
        var route = resolved.Route!;
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
            Mark(context, RunLogOutcome.Completed);
            await Fail(context, StatusCodes.Status501NotImplemented, "api_error",
                "Streaming responses from an OpenAI upstream are not supported yet.");
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
            var payload = await response.Content.ReadAsStringAsync(aborted);
            state.FirstByte ??= Clock.Elapsed - started;
            ForwardStatusHeaders(response, context);

            if (!response.IsSuccessStatusCode)
            {
                Mark(context, RunLogOutcome.Completed);
                await FailFromUpstream(context, response.StatusCode, payload);
                return;
            }

            if (JsonNode.Parse(payload) is not JsonObject completion || responseTranslator.TranslateMessage(completion, route.Model) is not { } message)
            {
                Mark(context, RunLogOutcome.Completed);
                await Fail(context, StatusCodes.Status502BadGateway, "api_error", "The OpenAI upstream returned a response with no completion choices.");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(message.ToJsonString(BodyJson), aborted);
            Mark(context, RunLogOutcome.Completed);
        }
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
    private static Task FailFromUpstream(HttpContext context, HttpStatusCode status, string payload)
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

        return AnthropicErrors.Create((int)status, type, UpstreamMessage(payload) ?? $"The upstream returned {(int)status}.").ExecuteAsync(context);
    }

    private static string? UpstreamMessage(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject error ? error["error"]?["message"]?.GetValue<string>() : null;
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