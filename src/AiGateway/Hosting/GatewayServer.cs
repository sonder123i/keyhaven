using System.Net;
using AiGateway.Configuration;
using AiGateway.Passthrough;
using AiGateway.ProxyKeys;
using AiGateway.RunLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Hosting;

public sealed class GatewayServer : IAsyncDisposable
{
    private const string HealthPath = "/health";

    private readonly WebApplication _app;

    private GatewayServer(WebApplication app, int configuredPort)
    {
        _app = app;
        ConfiguredPort = configuredPort;
    }

    public int ConfiguredPort { get; }

    public IServiceProvider Services => _app.Services;

    public Uri? BaseAddress { get; private set; }

    internal IReadOnlyCollection<string> ListeningAddresses => _app.Urls.ToList();

    public static GatewayServer Create(Action<IServiceCollection>? configureServices = null, string? configPath = null)
    {
        var store = new ConfigStore(configPath ?? ConfigStore.DefaultPath);
        var port = store.Current.Port;

        // Empty builder on purpose: no environment variables or appsettings are read, so nothing like ASPNETCORE_URLS
        // or Kestrel__Endpoints__* can add a listener beyond loopback (D9), and no default log sink receives request data.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddRoutingCore();
        builder.Services.AddGatewayCore(store);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.Use(ObserveRun);
        app.Use(RequireProxyKey);
        app.MapGet(HealthPath, () => Results.Ok(new { status = "ok" }));
        app.MapPost("/v1/messages", Forward);
        app.MapPost("/v1/messages/count_tokens", Forward);

        return new GatewayServer(app, port);
    }

    private static Task Forward(HttpContext context) =>
        context.RequestServices.GetRequiredService<PassthroughForwarder>().ForwardAsync(context);

    private static async Task RequireProxyKey(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == HealthPath)
        {
            await next(context);
            return;
        }

        var authenticator = context.RequestServices.GetRequiredService<ProxyKeyAuthenticator>();
        if (authenticator.Authorize(ProxyKeyAuthenticator.ReadPresentedKey(context.Request)) is not { } fingerprint)
        {
            // Claude Code retries 401 for about three minutes too; a revoked key never becomes valid again.
            context.Response.Headers["x-should-retry"] = "false";
            await AnthropicErrors
                .Create(StatusCodes.Status401Unauthorized, "authentication_error", "Missing, unknown or revoked proxy key.")
                .ExecuteAsync(context);
            return;
        }

        RunRequestState.For(context)!.Fingerprint = fingerprint;
        await next(context);
    }

    // The single place a request ends: success, rejection, 404, abort or crash all produce exactly one run-log line.
    private static async Task ObserveRun(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == HealthPath)
        {
            await next(context);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var state = RunRequestState.For(context)!;
        var outcome = RunLogOutcome.Completed;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            outcome = RunLogOutcome.ClientAborted;
            throw;
        }
        catch
        {
            outcome = RunLogOutcome.UnhandledError;
            throw;
        }
        finally
        {
            // A status the client could never have seen (headers not sent, still the default 200) is recorded as unknown.
            var status = context.Response.HasStarted || context.Response.StatusCode != StatusCodes.Status200OK
                ? context.Response.StatusCode
                : (int?)null;
            context.RequestServices.GetRequiredService<RunLogRing>().Append(
                new RunLogEntry(started, state.Fingerprint, state.Provider, state.Model, status, state.FirstByte, state.Outcome ?? outcome));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _app.StartAsync(cancellationToken);
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            throw new GatewayPortInUseException(ConfiguredPort, ex);
        }

        BaseAddress = new Uri(_app.Urls.Single());
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => _app.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public sealed class GatewayPortInUseException(int port, Exception innerException)
    : Exception($"Port {port} on 127.0.0.1 is already in use by another program.", innerException)
{
    public int Port { get; } = port;
}
