using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiGateway.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    public static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiGateway.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiGateway.sln not found above " + AppContext.BaseDirectory);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aigw-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

internal sealed class StubUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubUpstream(WebApplication app) => _app = app;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public Uri BaseAddress { get; private set; } = null!;

    public static async Task<StubUpstream> StartAsync(Func<HttpContext, Task>? respond = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var stub = new StubUpstream(app);

        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            stub.Requests.Enqueue(new RecordedRequest(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                await reader.ReadToEndAsync()));

            if (respond is null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{}");
            }
            else
            {
                await respond(context);
            }
        });

        await app.StartAsync();
        stub.BaseAddress = new Uri(app.Urls.Single());
        return stub;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

// Records formatted messages, every structured value and every scope, so a "no secret in logs" check cannot miss a sink.
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Lines.Enqueue($"[scope] {category}: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Lines.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    var rendered = value is System.Collections.IEnumerable items and not string
                        ? string.Join(", ", items.Cast<object?>())
                        : value?.ToString();
                    owner.Lines.Enqueue($"  {key} = {rendered}");
                }
            }
        }
    }
}
