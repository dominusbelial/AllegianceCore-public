using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P986/P987 — opt-in recording of bot decision INPUTS and outputs. Disabled
/// recording is one static Boolean check at each instrumented decision. Enabled
/// recording writes compact, canonical, versioned JSONL; P987's replay test
/// deduplicates and samples that raw stream into the reviewed corpus.
/// </summary>
internal static class BotDecisionRecorder
{
    internal const string EnableVar = "ALLEGIANCE_BOT_RECORD_DECISIONS";
    internal const string PathVar = "ALLEGIANCE_BOT_RECORD_PATH";
    internal const string ScenarioVar = "ALLEGIANCE_BOT_RECORD_SCENARIO";
    internal const int FormatVersion = 2;

    private static readonly bool Enabled = Environment.GetEnvironmentVariable(EnableVar) == "1";
    private static readonly string BasePath = Environment.GetEnvironmentVariable(PathVar)
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p987", "bot-decisions");
    private static readonly string Scenario = Environment.GetEnvironmentVariable(ScenarioVar) ?? "unlabelled";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.Ordinal);
    private static long _records, _botTicks, _stopwatchTicks;

    /// <summary>True only when the process was started with recording enabled.</summary>
    internal static bool IsEnabled => Enabled;

    /// <summary>Count one combat-bot decision tick for the ON-cost measurement.</summary>
    internal static void BotTick()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _botTicks);
    }

    /// <summary>Record one versioned input snapshot + output record.</summary>
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
        long started = Stopwatch.GetTimestamp();
        var record = new JsonObject
        {
            ["version"] = FormatVersion,
            ["scenario"] = Scenario,
            ["family"] = family,
            ["decision"] = decision,
            ["source"] = $"{Path.GetFileName(sourceFile)}:{sourceLine}",
            ["inputs"] = ToJson(inputs, floatDigits),
            ["outputs"] = ToJson(outputs, floatDigits),
        };

        string line = record.ToJsonString(Compact) + "\n";
        lock (Sync)
        {
            Directory.CreateDirectory(BasePath);
            if (!Writers.TryGetValue(family, out var writer))
            {
                writer = new StreamWriter(Path.Combine(BasePath, $"{family}.jsonl"), append: true,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
                Writers.Add(family, writer);
            }
            writer.Write(line);
            _records++;
        }
        Interlocked.Add(ref _stopwatchTicks, Stopwatch.GetTimestamp() - started);
    }

    /// <summary>Write recorder-ON serialization/I/O cost and volume when a manager closes.</summary>
    internal static void WriteMetrics()
    {
        if (!Enabled) return;
        lock (Sync)
        {
            Directory.CreateDirectory(BasePath);
            foreach (var writer in Writers.Values) writer.Flush();
            long ticks = Interlocked.Read(ref _stopwatchTicks);
            long botTicks = Interlocked.Read(ref _botTicks);
            double microseconds = botTicks == 0 ? 0
                : ticks * (1_000_000.0 / Stopwatch.Frequency) / botTicks;
            var metrics = new JsonObject
            {
                ["version"] = FormatVersion,
                ["scenario"] = Scenario,
                ["records"] = _records,
                ["botTicks"] = botTicks,
                ["recorderStopwatchTicks"] = ticks,
                ["microsecondsPerBotTick"] = Math.Round(microseconds, 6, MidpointRounding.AwayFromZero),
                ["measurement"] = "BotDecisionRecorder.Record serialization plus JSONL I/O / DriveCombat calls; excludes the BotTick integer increment and final metrics write",
            };
            File.WriteAllText(Path.Combine(BasePath, "recorder-metrics.json"),
                metrics.ToJsonString(Indented) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>
    /// Build the stable reviewed corpus from raw JSONL. Exact duplicate decisions
    /// are removed, then each scenario/family/decision contributes at most
    /// <paramref name="samplePerDecision"/> records selected by content hash. IDs
    /// are hashes of scenario + decision + inputs + outputs, never wall clock or
    /// peer identity. Whole-match order and timing are deliberately discarded.
    /// </summary>
    internal static JsonArray BuildCorpus(IEnumerable<string> paths, int samplePerDecision = 16)
    {
        if (samplePerDecision <= 0) throw new ArgumentOutOfRangeException(nameof(samplePerDecision));
        var unique = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonNode.Parse(line)!.AsObject();
            if (raw["version"]?.GetValue<int>() != FormatVersion)
                throw new InvalidDataException($"Unsupported bot decision record in {path}; expected version {FormatVersion}");
            string scenario = raw["scenario"]!.GetValue<string>();
            string family = raw["family"]!.GetValue<string>();
            string decision = raw["decision"]!.GetValue<string>();
            string inputs = raw["inputs"]!.ToJsonString(Compact);
            string outputs = raw["outputs"]!.ToJsonString(Compact);
            string identity = string.Join("\n", scenario, family, decision, inputs, outputs);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
            var item = new JsonObject
            {
                ["id"] = $"{scenario}/{family}.{decision}/{hash}",
                ["version"] = FormatVersion,
                ["scenario"] = scenario,
                ["family"] = family,
                ["decision"] = decision,
                ["inputs"] = JsonNode.Parse(inputs),
                ["outputs"] = JsonNode.Parse(outputs),
            };
            unique.TryAdd(identity, item);
        }

        var result = new JsonArray();
        foreach (var group in unique.Values.GroupBy(r =>
                     (Scenario: r["scenario"]!.GetValue<string>(), Family: r["family"]!.GetValue<string>(),
                      Decision: r["decision"]!.GetValue<string>()))
                     .OrderBy(g => g.Key.Scenario, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Family, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Decision, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
            // Preserve rare outcomes (an armed finale, a released wave, a pod)
            // before filling the remaining budget by stable hash. Pure hash
            // sampling otherwise tends to keep only the common false/null output.
            var selected = ordered.GroupBy(r => r["outputs"]!.ToJsonString(Compact), StringComparer.Ordinal)
                .Select(g => g.First()).Take(samplePerDecision).ToList();
            if (selected.Count < samplePerDecision)
            {
                var chosen = selected.Select(r => r["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                selected.AddRange(ordered.Where(r => !chosen.Contains(r["id"]!.GetValue<string>()))
                    .Take(samplePerDecision - selected.Count));
            }
            foreach (var record in selected) result.Add(record);
        }
        return result;
    }

    /// <summary>Canonical JSON used both by live records and by replay output.</summary>
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
        string json = JsonSerializer.Serialize(value, value.GetType(), options);
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
                foreach (var item in element.EnumerateArray()) arr.Add(ToNode(item, floatDigits));
                return arr;
            case JsonValueKind.Number:
                // Preserve integral ids, prices and long.MaxValue exactly. Only
                // floating-point observations are rounded for corpus stability.
                if (element.TryGetInt64(out long integer)) return JsonValue.Create(integer)!;
                if (element.TryGetUInt64(out ulong unsigned)) return JsonValue.Create(unsigned)!;
                if (element.TryGetDouble(out double d))
                {
                    if (double.IsNaN(d)) return JsonValue.Create("NaN")!;
                    if (double.IsInfinity(d)) return JsonValue.Create(d > 0 ? "Infinity" : "-Infinity")!;
                    return JsonValue.Create(Math.Round(d, floatDigits, MidpointRounding.AwayFromZero))!;
                }
                return JsonValue.Create(element.GetRawText())!;
            case JsonValueKind.String: return JsonValue.Create(element.GetString())!;
            case JsonValueKind.True: return JsonValue.Create(true)!;
            case JsonValueKind.False: return JsonValue.Create(false)!;
            case JsonValueKind.Null: return JsonNode.Parse("null")!;
            default: return JsonValue.Create(element.GetRawText())!;
        }
    }

    internal static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    internal static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Produce a readable line-oriented diff for replay failures.</summary>
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
            if (e == a) continue;
            sb.AppendLine(CultureInfo.InvariantCulture, $"--- line {i + 1}");
            sb.AppendLine($"expected: {e}");
            sb.AppendLine($"actual:   {a}");
        }
        return sb.Length == 0 ? "(no diff)" : sb.ToString();
    }
}
