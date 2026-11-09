using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P986 — an opt-in, zero-cost-when-off recorder for bot decision inputs and
/// outputs. Enabled only when <c>ALLEGIANCE_BOT_RECORD_DECISIONS=1</c> AND a
/// write path is supplied via <c>ALLEGIANCE_BOT_RECORD_PATH</c>. The format is
/// a compact canonical JSON stream (one object per line) so it can be diffed
/// and versioned as a golden corpus.
///
/// This seam is intentionally behaviour-neutral: every public method returns
/// immediately when recording is disabled and never mutates bot state.
/// </summary>
internal static class BotDecisionRecorder
{
    internal const string EnableVar = "ALLEGIANCE_BOT_RECORD_DECISIONS";
    internal const string PathVar = "ALLEGIANCE_BOT_RECORD_PATH";

    private static readonly bool Enabled = Environment.GetEnvironmentVariable(EnableVar) == "1";
    private static readonly string BasePath = Environment.GetEnvironmentVariable(PathVar)
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p986", "bot-decisions");

    /// <summary>True when the recorder is actively writing decisions.</summary>
    internal static bool IsEnabled => Enabled;

    /// <summary>
    /// Record one decision. Inputs and outputs are serialized as JSON using a
    /// deterministic key order and invariant culture; floating-point values are
    /// rounded to <paramref name="floatDigits"/> digits so golden diffs stay
    /// stable across platforms. No-op unless enabled.
    /// </summary>
    internal static void Record(
        string family,
        string decision,
        object? inputs,
        object? outputs,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        int floatDigits = 6)
    {
        if (!Enabled) return;

        var record = new JsonObject
        {
            ["family"] = family,
            ["decision"] = decision,
            ["source"] = $"{Path.GetFileName(sourceFile)}:{sourceLine}",
            ["inputs"] = ToJson(inputs, floatDigits),
            ["outputs"] = ToJson(outputs, floatDigits),
        };

        string line = record.ToJsonString(_compact) + Environment.NewLine;
        string dir = BasePath;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{family}.jsonl");
        File.AppendAllText(path, line);
    }

    /// <summary>
    /// Serialize an arbitrary object to a <see cref="JsonNode"/> with sorted
    /// dictionary keys and rounded floats. null becomes an empty object so the
    /// caller is forced to supply meaningful inputs/outputs.
    /// </summary>
    internal static JsonNode ToJson(object? value, int floatDigits = 6)
    {
        if (value is null) return new JsonObject();
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var json = JsonSerializer.Serialize(value, value.GetType(), options);
        using var doc = JsonDocument.Parse(json);
        return ToNode(doc.RootElement, floatDigits);
    }

    private static JsonNode ToNode(JsonElement element, int floatDigits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    obj[prop.Name] = ToNode(prop.Value, floatDigits);
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    arr.Add(ToNode(item, floatDigits));
                return arr;
            case JsonValueKind.Number:
                if (element.TryGetDouble(out var d))
                {
                    if (double.IsNaN(d)) return JsonValue.Create("NaN")!;
                    if (double.IsInfinity(d)) return JsonValue.Create(d > 0 ? "Infinity" : "-Infinity")!;
                    return JsonValue.Create(Math.Round(d, floatDigits, MidpointRounding.AwayFromZero))!;
                }
                return JsonValue.Create(element.GetRawText())!;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString())!;
            case JsonValueKind.True:
                return JsonValue.Create(true)!;
            case JsonValueKind.False:
                return JsonValue.Create(false)!;
            case JsonValueKind.Null:
                return JsonNode.Parse("null")!;
            default:
                return JsonValue.Create(element.GetRawText())!;
        }
    }

    private static readonly JsonSerializerOptions _compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Produce a readable line-oriented diff between two JSON strings. Used by
    /// golden tests to report mismatches.
    /// </summary>
    internal static string Diff(string expected, string actual)
    {
        var expectedLines = expected.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var actualLines = actual.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int max = Math.Max(expectedLines.Length, actualLines.Length);
        var sb = new StringBuilder();
        for (int i = 0; i < max; i++)
        {
            string e = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            string a = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (e != a)
            {
                sb.AppendLine($"--- line {i + 1}");
                sb.AppendLine($"expected: {e}");
                sb.AppendLine($"actual:   {a}");
            }
        }
        return sb.Length == 0 ? "(no diff)" : sb.ToString();
    }
}
