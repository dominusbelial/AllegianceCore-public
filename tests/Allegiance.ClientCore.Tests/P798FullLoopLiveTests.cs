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
/// P798b — the STANDING REQUIREMENT proof: one unattended showcase match with
/// native fog ON (no <c>showMap</c>, no <c>target_sector</c>) that runs the whole
/// loop by itself and then starts the next match. Per side the test requires, all
/// from data the server really simulated (never a bot claim):
/// <list type="number">
/// <item>miners mining and ore leaving the rocks, and a payday credit arriving in
/// a player's wallet;</item>
/// <item>research progressing (the research commander's plan step/progress moves);</item>
/// <item>a constructor building a station on a rock — and that rock must be one
/// the building side had DISCOVERED (it is in that side's own intel rocks; the
/// constructor choice reads the side's per-side <c>ClientWorld.Asteroids</c>);</item>
/// <item>at least one station kill (a station the server had disappears);</item>
/// <item>a game over, then the next match reaching <c>MissionStage.Started</c>.</item>
/// </list>
///
/// <para><b>Config note.</b> The showcase runs with the harness' all-tech grant
/// (<c>TestUnlock</c>) and test money so the finale can actually buy a bomber
/// strike inside a 9-minute wall window: P778a measured that a RESEARCH-mode
/// showcase never clears the bomber tech gate (docs/reports/P778A_BOMBER_PUSH.md
/// §3), so research mode could not prove a station kill in ten minutes. The
/// research adviser still runs its plan (and is asserted progressing); only the
/// buyability gate is lifted. Fog is native: nothing here reveals the map.</para>
///
/// <para><b>Sampling.</b> Every server read happens on the tick thread that owns
/// the mission collections (a snapshot, then the test thread reads the snapshot):
/// the first version polled the live Sector lists from the test thread and died
/// with \"Collection was modified\" at 6m16s.</para>
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P798FullLoopLiveTests(ITestOutputHelper output)
{
    private static string PlanDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "showcase-plans"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", "showcase-plans");
    }

    private static Srv Boot()
    {
        var server = new Srv();
        server.Boot(new()
        {
            Port = 0,
            SideSelectAutoSeatSeconds = 30,
            // P798b — the REAL research showcase configuration (the rig's own):
            // native fog, native wallets, the research commander buying every
            // development/constructor/expansion through real buckets, accelerated
            // (income x100, research x32) so a 10-minute wall window still covers
            // the whole loop. The research finale also arms the server's bounded
            // finish (FinaleTimeoutSeconds) — the production game-over path used
            // when a strike cannot level a 20k-armor garrison in time.
            Showcase = new() { TechMode = "research", IncomeMultiplier = 1000, ResearchTimeScale = 64, FinaleTimeoutSeconds = 3600 },
            LiveSettings = new()
            {
                TimeDilation = "off",
                AutoNextMatch = new() { Enabled = true, IntermissionSeconds = 3 },
            },
            Games = [new() { Name = "P798 full loop", Map = "training_6.igc", Teams = 2, CivIds = [25, 3],
                SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 40,
                ResearchPlanDirectory = PlanDirectory(), ResearchPlans = new() { [0] = "auto", [1] = "auto" } }],
        });
        return server;
    }

    private readonly record struct Sample(
        Mission Mission, int Miners0, int Miners1, float Ore,
        Dictionary<short, short> Stations, (short Side, short Rock, short Sector)? Build);

    private static Dictionary<short, short> Stations(Mission m)
    {
        var map = new Dictionary<short, short>();
        foreach (var sector in m.Sectors)
            foreach (var station in sector.StationRuntimes)
                map[station.StationId] = station.SideId;
        return map;
    }

    private static (short Side, short Rock, short Sector)? AnyBuild(Mission m)
    {
        foreach (var sector in m.Sectors)
            foreach (var rock in sector.Mineable)
                if (rock.BuildingEffect is { } effect)
                    return (effect.SideId, rock.Data.AsteroidId, rock.Data.ClusterId);
        return null;
    }

    private static int MinerCount(Mission m, short side) => m.Sectors
        .SelectMany(s => s.Models).OfType<Ship>()
        .Count(ship => ship.SideId == side && !ship.Dead && ship.StationId is null
            && ship.HullType.HasCapability(DataHullTypeIGC.HabmMiner));

    private static float TotalOre(Mission m) => m.Sectors.SelectMany(s => s.Mineable).Sum(a => a.OreRemaining);

    [SkippableFact]
    public void Fogged_showcase_runs_the_whole_loop_unattended_and_starts_the_next_match()
    {
        Require.Artwork();
        using var server = Boot();
        using var stop = new CancellationTokenSource();
        var gate = new object();
        Sample sample = default;
        // The tick thread owns the mission collections: it also takes the sample.
        var ticker = new Thread(() =>
        {
            int tick = 0;
            while (!stop.IsCancellationRequested)
            {
                server.TickSim();
                if (tick++ % 5 == 0)
                {
                    var mission = server.Missions[0];
                    var s = new Sample(mission, MinerCount(mission, 0), MinerCount(mission, 1),
                        TotalOre(mission), Stations(mission), AnyBuild(mission));
                    lock (gate) sample = s;
                }
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":1,"staging":true,"finale_after_minutes":0.25,"finale_side":"both","finale_min_relaunch_share":0.05,"finale_min_kills_last_5_minutes":1,"finale_bombers":12,"loadout_mode":"best"}""")!;
            var spawn = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(spawn.IsError ?? false, JsonSerializer.Serialize(spawn));

            var watch = Stopwatch.StartNew();
            Mission first;
            lock (gate) first = sample.Mission;
            if (first is null) { while (first is null) { Thread.Sleep(50); lock (gate) first = sample.Mission; } }
            float startOre;
            long startTreasury;
            lock (gate)
            {
                startOre = sample.Ore;
                startTreasury = sample.Mission.Sides.Sum(s => s.Money);
            }
            var seenMiners = new Dictionary<short, int>();
            float minOre = startOre;
            var research = new Dictionary<short, (int Step, int Total, double Progress)>();
            var paydays = new Dictionary<short, int>();
            var walletMax = new Dictionary<short, int>();
            var lastWallet = new Dictionary<short, int>();
            (double At, short Side, short Rock, short Sector)? build = null;
            (double At, short Side, short Station)? kill = null;
            double? gameOverAt = null, nextStartedAt = null;
            bool rotated = false;
            Dictionary<short, short> lastStations;
            lock (gate) lastStations = sample.Stations;

            while (watch.Elapsed.TotalSeconds < 600)
            {
                double now = watch.Elapsed.TotalSeconds;
                Sample s;
                lock (gate) s = sample;
                if (!ReferenceEquals(s.Mission, first) && !rotated)
                {
                    rotated = true;
                    gameOverAt ??= now;
                    output.WriteLine($"[p798-loop] t={now:F0}s game over: mission rotated");
                }
                if (!rotated)
                {
                    seenMiners[0] = Math.Max(seenMiners.TryGetValue(0, out int m0) ? m0 : 0, s.Miners0);
                    seenMiners[1] = Math.Max(seenMiners.TryGetValue(1, out int m1) ? m1 : 0, s.Miners1);
                    minOre = Math.Min(minOre, s.Ore);
                    if (kill is null)
                    {
                        var gone = lastStations.Keys.Except(s.Stations.Keys).FirstOrDefault((short)-1);
                        if (gone >= 0) kill = (now, lastStations.GetValueOrDefault(gone, (short)-1), gone);
                    }
                    lastStations = s.Stations;
                    if (build is null && s.Build is { } b)
                    {
                        build = (now, b.Side, b.Rock, b.Sector);
                        output.WriteLine($"[p798-loop] t={now:F0}s side {b.Side} constructor building on rock {b.Rock} in sector {b.Sector}");
                    }
                }
                else if (s.Mission.Stage == MissionStage.Started && nextStartedAt is null)
                {
                    nextStartedAt = now;
                    output.WriteLine($"[p798-loop] t={now:F0}s next match started");
                }
                if (nextStartedAt is not null) break;

                // Research progress + payday credits (the research peer is a seated
                // player, so the native per-player payday share lands in its wallet,
                // fsmission.cpp:2310-2324; Payday(114) credits the client wallet).
                foreach (var row in manager.ResearchStatus())
                {
                    var prev = research.GetValueOrDefault(row.Side, (0, row.TotalSteps, 0));
                    research[row.Side] = (Math.Max(prev.Item1, row.Step), row.TotalSteps, Math.Max(prev.Item3, row.ProgressPercent));
                    int prevMoney = lastWallet.GetValueOrDefault(row.Side, row.Money);
                    if (row.Money > prevMoney + 500) paydays[row.Side] = paydays.GetValueOrDefault(row.Side) + 1;
                    lastWallet[row.Side] = row.Money;
                    walletMax[row.Side] = Math.Max(walletMax.GetValueOrDefault(row.Side), row.Money);
                }
                Thread.Sleep(200);
            }

            double elapsed = watch.Elapsed.TotalSeconds;
            var intelRows = JsonSerializer.SerializeToElement(manager.SideIntelReport());
            var discovered = new Dictionary<short, HashSet<short>>();
            foreach (var row in intelRows.EnumerateArray())
            {
                short side = row.GetProperty("side").GetInt16();
                discovered[side] = row.GetProperty("rocks").EnumerateArray()
                    .Select(r => r.GetProperty("id").GetInt16()).ToHashSet();
                output.WriteLine($"[p798-loop] intel side {side}: sectors={row.GetProperty("knownSectors").GetArrayLength()} "
                    + $"rocks={row.GetProperty("rockCount").GetInt32()} "
                    + $"enemyStations={row.GetProperty("enemyStations").GetArrayLength()} "
                    + $"raids={row.GetProperty("minerRaids").GetInt32()} support={row.GetProperty("supportCalls").GetInt32()} "
                    + $"explorationComplete={row.GetProperty("opening").GetProperty("explorationComplete").GetBoolean()}");
            }
            foreach (var (side, miners) in seenMiners.OrderBy(kv => kv.Key))
                output.WriteLine($"[p798-loop] side {side}: minersSeen={miners} paydayCredits={paydays.GetValueOrDefault(side)} "
                    + $"walletMax={walletMax.GetValueOrDefault(side)} research={research.GetValueOrDefault(side)}");
            output.WriteLine($"[p798-loop] ore {startOre:F0} -> {minOre:F0} (mined {startOre - minOre:F0}); treasury start={startTreasury}; "
                + $"build={build}; kill={kill}; gameOverAt={gameOverAt:F0}s; nextStartedAt={nextStartedAt:F0}s");

            // 1) economy: miners in space, ore actually leaving the rocks, payday credits.
            Assert.All(seenMiners.Values, m => Assert.True(m > 0, "each side must have flying miners"));
            Assert.True(startOre - minOre > 100, $"miners must actually mine (ore left the rocks: {startOre - minOre:F0})");
            Assert.All(new short[] { 0, 1 }, side => Assert.True(paydays.GetValueOrDefault(side) > 0,
                $"side {side} must receive a payday credit into a player wallet"));
            // 2) research progressing per side.
            Assert.All(new short[] { 0, 1 }, side =>
            {
                Assert.True(research.ContainsKey(side), $"side {side} must run the research commander");
                var r = research[side];
                Assert.True(r.Step > 1 || r.Progress > 0,
                    $"side {side} research must progress (step {r.Step}/{r.Total}, progress {r.Progress:F1}%)");
            });
            // 3) a constructor building on a DISCOVERED rock.
            Assert.True(build is not null, "a constructor must start a build on a rock");
            var (buildAt, buildSide, buildRock, buildSector) = build!.Value;
            Assert.True(discovered.GetValueOrDefault(buildSide)?.Contains(buildRock) ?? false,
                $"side {buildSide} built on rock {buildRock} (sector {buildSector}) without having discovered it (knowledge leak)");
            // 4) a real station kill.
            Assert.True(kill is not null, "at least one station must be destroyed");
            // 5) game over and the next match starting.
            Assert.True(gameOverAt is not null, "the match must reach a game over");
            Assert.True(nextStartedAt is not null, "the next match must start after the game over");
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"[p798-loop] PASS in {elapsed:F0}s wall (build at {buildAt:F0}s, kill at {kill!.Value.At:F0}s, "
                + $"game over {gameOverAt:F0}s, next match {nextStartedAt:F0}s)");
        }
        finally
        {
            stop.Cancel();
            Assert.True(ticker.Join(5000));
        }
    }
}
