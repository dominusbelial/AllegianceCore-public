using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P745FinaleSiegeLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_showcase_finale_destroys_a_station()
    {
        string run = Environment.GetEnvironmentVariable("ALLEGIANCE_P745_RUN") ?? "test";
        Require.Artwork();
        string path = Path.Combine(Path.GetTempPath(), $"p745-siege-{run}-{Guid.NewGuid():N}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out;
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            var config = P733ResearchTests.Config(64);
            config.Showcase.FinaleTimeoutSeconds = 900;
            bool spider = Environment.GetEnvironmentVariable("ALLEGIANCE_P745_MAP") == "spider";
            if (spider) { config.Games[0].MapGen = null; config.Games[0].Map = "m2xspiderw.igc"; }
            else if (Environment.GetEnvironmentVariable("ALLEGIANCE_P745_MAP") != "dense-ring")
            {
                // Small native topology for repeatable siege qualification. Research
                // still supplies its ordinary branch rocks and flies constructors.
                var map = config.Games[0].MapGen!;
                map.PlayerSectorAsteroids = map.PlayerSectorSpecialAsteroids = map.PlayerSectorMineableAsteroids = 0;
                map.NeutralSectorAsteroids = map.NeutralSectorSpecialAsteroids = map.NeutralSectorMineableAsteroids = 0;
            }
            config.LiveSettings.AutoNextMatch.Enabled = false;
            server.Boot(config);
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
                    """{"preset":"showcase","size_scale":1,"target_sector":"Alderan","staging":true,"finale_after_minutes":2,"finale_side":"1","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":12}""")!;
                if (!spider)
                    args["target_sector"] = JsonSerializer.SerializeToElement(server.Missions[0].Sectors.First(s => s.Stations.Count == 0).Name);
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var mission = server.Missions[0];
                var watch = Stopwatch.StartNew();
                bool destroyed = false;
                double? firstKill = null;
                // Station ids sampled alive. A dead station is REMOVED from its
                // sector (Sector.RemoveStation, the P125 death contract), so
                // destruction shows up either as a runtime whose armor crossed
                // zero or as a seen id vanishing between samples.
                var seenStations = new HashSet<short>();
                // Retiring the mission clears its side registry (Mission.Lifetime.cs), so keep
                // the sides for the deadline ranking below.
                var sides = mission.Sides.ToArray();
                while (watch.Elapsed.TotalSeconds < 1200 && mission.Stage != MissionStage.Over)
                {
                    var bots = manager.ActiveBots();
                    var snapshot = bots.Where(b => b.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)).Select(b =>
                    {
                        var ship = server.Ships.FirstOrDefault(s => s.Ship.Id == b.ShipId)?.Ship;
                        return new { Bot = b, Hull = ship?.HullType.Name, Sector = ship?.Sector?.Id,
                            ship?.StateM, ship?.Controls, ship?.IsNpc, ship?.PilotType, ship?.GotoWaypoint,
                            ServerPosition = ship?.Position, ServerVelocity = ship?.Velocity,
                            ServerForward = ship?.Orientation.GetForward(), Target = ship?.CommandTarget?.ToString(),
                            Parts = ship?.Parts.ToArray().Select(p => new { p.Type.PartId, p.Type.Name, p.MountId, Firing = p.FunctionalWeapon?.FiringShot }) };
                    }).ToArray();
                    log.WriteLine($"[proof] wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} finale={JsonSerializer.Serialize(manager.FinaleStatus())} bombers={JsonSerializer.Serialize(snapshot)}");
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    object[] roster;
                    lock (typeof(BotManager).GetField("_gate", flags)!.GetValue(manager)!)
                        roster = ((System.Collections.IEnumerable)typeof(BotManager).GetField("_bots", flags)!.GetValue(manager)!).Cast<object>().ToArray();
                    foreach (var bot in roster)
                    {
                        var bt = bot.GetType();
                        if (!(bool)bt.GetField("FinaleBomberActive", flags)!.GetValue(bot)!) continue;
                        var cs = (Allegiance.ClientCore.Session.ConnectionSession)bt.GetField("Cs", flags)!.GetValue(bot)!;
                        lock (cs.Gate)
                        {
                            var id = (short)bt.GetField("ShipId", flags)!.GetValue(bot)!;
                            if (!cs.World.Ships.TryGetValue(id, out var me) || me.Latest is not { } pose) continue;
                            object?[] dodgeArgs = [cs, bot, pose.Position, null];
                            var dodge = typeof(BotManager).GetMethod("TryDodgeGoal", BindingFlags.Static | flags)!.Invoke(null, dodgeArgs);
                            var routePath = (List<short>)bt.GetField("Path", flags)!.GetValue(bot)!;
                            int at = routePath.IndexOf(me.SectorId);
                            short next = at >= 0 && at + 1 < routePath.Count ? routePath[at + 1] : (short)-1;
                            var warp = cs.World.Warps.Values.FirstOrDefault(w => w.ClusterId == me.SectorId
                                && cs.World.Warps.TryGetValue(w.DestinationId, out var dest) && dest.ClusterId == next);
                            log.WriteLine($"[proof-warp] ship={id} next={next} warp={warp.WarpId} pos={string.Join(',', warp.Position ?? [])} ownForward={pose.Orientation.GetForward()}");
                            log.WriteLine($"[proof-route] ship={id} path={string.Join(',', (List<short>)bt.GetField("Path", flags)!.GetValue(bot)!)} dodges={dodge} goal={dodgeArgs[3]} lock={cs.World.MissileLock}");
                            if (id == snapshot.FirstOrDefault()?.Bot.ShipId)
                            {
                                log.WriteLine($"[proof-obstacles] stations={JsonSerializer.Serialize(cs.World.Stations.Values)} rocks={JsonSerializer.Serialize(cs.World.Asteroids.Values)}");
                            }
                        }
                    }
                    var stations = mission.Sectors.SelectMany(s => s.StationRuntimes).ToArray();
                    log.WriteLine($"[proof-stations] {string.Join(";", stations.Select(s => $"{s.StationId}:dead={s.Dead},shield={s.ShieldFraction:F4},hull={s.ArmorFraction:F4}"))}");
                    // P954 — destruction is judged from a LIVE sweep plus the mission
                    // ledger (the same sim evidence the wire summary reports,
                    // Mission.StationKills.cs:21-30). The previous spawn-time
                    // runtime snapshot could only ever see the two home garrisons
                    // die: a run whose bombers killed a constructor-built target
                    // (run p954r3: Alpha=1, Bravo=1 on the wire summary) read as
                    // "No station destruction".
                    destroyed = stations.Any(s => s.Dead)
                        || seenStations.Any(id => stations.All(s => s.StationId != id))
                        || mission.Sides.Any(s => mission.StationKills(s.Id) > 0)
                        || mission.UncreditedStationDestructions > 0;
                    foreach (var s in stations) seenStations.Add(s.StationId);
                    if (destroyed) firstKill ??= watch.Elapsed.TotalSeconds;
                    Thread.Sleep(2000);
                }
                var finalStations = mission.Sectors.SelectMany(s => s.StationRuntimes).ToArray();
                destroyed |= finalStations.Any(s => s.Dead)
                    || seenStations.Any(id => finalStations.All(s => s.StationId != id))
                    || mission.Sides.Any(s => mission.StationKills(s.Id) > 0)
                    || mission.UncreditedStationDestructions > 0;
                string? reason = null;
                var receipt = Stopwatch.StartNew();
                while (string.IsNullOrWhiteSpace(reason) && receipt.Elapsed.TotalSeconds < 5)
                {
                    lock (manager.Probe.Gate) reason = manager.Probe.World.GameOverResult?.GameoverMessage;
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        // The mission can retire and rebuild between GAME_OVER and this
                        // sample, retiring the finale run with it (see the race noted
                        // below): never index an empty status array; the probe's wire
                        // text stays the primary evidence either way.
                        var statuses = manager.FinaleStatus();
                        if (statuses.Length > 0)
                        {
                            var ended = JsonSerializer.SerializeToElement(statuses[0]);
                            if (ended.TryGetProperty("gameOverReason", out var endedReason)
                                && endedReason.ValueKind == JsonValueKind.String)
                                reason = endedReason.GetString();
                        }
                    }
                    if (string.IsNullOrWhiteSpace(reason)) Thread.Sleep(20);
                }
                output.WriteLine($"run={run} destroyed={destroyed} firstKill={firstKill:F1}s stage={mission.Stage} wall={watch.Elapsed.TotalSeconds:F1}s sim={server.SimNow:F1}s reason={reason} log={path}");
                log.WriteLine($"[proof-result] run={run} firstKill={firstKill:F1} wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} reason={reason}");
                Assert.True(destroyed, $"No station destruction; see {path}");
                Assert.Equal(MissionStage.Over, mission.Stage);
                // The server rebuilds the mission after GAME_OVER; that can
                // retire a slow observer's world before it samples the wire text.
                // Station destruction remains mandatory. A timed match may end
                // with active sides: native ranks kills/deaths at the deadline
                // (FedSrv/fsmission.cpp:2468-2498), independently of elimination.
                var eliminated = sides.Where(side => !side.Active).ToArray();
                foreach (var side in eliminated)
                    Assert.DoesNotContain(mission.Sectors.SelectMany(sector => sector.StationRuntimes),
                        station => station.SideId == side.Id && station.Type.HasCapability(Allegiance.Core.DataStationTypeIGC.SabmRestart));
                if (eliminated.Length == 0)
                {
                    var ranked = sides.OrderByDescending(side => side.Kills).ThenBy(side => side.Deaths).ToArray();
                    var best = ranked[0];
                    Assert.True(best.Kills > 0, "The live deadline must see earned ship kills");
                    bool tied = ranked.Skip(1).Any(side => side.Kills == best.Kills && side.Deaths == best.Deaths);
                    // Native's deadline reason leads verbatim: it is the whole GAME_OVER
                    // blob (FedSrv/fsmission.cpp:3063-3065), ranked at fsmission.cpp:2468-2498
                    // ("The clock has stopped. The winner: %s by %s" at :2497, the draw text
                    // at :2502). P914's approved preset addition (docs/reports/P914_REPORT.md,
                    // "Feed and score overlay") appends the station-kill tally to showcase
                    // results only; assert that exact shape, not a bare equality that the
                    // tally breaks. The mission may already be retired here, so pin the
                    // documented line shape rather than re-reading the live ledger.
                    Assert.NotNull(reason);
                    string[] lines = reason.Split('\n');
                    Assert.Equal(tied ? "The clock has stopped. The game is a draw."
                        : $"The clock has stopped. The winner: {best.Name} by kills", lines[0]);
                    if (lines.Length > 1)
                    {
                        Assert.Equal(3, lines.Length);
                        Assert.StartsWith("Station kills: ", lines[1]);
                        Assert.StartsWith("Enemy base shattered: ", lines[2]);
                    }
                    output.WriteLine($"P746 native deadline: {mission.CountdownValues("kills")}");
                }
                else if (!string.IsNullOrWhiteSpace(reason))
                    Assert.DoesNotContain("The clock has stopped", reason, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}
