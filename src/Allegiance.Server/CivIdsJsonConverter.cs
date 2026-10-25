using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Server;

/// <summary>Keep the existing int[] config API; JSON null is native's NA (-1).
/// An entire null list is invalid, unlike a null entry.</summary>
public sealed class CivIdsJsonConverter : JsonConverter<int[]>
{
    public override bool HandleNull => true;

    public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("civIds must be an array of civilization IDs or null entries (random)");
        var ids = new List<int>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return ids.ToArray();
            if (reader.TokenType == JsonTokenType.Null) ids.Add(-1);
            else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int id)) ids.Add(id);
            else throw new JsonException("civIds entries must be integer civilization IDs or null (random)");
        }
        throw new JsonException("civIds array is incomplete");
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (int id in value)
            if (id == -1) writer.WriteNullValue(); else writer.WriteNumberValue(id);
        writer.WriteEndArray();
    }
}
