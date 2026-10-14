using System.Text.Json;
using System.Text.Json.Serialization;
using AiGateway.Secrets;

namespace AiGateway.Configuration;

internal sealed class ConfigStore : ICiphertextStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _path;
    private readonly object _writeLock = new();
    private GatewayConfig _current;

    public ConfigStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiGateway", "config.json");

    public GatewayConfig Current => Volatile.Read(ref _current);

    public void Update(Func<GatewayConfig, GatewayConfig> change)
    {
        lock (_writeLock)
        {
            var next = change(_current);
            if (ReferenceEquals(next, _current))
            {
                return;
            }

            Write(_path, next);
            Volatile.Write(ref _current, next);
        }
    }

    public CiphertextRecord? Find(string providerId) =>
        Current.Secrets.FirstOrDefault(s => s.ProviderId == providerId);

    public void Put(CiphertextRecord record) =>
        Update(config => config with
        {
            Secrets = [.. config.Secrets.Where(s => s.ProviderId != record.ProviderId), record],
        });

    public bool Remove(string providerId)
    {
        var removed = false;
        Update(config =>
        {
            removed = config.Secrets.Any(s => s.ProviderId == providerId);
            return removed
                ? config with { Secrets = [.. config.Secrets.Where(s => s.ProviderId != providerId)] }
                : config;
        });
        return removed;
    }

    private static GatewayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new GatewayConfig();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<GatewayConfig>(stream, JsonOptions)
                ?? throw new InvalidDataException("Config file is empty: " + path);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Config file is not valid JSON: " + path, ex);
        }
    }

    private static void Write(string path, GatewayConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, config, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
