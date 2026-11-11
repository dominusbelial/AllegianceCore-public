using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P996 — the BOTS half: a seeded loopback showcase in which bots really do get
/// podded and their pods really do die, and NO bot ever loops on an oxygen death.
///
/// <para>The rig bug was a stale lifepod deadline: after a pod death the player
/// respawns berthed in a pod hull, and the next launch died of oxygen ~1 tick
/// later ("[oxygen] … exhausted at sim-t 1030.7 (deadline 926.25)" 6 ms after
/// "[undock]"). The bots already avoid the client half of that loop — they
/// re-buy a real ROLE hull on every relaunch (<c>bot.HullId</c>,
/// <c>BotManager.Lifecycle.cs:216</c>, chosen by
/// <c>RepickRelaunchLoadout</c>/<c>TechLoadoutSelector</c>, never the pod, and
/// <c>Catalog.IsLifepodHull</c> is what marks the pod state) — so this test pins
/// that they also never show the server half: an oxygen death within 1 s of that
/// same ship's launch.</para>
///
/// <para>The oxygen endurance is compressed (<c>liveSettings.lifepodEnduranceSeconds
/// = 5</c>, the P378 tunable) so every pod that cannot reach a base in five
/// seconds really does die of oxygen inside the observation window — otherwise a
/// pod's native 300 s clock would never expire in a short test and the assertion
/// would be vacuous. <see cref="Ship.KillingHit"/> (<see cref="HitKind.Oxygen"/>)
/// and <see cref="Ship.LastLaunchTime"/> are the structural counterparts of the
/// "[oxygen] … exhausted" / "[undock]" log lines, and the captured log is also
/// parsed per ship to prove the counts agree.</para>
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P996BotsLifepodNoOxygenLoopLiveTests(ITestOutputHelper output)
{
    private const int TeamKills = 1000;      // keep the deathmatch running for the whole window
    private const double ObserveSeconds = 200;
    private const double PodEnduranceSeconds = 5;

    [SkippableFact]
    public void Seeded_showcase_bots_get_podded_and_never_die_of_oxygen_right_after_a_launch()
    {
        Require.Artwork();
        string logPath = Path.Combine(Path.GetTempPath(), $"p996-bots-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv();
            var config = P660NextMatchLiveTests.Config(false);
            config.BenchmarkSeed = 996;
            config.LiveSettings.TestHarness = null;
            config.LiveSettings.TestUnlock = null;
            // P378 tunable: compress the pod oxygen clock so pods really die here.
            config.LiveSettings.LifepodEnduranceSeconds = PodEnduranceSeconds;
            config.Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = 16 };
            config.Games[0].Map = "";
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 996,
                PlayerSectorAsteroids = 8, PlayerSectorMineableAsteroids = 4, PlayerSectorSpecialAsteroids = 8 };
            config.Games[0].Goals = new() { TeamKills = TeamKills };
            config.Games[0].MaxTeamImbalance = 0x7fff;
            config.Games[0].CivIds = [25, 25];
            server.Boot(config);
            var mission = server.Missions[0];

            using var stop = new CancellationTokenSource();
            var gate = new object();
            Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(20); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"kills","count_a":12,"count_b":12,"loadout_mode":"best"}""")!;
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));

                // Structural record of every pod observation and every pod death.
                var drifingPods = new HashSet<short>();
                var podDeaths = new HashSet<(short Ship, double Time)>();
                var oxygenDeaths = new List<(short Ship, double At, double Launched)>();
                var seenKills = new HashSet<(short Ship, double Time)>();
                var watch = Stopwatch.StartNew();
                double startedAt = double.NaN;
                while (watch.Elapsed.TotalSeconds < ObserveSeconds && mission.Stage != MissionStage.Over)
                {
                    lock (gate)
                    {
                        if (double.IsNaN(startedAt) && mission.Stage == MissionStage.Started) startedAt = server.SimNow;
                        foreach (var live in server.Ships)
                        {
                            var ship = live.Ship;
                            var hit = ship.KillingHit;
                            bool fresh = seenKills.Add((ship.Id, hit.Time));
                            if (!ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;
                            if (ship.StationId is null && !ship.Dead) drifingPods.Add(ship.Id);
                            if (hit.Kind is HitKind.Oxygen or HitKind.Gun or HitKind.Missile or HitKind.Blast
                                && fresh)
                                podDeaths.Add((ship.Id, hit.Time));
                            if (hit.Kind == HitKind.Oxygen && fresh)
                                oxygenDeaths.Add((ship.Id, hit.Time, ship.LastLaunchTime));
                        }
                    }
                    Assert.Null(tickerError);
                    Thread.Sleep(20);
                }
                Assert.Null(tickerError);
                Assert.False(double.IsNaN(startedAt), "the seeded match never started");

                // ── the scenario really did exercise the class of state ──────────
                Assert.True(drifingPods.Count > 0, "no bot was ever podded — the scenario proves nothing about lifepods");
                Assert.True(podDeaths.Count > 0, "no pod ever died");
                Assert.True(oxygenDeaths.Count > 0,
                    $"no pod died of oxygen with a {PodEnduranceSeconds}s clock — the stale-deadline rule would be vacuous");

                // ── the rule: no oxygen death within 1 s of that ship's launch ───
                var loops = oxygenDeaths.Where(d => d.At - d.Launched <= 1.0).ToList();
                foreach (var d in loops)
                    output.WriteLine($"[p996-bots] LOOP ship {d.Ship}: oxygen death at sim-t {d.At:F2}, launched at {d.Launched:F2} "
                        + $"({d.At - d.Launched:F2}s later)");
                Assert.Empty(loops);

                // Per-ship oxygen-death counts must stay small (no ship cycling:
                // a looping ship would accumulate them one launch apart).
                var perShip = oxygenDeaths.GroupBy(d => d.Ship).ToDictionary(g => g.Key, g => g.Count());
                foreach (var (shipId, count) in perShip)
                    output.WriteLine($"[p996-bots] ship {shipId}: {count} oxygen death(s)");

                // The captured log must agree with the structural count (the
                // "[oxygen] lifepod ship N oxygen exhausted" line is the same fact).
                log.Flush();
                var text = File.ReadAllText(logPath);
                var logged = Regex.Matches(text, @"\[oxygen\] lifepod ship (\d+) oxygen exhausted")
                    .Select(m => short.Parse(m.Groups[1].Value)).GroupBy(id => id)
                    .ToDictionary(g => g.Key, g => g.Count());
                foreach (var (shipId, count) in perShip)
                    Assert.Equal(count, logged.GetValueOrDefault(shipId));
                output.WriteLine($"[p996-bots] seed=996 wall={watch.Elapsed.TotalSeconds:F0}s podsDrifting={drifingPods.Count} "
                    + $"podDeaths={podDeaths.Count} oxygenDeaths={oxygenDeaths.Count} "
                    + $"launchGapMin={(oxygenDeaths.Count == 0 ? double.NaN : oxygenDeaths.Min(d => d.At - d.Launched)):F2}s "
                    + $"log={logPath}");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}
