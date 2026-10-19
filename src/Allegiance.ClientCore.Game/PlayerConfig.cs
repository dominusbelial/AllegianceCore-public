using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Portable player preferences, kept outside the installation.</summary>
public sealed record PlayerConfig(string Server = "", ushort Port = 6073, string Pilot = "pilot",
    string Lobby = "http://127.0.0.1:5000", BenchmarkCollectorConfig? Benchmark = null)
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllegianceCore");
    public static string FilePath => Path.Combine(DirectoryPath, "player.json");

    public static string? ReadBenchmarkCollectorUrl()
    {
        try { return Load().Benchmark?.CollectorUrl; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    public static PlayerConfig Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<PlayerConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Empty player configuration: {path}");
    }
}

[JsonConverter(typeof(BenchmarkCollectorConfigConverter))]
public sealed record BenchmarkCollectorConfig(string CollectorUrl = "");

// A malformed optional sharing setting must not prevent loading ordinary player preferences.
public sealed class BenchmarkCollectorConfigConverter : JsonConverter<BenchmarkCollectorConfig>
{
    public override BenchmarkCollectorConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        if (value.RootElement.ValueKind != JsonValueKind.Object) return new();
        foreach (var property in value.RootElement.EnumerateObject())
            if (property.Name.Equals("collectorUrl", StringComparison.OrdinalIgnoreCase))
                return new(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : "");
        return new();
    }
    public override void Write(Utf8JsonWriter writer, BenchmarkCollectorConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("collectorUrl", value.CollectorUrl); writer.WriteEndObject();
    }
}
