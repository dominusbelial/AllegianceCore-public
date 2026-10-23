using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P798 — the loopback opening proof, with native fog ON (no `ShowMap`, no
/// target_sector override): scouts must unveil the map, find the enemy STATION
/// and its miners, the economy clash must produce a miner raid and a support
/// call, and the main clash sector must be chosen from the resources the sides
/// actually saw. Runs on two different real maps.
///
/// <para>P798b adds the two acceptance measurements the first live run lacked:
/// each side must observe an enemy station within N minutes (N is pinned per
/// map below and the measured value is printed), and every rock in a side's
/// intel must be a rock the SERVER really has <c>SeenBySide(side)</c> — the
/// per-side visibility proof, not a count coincidence (m2xspiderw's Alderan
/// holds all 134 rocks, so "rocks=134" on both sides is correct once both have
/// entered Alderan, never before).</para>
///
/// <para>This test fails on master by construction: master reveals the whole
/// map (`ShowMap` in the harness configs) and has no scout/intel/support-call
/// code, so `get_side_intel` does not exist and no miner raid/support call is
/// ever recorded.</para>
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P798ScoutOpeningLiveTests(ITestOutputHelper output)
{
    private static Srv Boot(string map)
    {
        var server = new Srv();
        server.Boot(new()
        {
            Port = 0,
            SideSelectAutoSeatSeconds = 30,
            LiveSettings = new()
            {
                TimeDilation = "off",
                AutoNextMatch = new() { Enabled = false },
                TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
            },
            // P798 — native fog: ShowMap/ShowHomeSector stay at their defaults (off).
            Games = [new() { Name = "P798 opening", Map = map, Teams = 2, CivIds = [25, 3],
                SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 40 }],
        });
        return server;
    }

    private static int TotalAlephs(string map)
    {
        var path = Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), map);
        var m = MissionMap.Load(IgcReader.ParseFile(path));
        return m.Warps.Count;
    }

    /// <summary>Every server asteroid, keyed by id, with its per-side seen state
    /// (the visibility truth the bot intel is audited against).</summary>
    private static Dictionary<short, Asteroid> ServerRocks(Srv server)
    {
        var rocks = new Dictionary<short, Asteroid>();
        foreach (var mission in server.Missions)
            foreach (var sector in mission.Sectors)
                foreach (var rock in sector.Mineable)
                    rocks[rock.Data.AsteroidId] = rock;
        return rocks;
    }

    private sealed record SideProgress(double Share, int Alephs, bool Raid, bool Support,
        short? Clash, double Resource, double? FirstStation, int Rocks);

    [SkippableTheory]
    // N = the pinned acceptance bound for "each side observes an enemy station".
    [InlineData("m2xspiderw.igc", 8, .30, 40, 6)]
    [InlineData("training_6.igc", 6, .90, 40, 3)]
    public void Fogged_showcase_scouts_open_the_map_and_the_clash_forms_on_resources(
        string map, int minutes, double minSectorShare, int minAlephPercent, double stationMinutes)
    {
        Require.Artwork();
        using var server = Boot(map);
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","count_a":10,"count_b":10,"staging":true,"finale_after_minutes":0,"loadout_mode":"best"}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            int totalAlephs = TotalAlephs(map);
            var watch = Stopwatch.StartNew();
            var best = new Dictionary<short, SideProgress>();
            while (watch.Elapsed.TotalSeconds < minutes * 60)
            {
                var rows = JsonSerializer.SerializeToElement(manager.SideIntelReport());
                foreach (var row in rows.EnumerateArray())
                {
                    short side = row.GetProperty("side").GetInt16();
                    double share = row.GetProperty("seenReachableShare").GetDouble();
                    int alephs = row.GetProperty("alephs").GetArrayLength();
                    bool raid = row.GetProperty("minerRaids").GetInt32() > 0;
                    bool support = row.GetProperty("supportCalls").GetInt32() > 0;
                    short? clash = row.GetProperty("clash").ValueKind == JsonValueKind.Null ? null
                        : row.GetProperty("clash").GetProperty("sector").GetInt16();
                    double resource = row.GetProperty("clash").ValueKind == JsonValueKind.Null ? 0
                        : row.GetProperty("clash").GetProperty("resource").GetDouble();
                    double? firstStation = row.GetProperty("opening").GetProperty("firstEnemyStationSeconds").ValueKind
                        == JsonValueKind.Null ? null
                        : row.GetProperty("opening").GetProperty("firstEnemyStationSeconds").GetDouble();
                    int rocks = row.GetProperty("rockCount").GetInt32();
                    var prev = best.GetValueOrDefault(side) ?? new SideProgress(0, 0, false, false, null, 0, null, 0);
                    best[side] = new SideProgress(Math.Max(prev.Share, share), Math.Max(prev.Alephs, alephs),
                        prev.Raid || raid, prev.Support || support, clash ?? prev.Clash,
                        Math.Max(prev.Resource, resource), firstStation ?? prev.FirstStation, Math.Max(prev.Rocks, rocks));
                }
                if (best.Count == 2 && best.Values.All(v =>
                        v.Clash is not null && v.Share >= minSectorShare && v.Alephs * 100 >= totalAlephs * minAlephPercent
                        && v.FirstStation is not null
                        && best.Values.Any(x => x.Raid) && best.Values.Any(x => x.Support)))
                    break;
                Thread.Sleep(500);
            }
            double elapsed = watch.Elapsed.TotalSeconds;
            foreach (var (side, row) in best.OrderBy(kv => kv.Key))
                output.WriteLine($"[p798] map={map} t={elapsed:F0}s side {side}: sectorsSeen={row.Share:P0} "
                    + $"alephs={row.Alephs}/{totalAlephs} ({100.0 * row.Alephs / Math.Max(1, totalAlephs):F0}%) "
                    + $"rocks={row.Rocks} firstEnemyStation={(row.FirstStation is { } fs ? $"{fs:F0}s" : "never")} "
                    + $"minerRaid={row.Raid} supportCall={row.Support} clash={(row.Clash?.ToString() ?? "pending")} resource={row.Resource:F1}");
            var intelJson = JsonSerializer.SerializeToElement(manager.SideIntelReport());
            foreach (var row in intelJson.EnumerateArray())
                output.WriteLine($"[p798-intel] {row.GetRawText()}");

            // ── Per-side visibility proof: every rock this side "knows" is a rock
            // the server really exported to that side (Asteroid.SeenBySide, the
            // P797 per-side static-object gate, fsmission.cpp:4169-4250).
            var serverRocks = ServerRocks(server);
            int audited = 0;
            foreach (var row in intelJson.EnumerateArray())
            {
                short side = row.GetProperty("side").GetInt16();
                var rockSectorRows = row.GetProperty("rockSectors").EnumerateArray()
                    .Select(s => $"{s.GetProperty("sector").GetInt16()}:{s.GetProperty("count").GetInt32()}").ToArray();
                int leaked = 0;
                foreach (var rock in row.GetProperty("rocks").EnumerateArray())
                {
                    short id = rock.GetProperty("id").GetInt16();
                    Assert.True(serverRocks.TryGetValue(id, out var serverRock), $"side {side} reports rock {id} that the server does not have");
                    if (!serverRock!.SeenBySide(side)) leaked++;
                    audited++;
                }
                output.WriteLine($"[p798-rocks] side {side}: {row.GetProperty("rockCount").GetInt32()} rock(s) "
                    + $"in sectors [{string.Join(",", rockSectorRows)}] — {row.GetProperty("rocks").GetArrayLength()} audited, {leaked} not SeenBySide({side})");
                Assert.Equal(0, leaked);
            }
            Assert.True(audited > 0, "at least one side must have seen rocks on these maps");
            output.WriteLine($"[p798-rocks] total audited={audited}, leaks=0");

            Assert.Equal(2, best.Count);
            Assert.All(best.Values, v => Assert.NotNull(v.Clash));
            Assert.All(best.Values, v => Assert.True(v.Share >= minSectorShare,
                $"sector coverage {v.Share:P0} below {minSectorShare:P0}"));
            Assert.All(best.Values, v => Assert.True(v.Alephs * 100 >= totalAlephs * minAlephPercent,
                $"alephs {v.Alephs}/{totalAlephs} below {minAlephPercent}%"));
            // P798b acceptance: each side OBSERVED an enemy station within N minutes.
            Assert.All(best.Values, v => Assert.True(v.FirstStation is { } fs && fs <= stationMinutes * 60,
                $"each side must observe an enemy station within {stationMinutes} min "
                + $"(measured {(v.FirstStation is { } m ? $"{m:F0}s" : "never")})"));
            Assert.Contains(best.Values, v => v.Raid);
            Assert.Contains(best.Values, v => v.Support);
            foreach (var v in best.Values)
                Assert.True(v.Resource > 0, $"clash {v.Clash} must be chosen from resources (scoring is logged in [clash])");
            // Both sides read the SAME resource area: the main clash has one place to form.
            var sectors = best.Values.Select(v => v.Clash).Distinct().ToArray();
            output.WriteLine($"[p798] main clash sector(s): {string.Join(",", sectors)} after {elapsed:F0}s");
            Assert.Single(sectors);
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally
        {
            stop.Cancel();
            Assert.True(ticker.Join(5000));
        }
    }
}
