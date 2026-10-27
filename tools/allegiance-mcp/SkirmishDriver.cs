using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Game-owned headless supervisor. Stdout is a small private protocol; no MCP client required.</summary>
internal static class SkirmishDriver
{
    internal static async Task RunAsync(DriverOptions options, CancellationToken ct)
    {
        if (await Console.In.ReadLineAsync(ct) != "start") throw new IOException("Game ownership handshake missing");
        var counts = options.Skirmish!.Split('/').Select(int.Parse).ToArray();
        if (counts.Length != 2 || counts.Any(n => n is < 1 or > 200)) throw new ArgumentException("--skirmish requires countA/countB");
        using var supervisor = await WorkerSupervisor.StartAsync(options.Host, options.Port, options.Workers, ct);
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        using var writes = new SemaphoreSlim(1);
        async Task Send(object message)
        {
            await writes.WaitAsync(ct);
            try { await output.WriteLineAsync(JsonSerializer.Serialize(message)); }
            finally { writes.Release(); }
        }
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            // P798 — no hard-coded rendezvous: the fleet's scouts find the map
            // and the clash sector is computed per side from the resources seen
            // (the showcase `target_sector` override is intentionally omitted).
            preset = "showcase", count_a = counts[0], count_b = counts[1], staging = true,
            // P837 — a nine-bot skirmish is a MEETING ENGAGEMENT: with native fog
            // (P798) its two scouts cannot route a 13-sector map inside a short
            // battle, so the small fleet fights what it meets while it travels, does
            // not wait for a wing that four ships will never assemble, and commits to
            // its clash as soon as its scaled opening window allows. Larger skirmishes
            // (Large/Epic/Grand) keep the rig's staging/transit discipline.
            meeting_engagement = counts[0] + counts[1] <= 12
        }))!;
        var result = WorkerReply.FromTool(await supervisor.HandleAsync("spawn_scenario", args, ct));
        if (result.IsError) throw new IOException(result.Data.ToString());
        // Partial spawns must be visible as startup failures too.
        foreach (var side in new[] { "sideA", "sideB" })
            if (result.Data.GetProperty(side).GetProperty("failed").GetInt32() != 0)
                throw new IOException("Skirmish fleet incomplete: " + result.Data);
        await Send(new { type = "ready", pids = supervisor.ProcessIds, scenario = result.Data });
        short? playerSide = counts[0] == counts[1] ? null : (short)0;
        var vacantHulls = new Dictionary<short, string>();
        async Task<WorkerReply> Call(string name, JsonObject data)
        {
            using var document = JsonDocument.Parse(data.ToJsonString());
            var args = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var reply = WorkerReply.FromTool(await supervisor.HandleAsync(name, args, ct));
            if (reply.IsError) throw new IOException(reply.Data.ToString());
            return reply;
        }
        async Task Exchange(short? side)
        {
            if (side == playerSide) return;
            var inventory = (await Call("list_active_bots", new JsonObject())).Data.EnumerateArray()
                .Where(b => b.TryGetProperty("status", out var status) && status.GetString() == "active").ToArray();
            // Refill before taking a seat from either side; disconnect is graceful and the
            // game waits for this reply before its ordinary POSITION_REQ connection.
            if (playerSide is { } old)
            {
                string hull = vacantHulls.GetValueOrDefault(old) ?? inventory.Where(b => b.GetProperty("side").GetInt16() == old).OrderBy(b => string.Equals(b.GetProperty("role").GetString(), "fighter", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First().GetProperty("hull").GetString()!;
                await Call("spawn_bot", new JsonObject { ["hull"] = hull, ["side"] = old, ["behavior"] = "combat" });
                playerSide = null;
            }
            if (side is { } next)
            {
                var bot = inventory.Where(b => b.GetProperty("side").GetInt16() == next)
                    .OrderBy(b => string.Equals(b.GetProperty("role").GetString(), "fighter", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First();
                vacantHulls[next] = bot.GetProperty("hull").GetString()!;
                var removed = await Call("despawn_bot", new JsonObject { ["name"] = bot.GetProperty("name").GetString() });
                if (removed.Data.GetProperty("stopped").GetInt32() != 1) throw new IOException("Pilot bot slot was not released");
                playerSide = next;
            }
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var health = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                var stats = WorkerReply.FromTool(await supervisor.HandleAsync("get_perf_stats", null, lifetime.Token));
                await Send(new { type = "health", healthy = supervisor.Healthy && !stats.IsError, stats = stats.Data, pids = supervisor.ProcessIds });
                await Task.Delay(2000, lifetime.Token);
            }
        }, lifetime.Token);
        try
        {
            while (await Console.In.ReadLineAsync(ct) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                string? error = null;
                try
                {
                    switch (root.GetProperty("command").GetString())
                    {
                        case "focus": await supervisor.SetFocusSectorAsync(root.GetProperty("sector").ValueKind == JsonValueKind.Null ? null : root.GetProperty("sector").GetInt16(), ct); break;
                        case "pause": await supervisor.PauseAsync(root.GetProperty("paused").GetBoolean(), ct); break;
                        case "seat": await Exchange(root.GetProperty("side").ValueKind == JsonValueKind.Null ? null : root.GetProperty("side").GetInt16()); break;
                        default: throw new ArgumentException("Unknown command");
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                await Send(new { type = "reply", id = root.GetProperty("id").GetInt64(), error });
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await health; } catch (OperationCanceledException) { }
        }
    }
}
