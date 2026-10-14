using System.Net;
using AiGateway.Configuration;
using AiGateway.Passthrough;
using AiGateway.ProxyKeys;
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
        if (!authenticator.IsAuthorized(ProxyKeyAuthenticator.ReadPresentedKey(context.Request)))
        {
            await AnthropicErrors
                .Create(StatusCodes.Status401Unauthorized, "authentication_error", "Missing, unknown or revoked proxy key.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
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
