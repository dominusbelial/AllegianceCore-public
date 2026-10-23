using System.Diagnostics;
using System.Text.Json;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P796 — the loopback showcase coverage run: a real in-process showcase
/// (varied loadouts, the new default) must produce real coverage rows from
/// the events the bots already receive — purchases/launches, gun triggers,
/// hull hits, damage, kills and expendable launches — and the
/// get_weapon_coverage tool must return them. Also asserts the varied
/// rotation actually widened the loadout spread (multiple distinct parts
/// equipped across the two sides).
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P796CoverageLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Loopback_showcase_with_varied_loadouts_builds_weapon_coverage()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = P733ResearchTests.Config(64);
        config.Games[0].MapGen = null; config.Games[0].Map = "m2xspiderw.igc";
        config.Games[0].MaxPlayersPerTeam = 40;
        server.Boot(config);
        var simGate = new object();
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            { lock (simGate) server.TickSim(); Thread.Sleep(20); }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            // loadout_mode defaults to "varied" for preset:"showcase" — pinned
            // here explicitly so the run documents what it ran.
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","count_a":15,"count_b":15,"target_sector":"Alderan","staging":true,"finale_after_minutes":0,"loadout_mode":"varied"}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));

            // Wait for real combat observed by the coverage tracker (the loop
            // sweeps each bot's world ~1 Hz): purchases, triggers, hits, kills.
            // The brief's ≥80 % gate is over FIRE-CAPABLE parts (guns and
            // launchers — shields/afterburners never fire by nature).
            var watch = Stopwatch.StartNew();
            double share = 0;
            while (watch.Elapsed.TotalMinutes < 8)
            {
                var json = System.Text.Json.JsonSerializer.SerializeToElement(manager.Coverage.Json());
                var parts = json.GetProperty("parts").EnumerateArray().ToList();
                long fires = parts.Sum(p => p.GetProperty("fired").GetInt64());
                long hits = parts.Sum(p => p.GetProperty("hits").GetInt64());
                double damage = parts.Sum(p => p.GetProperty("damage").GetDouble());
                int equippedParts = parts.Count(p => p.GetProperty("equipped").GetInt32() > 0);
                var capable = parts.Where(p => p.GetProperty("kind").GetString() is not ("shield" or "afterburner" or "cloak")).ToList();
                int capableEquipped = capable.Count(p => p.GetProperty("equipped").GetInt32() > 0);
                int capableFired = capable.Count(p => p.GetProperty("fired").GetInt64() > 0);
                share = 100.0 * capableFired / Math.Max(1, capableEquipped);
                int firedParts = parts.Count(p => p.GetProperty("fired").GetInt64() > 0);
                var hulls = json.GetProperty("hulls").EnumerateArray().ToList();
                long kills = hulls.Sum(h => h.GetProperty("killed").GetInt64());
                long bought = hulls.Sum(h => h.GetProperty("bought").GetInt64());
                long expFires = json.GetProperty("expendables").EnumerateArray().Sum(e => e.GetProperty("fired").GetInt64());
                if (fires > 30 && hits > 5 && kills > 0 && damage > 50 && bought > 10 && equippedParts >= 8 && expFires > 0
                    && (share >= 80 || watch.Elapsed.TotalMinutes >= 7))
                {
                    output.WriteLine($"coverage after {watch.Elapsed.TotalSeconds:F0}s: equippedParts={equippedParts} firedParts={firedParts} fires={fires} hits={hits} damage={damage:F0} kills={kills} bought={bought} expendableFires={expFires}");
                    output.WriteLine($"fireCapableShare={share:F0}% ({capableFired}/{capableEquipped})");
                    break;
                }
                Thread.Sleep(2000);
            }
            var final = System.Text.Json.JsonSerializer.SerializeToElement(manager.Coverage.Json());
            var finalParts = final.GetProperty("parts").EnumerateArray().ToList();
            Assert.True(finalParts.Count(p => p.GetProperty("equipped").GetInt32() > 0) >= 8,
                "varied showcase equipped too few distinct parts");
            Assert.True(finalParts.Sum(p => p.GetProperty("fired").GetInt64()) > 30, "no real gun triggers observed");
            Assert.True(finalParts.Sum(p => p.GetProperty("hits").GetInt64()) > 5, "no real hull hits observed");
            Assert.True(final.GetProperty("hulls").EnumerateArray().Sum(h => h.GetProperty("killed").GetInt64()) > 0, "no kills observed");
            Assert.True(share >= 80, $"only {share:F0}% of fire-capable equipped parts fired within the window");
            // The MCP tool path returns the same rows.
            var tool = McpTools.Handle("get_weapon_coverage", new Dictionary<string, JsonElement>(), manager);
            Assert.False(tool.IsError ?? false, "get_weapon_coverage errored");
            Assert.Contains("parts", System.Text.Json.JsonSerializer.Serialize(tool));
            foreach (var line in manager.Coverage.Table()) output.WriteLine(line);
        }
        finally
        {
            stop.Cancel();
            Assert.True(ticker.Join(5000));
        }
    }
}
