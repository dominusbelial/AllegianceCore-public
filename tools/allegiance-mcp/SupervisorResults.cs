using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegiance.Tools.McpSpawner;

internal static class SupervisorResults
{
    private static JsonObject Object(JsonElement data) => data.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(data.GetRawText())!.AsObject() : new();
    private static JsonArray Rows(IEnumerable<JsonElement> data, string key) => new(data
        .Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array)
        .SelectMany(d => d.GetProperty(key).EnumerateArray()).Select(d => JsonNode.Parse(d.GetRawText())).ToArray());
    private static int Int(JsonElement data, string key) => data.TryGetProperty(key, out var value) ? value.GetInt32() : 0;

    internal static JsonElement MergeScenario(WorkerReply[] plans, WorkerReply[] replies, WorkerReply[] states, object[] workers)
    {
        var first = replies.FirstOrDefault(r => r.Data.TryGetProperty("sideA", out _));
        var merged = first is null ? new JsonObject() : Object(first.Data);
        var failures = Rows(replies.Select(r => r.Data), "failures");
        var grants = new JsonArray();
        var counts = new Dictionary<short, (int Requested, int Spawned, int Refilled)>();
        for (int i = 0; i < replies.Length; i++)
        {
            var reply = replies[i];
            JsonElement[] active = states[i].IsError ? [] : states[i].Data.Deserialize<WorkerState>()!.Bots.EnumerateArray()
                .Where(b => b.TryGetProperty("status", out var s) && s.GetString() == "active").ToArray();
            var liveNames = active.Select(b => b.GetProperty("name").GetString()).ToHashSet();
            var workerGrants = reply.Data.TryGetProperty("grants", out var g) ? g.EnumerateArray().ToArray() : [];
            foreach (string sideKey in new[] { "sideA", "sideB" })
            {
                var plan = plans[i].Data.GetProperty(sideKey);
                short side = plan.GetProperty("side").GetInt16();
                int requested = Int(plan, "requested");
                int spawned = 0, refilled = 0;
                if (reply.Data.TryGetProperty(sideKey, out var report) && !states[i].IsError)
                {
                    spawned = workerGrants.Count(bot => Int(bot, "side") == side && liveNames.Contains(bot.GetProperty("name").GetString()));
                    // Refill totals are only trustworthy up to final connected slots.
                    refilled = workerGrants.Any(g => g.TryGetProperty("refilled", out _))
                        ? workerGrants.Count(bot => Int(bot, "side") == side && liveNames.Contains(bot.GetProperty("name").GetString())
                            && bot.TryGetProperty("refilled", out var refilledBot) && refilledBot.GetBoolean())
                        : Math.Min(spawned, Int(report, "refilled"));
                }
                var old = counts.GetValueOrDefault(side);
                counts[side] = (old.Requested + requested, old.Spawned + spawned, old.Refilled + refilled);
            }
            foreach (var bot in workerGrants)
                if (liveNames.Contains(bot.GetProperty("name").GetString())) grants.Add(JsonNode.Parse(bot.GetRawText()));
                else failures.Add(JsonSerializer.SerializeToNode(new { worker = i + 1, name = bot.GetProperty("name").GetString(), side = Int(bot, "side"), error = states[i].IsError ? "worker lost or unresponsive at spawn completion" : "bot disconnected during parallel spawning" }));
            if (reply.IsError && !reply.Data.TryGetProperty("failures", out _)) failures.Add(JsonSerializer.SerializeToNode(new { worker = i + 1, error = reply.Data }));
        }
        foreach (string key in new[] { "sideA", "sideB" })
        {
            short side = plans[0].Data.GetProperty(key).GetProperty("side").GetInt16();
            var count = counts[side];
            var row = merged[key] as JsonObject ?? new JsonObject();
            row["side"] = side; row["requested"] = count.Requested; row["spawned"] = count.Spawned;
            row["failed"] = count.Requested - count.Spawned; row["refilled"] = count.Refilled;
            merged[key] = row;
        }
        merged["grants"] = grants;
        merged["failures"] = failures;
        merged["warnings"] = replies.Sum(r => r.Data.TryGetProperty("warnings", out var w) ? w.GetInt32() : 0);
        merged["minefields"] = Rows(replies.Select(r => r.Data), "minefields");
        merged["workers"] = JsonSerializer.SerializeToNode(workers);
        merged["note"] = "Parallel worker spawn; counts are connected slots at the supervisor's final inventory check. Lost or unresponsive workers contribute zero spawned slots.";
        return JsonSerializer.SerializeToElement(merged);
    }

    // Preserve every measured worker report. Sum additive work/counters, take
    // maxima for latency envelopes; never average independent p95 values.
    internal static JsonNode? Aggregate(JsonNode?[] values, string key = "")
    {
        var present = values.Where(v => v is not null).ToArray();
        if (present.Length == 0) return null;
        if (present[0] is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (string property in present.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct())
                result[property] = Aggregate(present.Select(v => (v as JsonObject)?[property]).ToArray(), property);
            return result;
        }
        if (present[0] is JsonArray) return new JsonArray(present.OfType<JsonArray>().SelectMany(a => a).Select(v => v?.DeepClone()).ToArray());
        if (present[0] is JsonValue value && value.TryGetValue<double>(out _))
        {
            var numbers = present.Select(v => v!.GetValue<double>()).ToArray();
            if (key is "mean" or "p50" or "p95" or "max" or "worstValue" or "sampleAgeSeconds" || key.Contains("Max", StringComparison.Ordinal)
                || key.EndsWith("AgeMs", StringComparison.Ordinal) || key is "watchdogLateMs" or "threadPoolNoProgressMs" or "decisionPassInProgressMs" or "durationMs" or "maxGateHoldMs") return JsonValue.Create(numbers.Max());
            if (key is "decisionRateHz" or "startMonotonicMs") return JsonValue.Create(numbers.Min());
            return JsonValue.Create(numbers.Sum());
        }
        if (present[0] is JsonValue flag && flag.TryGetValue<bool>(out _)) return JsonValue.Create(present.Any(v => v!.GetValue<bool>()));
        return present[0]!.DeepClone();
    }

    internal static JsonElement MergePerf(WorkerReply[] replies, object[] workers, JsonElement[] lost)
    {
        var reports = replies.Where(r => !r.IsError).Select(r => r.Data.GetProperty("Report")).ToArray();
        var all = reports.Select(d => JsonNode.Parse(d.GetRawText())).ToArray();
        var merged = Aggregate(all) as JsonObject ?? new JsonObject();
        var samples = replies.Where(r => !r.IsError).SelectMany(r => r.Data.GetProperty("Cadence").Deserialize<BotCadence[]>()!).ToArray();
        var cadence = BotManager.ComputePerf(samples);
        merged["botCount"] = cadence.BotCount;
        object Stat(MetricStat s) => new { mean = s.Mean, p95 = s.P95, worstBot = s.WorstBot, worstValue = s.WorstValue };
        merged["interval"] = JsonSerializer.SerializeToNode(Stat(cadence.Interval));
        merged["jump"] = JsonSerializer.SerializeToNode(Stat(cadence.Jump));
        if (merged["combat"] is JsonObject combat)
        {
            double shots = combat["shotsFired"]?.GetValue<double>() ?? 0, hits = combat["shipHits"]?.GetValue<double>() ?? 0;
            combat["hitRate"] = shots > 0 ? hits / shots : 0;
            foreach (string side in new[] { "sideA", "sideB" })
            {
                var ttks = all.Select(d => d?["combat"]?["timeToKill"]?[side]).OfType<JsonObject>().ToArray();
                double count = ttks.Sum(t => t["samples"]!.GetValue<double>());
                if (count > 0) combat["timeToKill"]![side] = new JsonObject { ["samples"] = count, ["meanSeconds"] = ttks.Sum(t => t["samples"]!.GetValue<double>() * t["meanSeconds"]!.GetValue<double>()) / count };
            }
        }
        if (merged["routes"] is JsonArray routes)
        {
            var grouped = routes.OfType<JsonObject>().GroupBy(r => (r["side"]!.ToJsonString(), r["routeIndex"]!.ToJsonString(), r["route"]!.ToJsonString()));
            merged["routes"] = new JsonArray(grouped.Select(g => (JsonNode)new JsonObject
            {
                ["side"] = g.First()["side"]!.DeepClone(), ["routeIndex"] = g.First()["routeIndex"]!.DeepClone(), ["route"] = g.First()["route"]!.DeepClone(),
                ["assigned"] = g.Sum(r => r["assigned"]!.GetValue<double>()), ["arrived"] = g.Sum(r => r["arrived"]!.GetValue<double>()),
            }).ToArray());
        }
        merged["lostBots"] = JsonSerializer.SerializeToNode(lost);
        merged["workers"] = JsonSerializer.SerializeToNode(workers.Select((status, i) => new { status, report = replies[i].IsError ? replies[i].Data : replies[i].Data.GetProperty("Report") }));
        merged["aggregation"] = "Cadence mean/p95 use pooled samples. Counters, resources and throughput sum; latency maxima and decision-pass percentiles are worker envelopes; decisionRateHz is the slowest worker. Full worker reports include all P657b stall metrics.";
        return JsonSerializer.SerializeToElement(merged);
    }
}
