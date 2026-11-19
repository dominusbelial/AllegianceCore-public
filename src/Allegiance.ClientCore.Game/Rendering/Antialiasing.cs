using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Optional desktop scene MSAA. A visual departure; new profiles stay Off
/// (native UseAntialiasing default 0, WinTrek/WinTrek.cpp:2997).</summary>
[JsonConverter(typeof(AntialiasingJsonConverter))]
public enum Antialiasing { Off = 0, Msaa2 = 2, Msaa4 = 4, Msaa8 = 8 }

public static class AntialiasingOptions
{
    public static Antialiasing Normalize(Antialiasing value)
    {
        if (Enum.IsDefined(value)) return value;
        return Unknown();
    }
    internal static Antialiasing Unknown()
    {
        Console.WriteLine("[antialiasing] unknown value; requesting highest device-supported MSAA (up to 8x)");
        return Antialiasing.Msaa8;
    }
    public static string Describe(Antialiasing value) => Normalize(value) switch
    {
        Antialiasing.Off => "Off", Antialiasing.Msaa2 => "2x MSAA", Antialiasing.Msaa4 => "4x MSAA", _ => "8x MSAA"
    };
    public static Antialiasing Next(Antialiasing value, int direction = 1)
    {
        Antialiasing[] levels = [Antialiasing.Off, Antialiasing.Msaa2, Antialiasing.Msaa4, Antialiasing.Msaa8];
        return levels[(Array.IndexOf(levels, Normalize(value)) + Math.Sign(direction) + levels.Length) % levels.Length];
    }
    /// <summary>Unsupported requests fall to the highest supported level no greater
    /// than the request. An unknown value requests the highest (8); no MSAA support => Off.</summary>
    public static int Clamp(Antialiasing request, Func<int, bool> supported)
    {
        int desired = (int)Normalize(request);
        foreach (int level in new[] { 8, 4, 2 })
            if (level <= desired && supported(level)) return level;
        return 0;
    }
    /// <summary>Additional scene colour/depth MSAA attachment + resolve texture.
    /// Depth24 uses a conservative 32-bit storage estimate. Driver padding excluded.</summary>
    public static long TargetBytes(int width, int height, int samples, bool hdr)
        => checked((long)width * height * ((hdr ? 8 : 4) * (Math.Max(1, samples) + (samples > 0 ? 1 : 0)) + 4 * Math.Max(1, samples)));
}

/// <summary>Unknown saved values must not discard the rest of graphics.json.</summary>
public sealed class AntialiasingJsonConverter : JsonConverter<Antialiasing>
{
    public override Antialiasing Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
            return AntialiasingOptions.Normalize((Antialiasing)number);
        if (reader.TokenType == JsonTokenType.String)
        {
            string? value = reader.GetString();
            if (Enum.TryParse<Antialiasing>(value, true, out var parsed)) return AntialiasingOptions.Normalize(parsed);
            return value?.ToLowerInvariant() switch { "2x" => Antialiasing.Msaa2, "4x" => Antialiasing.Msaa4, "8x" => Antialiasing.Msaa8, _ => AntialiasingOptions.Unknown() };
        }
        using var ignored = JsonDocument.ParseValue(ref reader);
        return AntialiasingOptions.Unknown();
    }
    public override void Write(Utf8JsonWriter writer, Antialiasing value, JsonSerializerOptions options)
        => writer.WriteStringValue(AntialiasingOptions.Normalize(value).ToString());
}
