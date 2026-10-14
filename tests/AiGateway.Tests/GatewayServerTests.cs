using System.Net;
using System.Net.Sockets;
using AiGateway.Hosting;

namespace AiGateway.Tests;

public sealed class GatewayServerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private GatewayServer CreateOnPort(int port)
    {
        var path = _dir.File("config.json");
        File.WriteAllText(path, $$"""{ "port": {{port}} }""");
        return GatewayServer.Create(configPath: path);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Health_answers_200_without_any_key()
    {
        await using var server = CreateOnPort(0);
        await server.StartAsync();

        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var response = await http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uses_the_port_from_config()
    {
        var port = FreePort();
        await using var server = CreateOnPort(port);
        await server.StartAsync();

        Assert.Equal(new Uri($"http://127.0.0.1:{port}"), server.BaseAddress);
    }

    [Fact]
    public async Task Listens_only_on_loopback_even_if_the_environment_asks_for_more()
    {
        var leakPort = FreePort();
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{leakPort}");
        Environment.SetEnvironmentVariable("Kestrel__Endpoints__Leak__Url", $"http://0.0.0.0:{leakPort}");
        try
        {
            await using var server = CreateOnPort(0);
            await server.StartAsync();

            var address = Assert.Single(server.ListeningAddresses);
            Assert.StartsWith("http://127.0.0.1:", address);

            using var squatter = new TcpListener(IPAddress.Any, leakPort);
            squatter.Start();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Leak__Url", null);
        }
    }

    [Fact]
    public async Task Busy_port_fails_with_a_readable_error()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var port = ((IPEndPoint)squatter.LocalEndpoint).Port;

        await using var server = CreateOnPort(port);
        var ex = await Assert.ThrowsAsync<GatewayPortInUseException>(() => server.StartAsync());

        Assert.Equal(port, ex.Port);
    }

    [Fact]
    public async Task Stopping_releases_the_port()
    {
        var server = CreateOnPort(0);
        await server.StartAsync();
        var port = server.BaseAddress!.Port;

        await server.StopAsync();
        await server.DisposeAsync();

        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
    }
}
