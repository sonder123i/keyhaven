using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using AiGateway.Hosting;
using AiGateway.Routing;
using AiGateway.RunLog;
using AiGateway.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AiGateway.Passthrough;

internal sealed class PassthroughForwarder(IHttpClientFactory clients)
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // Relaxed escaping keeps non-ASCII text (e.g. Chinese prompts) as-is instead of \uXXXX, so the body stays close to the original.
    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] ForwardedResponseHeaders = ["content-type", "retry-after", "request-id", "x-should-retry"];

    public async Task ForwardAsync(HttpContext context, RouteResult resolved)
    {
        var aborted = context.RequestAborted;
        var state = RunRequestState.For(context)!;

        // Both labels come from the one snapshot routing used, so a switch mid-request cannot relabel this line.
        state.Provider = resolved.ProviderId;
        if (resolved.Route is not { } route)
        {
            UpstreamForwarding.Mark(context, RunLogOutcome.Completed);
            await UpstreamForwarding.Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error", resolved.Problem!);
            return;
        }

        state.Model = route.Model;

        var body = await UpstreamForwarding.ReadJsonObject(context.Request, aborted);
        if (body is null)
        {
            UpstreamForwarding.Mark(context, RunLogOutcome.Completed);
            await UpstreamForwarding.Fail(context, StatusCodes.Status400BadRequest, "invalid_request_error", "Request body must be a JSON object.");
            return;
        }

        body["model"] = route.Model;
        var started = Clock.Elapsed;
        using var request = BuildUpstreamRequest(context.Request, route, body);

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(UpstreamRequest.ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            UpstreamForwarding.Mark(context, RunLogOutcome.ClientAborted);
            return;
        }
        catch (CryptographicException)
        {
            UpstreamForwarding.Mark(context, RunLogOutcome.Completed);
            await UpstreamForwarding.Fail(context, StatusCodes.Status503ServiceUnavailable, "api_error",
                $"The stored key for '{route.ProviderId}' cannot be decrypted under this Windows account. Enter the key again.");
            return;
        }
        catch (HttpRequestException)
        {
            UpstreamForwarding.Mark(context, RunLogOutcome.Completed);
            await UpstreamForwarding.Fail(context, StatusCodes.Status502BadGateway, "api_error", $"Could not reach the upstream at {route.BaseUrl.Host}.", retryable: true);
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
                await StreamBody(response, context.Response, aborted, state, started);
                UpstreamForwarding.Mark(context, RunLogOutcome.Completed);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                UpstreamForwarding.Mark(context, RunLogOutcome.ClientAborted);
            }
            catch (IOException)
            {
                // Upstream died mid-stream after headers went out: the response can no longer be replaced,
                // so record the break and let the client see the connection end. Never log the exception itself.
                UpstreamForwarding.Mark(context, RunLogOutcome.UpstreamInterrupted);
                throw;
            }
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

    private async Task StreamBody(HttpResponseMessage from, HttpResponse to, CancellationToken cancellationToken, RunRequestState state, TimeSpan started)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var source = await from.Content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                // First non-empty upstream body chunk: response headers alone do not count.
                state.FirstByte ??= Clock.Elapsed - started;
                await to.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
