using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Configuration;
using AiGateway.Hosting;
using AiGateway.Routing;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AiGateway.Passthrough;

internal sealed class PassthroughForwarder(RouteResolver resolver, IHttpClientFactory clients)
{
    // Relaxed escaping keeps non-ASCII text (e.g. Chinese prompts) as-is instead of \uXXXX, so the body stays close to the original.
    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] ForwardedResponseHeaders = ["content-type", "retry-after", "request-id", "x-should-retry"];

    public async Task ForwardAsync(HttpContext context)
    {
        var aborted = context.RequestAborted;

        var resolved = resolver.Resolve();
        if (resolved.Route is not { } route)
        {
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error", resolved.Problem!);
            return;
        }

        if (route.Kind != UpstreamKind.Anthropic)
        {
            await Fail(context, StatusCodes.Status501NotImplemented, "api_error", "Upstreams that only speak the OpenAI protocol are not supported yet.");
            return;
        }

        var body = await ReadJsonObject(context.Request, aborted);
        if (body is null)
        {
            await Fail(context, StatusCodes.Status400BadRequest, "invalid_request_error", "Request body must be a JSON object.");
            return;
        }

        body["model"] = route.Model;
        using var request = BuildUpstreamRequest(context.Request, route, body);

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(UpstreamRequest.ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return;
        }
        catch (CryptographicException)
        {
            await Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error",
                $"The stored key for '{route.ProviderId}' cannot be decrypted under this Windows account. Enter the key again.");
            return;
        }
        catch (HttpRequestException)
        {
            await Fail(context, StatusCodes.Status502BadGateway, "api_error", $"Could not reach the upstream at {route.BaseUrl.Host}.", retryable: true);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (IsForwardedResponseHeader(name))
                {
                    context.Response.Headers[name] = new StringValues(values.ToArray());
                }
            }

            // An upstream 401 means the stored key is wrong; retrying cannot fix that, unless the upstream itself says otherwise.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !context.Response.Headers.ContainsKey("x-should-retry"))
            {
                context.Response.Headers["x-should-retry"] = "false";
            }

            try
            {
                await StreamBody(response, context.Response, aborted);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
            }
        }
    }

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

    private static HttpRequestMessage BuildUpstreamRequest(HttpRequest inbound, UpstreamRoute route, JsonObject body)
    {
        var target = new Uri(route.BaseUrl.AbsoluteUri.TrimEnd('/') + inbound.Path + inbound.QueryString);
        var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body.ToJsonString(BodyJson), Encoding.UTF8, "application/json"),
        };

        foreach (var (name, values) in inbound.Headers)
        {
            if (IsForwardedRequestHeader(name))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        request.Options.Set(UpstreamRequest.ProviderId, route.ProviderId);
        return request;
    }

    // Allow-list, not block-list: anything not named here (cookies, client telemetry, the proxy key itself) stays behind.
    private static bool IsForwardedRequestHeader(string name) =>
        name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("accept", StringComparison.OrdinalIgnoreCase)
        || name.Equals("user-agent", StringComparison.OrdinalIgnoreCase);

    private static bool IsForwardedResponseHeader(string name) =>
        name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase)
        || ForwardedResponseHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static async Task StreamBody(HttpResponseMessage from, HttpResponse to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var source = await from.Content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await to.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Claude Code retries 5xx for about three minutes before showing anything (S8 measured 174 s); a gateway
    // configuration problem is not transient, so say so and let the message reach the user at once.
    private static Task Fail(HttpContext context, int statusCode, string type, string message, bool retryable = false)
    {
        if (!retryable)
        {
            context.Response.Headers["x-should-retry"] = "false";
        }

        return AnthropicErrors.Create(statusCode, type, message).ExecuteAsync(context);
    }
}
