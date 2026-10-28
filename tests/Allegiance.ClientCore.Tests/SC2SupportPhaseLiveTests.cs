using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

// P791 — support (nanite) pilots must follow their side's phase (opening: the
// fighter-fleet rendezvous sector; strike/finale: the strike sector) instead of
// idling at home in a mutual-repair ring. Diagnostic + light-gate live test:
// the same run logs per-pilot observed-vs-server hull fractions (the stale/
// quantized-target evidence) and asserts the phase-following behaviour.
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC2SupportPhaseLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, Fields)!.GetValue(obj)!;
    private static T? TryGet<T>(object obj, string field)
    {
        var f = obj.GetType().GetField(field, Fields);
        return f is null ? default : (T?)f.GetValue(obj);
    }

    [SkippableFact]
    public void Support_pilots_follow_the_side_phase_instead_of_parking_at_home()
    {
        Require.Artwork();
        string? savedShare = Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SUPPORT_SHARE");
        Environment.SetEnvironmentVariable("ALLEGIANCE_BOT_SUPPORT_SHARE", "0.25"); // ~4 support pilots per 15-ship side
        try
        {
            using var server = new Srv();
            var config = P733ResearchTests.Config(16);
            config.Transport = new() { MaxConnectionsPerIp = 64 };
            config.LiveSettings.AutoNextMatch.Enabled = true;
            server.Boot(config);
            using var stop = new CancellationTokenSource();
            var simActions = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var ticker = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    while (simActions.TryDequeue(out var action)) action();
                    server.TickSim(); Thread.Sleep(20);
                }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","size_scale":1,"staging":true,"finale_after_minutes":2,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":4}""")!;
                // A no-station sector is the contested opening sector (the 90v90
                // SC2 harness picks the same way).
                args["target_sector"] = JsonSerializer.SerializeToElement(server.Missions[0].Sectors.First(s => s.Stations.Count == 0).Name);
                var result = McpTools.Handle("spawn_scenario", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var fleet = manager.ActiveBots();
                Assert.Equal(30, fleet.Count);
                foreach (short side in new short[] { 0, 1 })
                    Assert.Equal(4, fleet.Count(b => b.Side == side && b.Role.Equals("support", StringComparison.OrdinalIgnoreCase)));
                var mission = server.Missions[0];
                // Own-side station ids per side: repairing one while it is damaged
                // at home is the sanctioned exception to phase-following (P791).
                var stationIds = new Dictionary<short, HashSet<short>>
                {
                    [0] = mission.Sectors.SelectMany(s => s.StationRuntimes).Where(rt => rt.SideId == 0).Select(rt => rt.StationId).ToHashSet(),
                    [1] = mission.Sectors.SelectMany(s => s.StationRuntimes).Where(rt => rt.SideId == 1).Select(rt => rt.StationId).ToHashSet(),
                };

                var watch = Stopwatch.StartNew();
                const double warmupSeconds = 150, observeSeconds = 750;
                int sweep = 0;
                var supportInBattle = new Dictionary<short, (int InBattle, int Total)> { [0] = new(), [1] = new() };
                var supportPairs = new Dictionary<short, (int MutualHome, int Total)> { [0] = new(), [1] = new() };
                var holdSince = new Dictionary<string, (short Target, double At)>();
                double longestHold = 0;
                var samples = new List<object>();
                var staleness = new List<object>();
                bool gameOverSeen = false, nextMatchSeen = false;
                short openingSector0 = -1, openingSector1 = -1;
                while (watch.Elapsed.TotalSeconds < warmupSeconds + observeSeconds)
                {
                    Thread.Sleep(5000);
                    double now = watch.Elapsed.TotalSeconds;
                    if (mission.Stage == MissionStage.Over) gameOverSeen = true;
                    if (gameOverSeen && mission.Stage == MissionStage.Started) nextMatchSeen = true;
                    var bots = manager.ActiveBots().Where(b => b.Role.Equals("support", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (bots.Length == 0) continue;
                    sweep++;
                    // The phase signal the production driver reads (gathered per side).
                    short[] anchors = new short[2];
                    string[] kinds = new string[2];
                    for (short side = 0; side < 2; side++)
                    {
                        var phase = Phase(manager, side);
                        anchors[side] = phase.anchor;
                        kinds[side] = phase.kind;
                        if (phase.kind == "opening")
                        {
                            if (side == 0 && openingSector0 < 0) openingSector0 = phase.anchor;
                            if (side == 1 && openingSector1 < 0) openingSector1 = phase.anchor;
                        }
                    }
                    var holders = InternalSupportBots(manager);
                    // The gate scores THIS match only: after game over the bots
                    // reseat into the next match and legitimately sit at home
                    // until it starts (the full-match/next-match behaviour is
                    // still logged below, per the standing unattended-run rule).
                    if (gameOverSeen) continue;
                    foreach (var bot in bots)
                    {
                        if (now <= warmupSeconds) continue;
                        // Score only live, launched pilots: pods, docked refits and
                        // dead time are not "parked at home" (pilots following the
                        // phase into a base raid die and return — that is the
                        // mission, not the bug). The base run's idle-at-home pilots
                        // are always eligible, so the comparison stays honest.
                        var holder = holders.Cast<object>().FirstOrDefault(h => Get<string>(h, "Name") == bot.Name);
                        bool eligible = holder is not null && !TryGet<bool>(holder, "IsPod")
                            && !TryGet<bool>(holder, "PodDocked") && !TryGet<bool>(holder, "SupportRefitting");
                        if (!eligible) continue;
                        bool inBattle = bot.Sector == anchors[bot.Side];
                        // Following the phase = in the anchor sector, or at home
                        // repairing a damaged own-side station (the sanctioned
                        // exception: "at home only for a damaged station"), or at
                        // home repairing a genuinely damaged own ship (< 0.85 — the
                        // bug was the 0.95+ quantization ring, not real damage).
                        short? held = holder is null ? null : TryGet<short?>(holder, "SupportHoldTarget");
                        bool stationRepair = held is { } id && stationIds[bot.Side].Contains(id);
                        float? heldFraction = held is { } hf ? HeldFraction(holder!, hf) : null;
                        bool genuineHomeRepair = !inBattle && heldFraction is { } frac && frac is > 0f and < .85f;
                        var (ib, tot) = supportInBattle[bot.Side];
                        supportInBattle[bot.Side] = (ib + (inBattle || stationRepair || genuineHomeRepair ? 1 : 0), tot + 1);
                        // Mutual home repair: a support pilot holding another support pilot outside the battle sector.
                        if (holder is not null && held is { }
                            && holders.Cast<object>().Any(h => Get<short>(h, "ShipId") == held))
                        {
                            var (mh, t2) = supportPairs[bot.Side];
                            supportPairs[bot.Side] = (mh + (inBattle ? 0 : 1), t2 + 1);
                        }
                    }
                    // Hold-duration leash: no pilot repairs one target for > 60 s.
                    // Hold-duration leash, measured on the production hold state:
                    // now - SupportHoldSince while a target is held. Measuring the
                    // raw target id instead would merge back-to-back legitimate
                    // holds on a target oscillating around the 0.85 bar (each one
                    // completed) into one fictitious 95 s hold (P791 run 8).
                    foreach (var holder in holders)
                    {
                        string name = Get<string>(holder, "Name");
                        // NT-2: a commander-driven repair (research commander; native
                        // shipIGC.cpp:1936-1939 — the commander owns the target and
                        // route) has no autonomous leash in production, so it must not
                        // feed the hold-duration metric; the fraction gate above still
                        // scores it via SupportHoldTarget. Only autonomous holds are
                        // leashed to 60 s.
                        var ordered = Get<object?>(holder, "OrderedCommand");
                        short orderedCommand = ordered is null ? (short)-1
                            : (short)Convert.ChangeType(ordered.GetType().GetProperty("CommandId")!.GetValue(ordered)!, typeof(short));
                        if (orderedCommand == OrderChange.CidRepair)
                        { holdSince.Remove(name); continue; }
                        if (TryGet<short?>(holder, "SupportHoldTarget") is not { } held)
                        { holdSince.Remove(name); continue; }
                        double since = TryGet<double>(holder, "SupportHoldSince");
                        holdSince[name] = (held, since);
                        if (now > warmupSeconds) longestHold = Math.Max(longestHold, now - since);
                    }
                    // Diagnostic evidence: observed fraction/pose age vs the server's real hull fraction.
                    if (sweep % 4 == 0)
                        foreach (var holder in holders)
                        {
                            short? held = TryGet<short?>(holder, "SupportHoldTarget") ?? TryGet<short?>(holder, "TargetId");
                            if (held is null) continue;
                            var cs = Get<ConnectionSession?>(holder, "Cs");
                            float observed = -1; double age = -1;
                            if (cs?.World is { } world)
                            {
                                lock (cs.Gate)
                                {
                                    if (world.Ships.TryGetValue(held.Value, out var se) && se.Latest is { } pose) observed = pose.HullFraction;
                                    age = world.PoseAgeSeconds(held.Value);
                                }
                            }
                            var ship = server.Ships.FirstOrDefault(s => s.Ship.Id == held.Value)?.Ship;
                            var row = new
                            {
                                t = Math.Round(now, 1), side = Get<short>(holder, "Side"), ship = Get<short>(holder, "ShipId"),
                                sector = HoldSector(holder), held, observed, age = double.IsFinite(age) ? Math.Round(age, 2) : -1,
                                server = ship is null ? -1f : ship.Fraction,
                                anchor = anchors[Get<short>(holder, "Side")], kind = kinds[Get<short>(holder, "Side")],
                            };
                            samples.Add(row);
                            output.WriteLine("[p791-sample] " + JsonSerializer.Serialize(row));
                            if (ship is not null && observed >= 0 && Math.Abs(observed - ship.Fraction) > 0.02f && ship.Fraction >= 0.999f)
                                staleness.Add(row);
                        }
                    // P791 — per-sweep ground truth for the gate metric: every
                    // participating pilot's sector vs the anchor, hold state included.
                    foreach (var bot in bots)
                    {
                        if (now <= warmupSeconds) continue;
                        var holder = holders.Cast<object>().FirstOrDefault(h => Get<string>(h, "Name") == bot.Name);
                        output.WriteLine("[p791-sweep] " + JsonSerializer.Serialize(new
                        {
                            t = Math.Round(now, 1), side = bot.Side, ship = bot.ShipId, sector = bot.Sector,
                            anchor = anchors[bot.Side], kind = kinds[bot.Side],
                            held = holder is null ? null : TryGet<short?>(holder, "SupportHoldTarget"),
                            pod = holder is null ? (bool?)null : TryGet<bool>(holder, "IsPod"),
                            docked = holder is null ? (bool?)null : TryGet<bool>(holder, "PodDocked"),
                            refit = holder is null ? (bool?)null : TryGet<bool>(holder, "SupportRefitting"),
                        }));
                    }
                }
                longestHold = Math.Max(longestHold, 0);
                foreach (short side in new short[] { 0, 1 })
                {
                    var (ib, tot) = supportInBattle[side];
                    var (mh, mt) = supportPairs[side];
                    double fraction = tot == 0 ? 0 : (double)ib / tot;
                    output.WriteLine($"[p791-gate] side={side} inBattleOrStation={ib}/{tot} ({fraction:P1}) mutualHomeRepair={mh}/{mt} longestHold={longestHold:F1}s gameOver={gameOverSeen} nextMatch={nextMatchSeen}");
                    Assert.True(tot > 20, "not enough support-pilot samples collected");
                    Assert.True(fraction >= 0.60, $"P791: only {fraction:P1} of support-pilot samples on side {side} followed the phase (in the battle sector or repairing a damaged own station); the unfixed base run measured 0.0%");
                    Assert.Equal(0, mh);
                    Assert.True(longestHold <= 60, $"P791: a support pilot held one repair target for {longestHold:F1}s");
                }
                output.WriteLine($"[p791-stale] {staleness.Count} samples where the observed fraction lagged a server-side full hull: {JsonSerializer.Serialize(staleness.Take(20))}");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_BOT_SUPPORT_SHARE", savedShare); }
    }

    private static (string kind, short anchor) Phase(BotManager manager, short side)
    {
        var m = typeof(BotManager).GetMethod("SupportPhaseForSide", Fields | BindingFlags.Instance)!;
        var signal = (BotManager.SupportPhaseSignal)m.Invoke(manager, [side])!;
        return (signal.Kind.ToString(), signal.AnchorSector ?? -1);
    }

    private static System.Collections.IEnumerable InternalSupportBots(BotManager manager)
    {
        var bots = (System.Collections.IList)Get<object>(manager, "_bots")!;
        foreach (var bot in bots)
            if (string.Equals(Get<string>(bot, "Role"), "support", StringComparison.OrdinalIgnoreCase))
                yield return bot;
    }

    private static float? HeldFraction(object holder, short held)
    {
        var cs = Get<ConnectionSession?>(holder, "Cs");
        if (cs?.World is not { } world) return null;
        lock (cs.Gate)
        {
            if (world.Ships.TryGetValue(held, out var ship) && ship.Latest is { } pose) return pose.HullFraction;
            if (world.StationLive.TryGetValue(held, out var live)) return live.HullFraction;
        }
        return null;
    }

    private static short HoldSector(object holder)
    {
        var cs = Get<ConnectionSession?>(holder, "Cs");
        if (cs?.World is not { } world) return -1;
        lock (cs.Gate) return world.Ships.TryGetValue(Get<short>(holder, "ShipId"), out var me) ? me.SectorId : (short)-1;
    }
}
