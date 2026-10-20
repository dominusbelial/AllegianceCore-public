using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Perf")]
public sealed class P705TechModeLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Field<T>(object bot, string name) => (T)bot.GetType().GetField(name, Fields)!.GetValue(bot)!;

    [SkippableTheory]
    [InlineData(25, 3, 10)]
    [InlineData(1, 2, 2)]
    [InlineData(4, 35, 3)]
    [InlineData(101, 1, 5)]
    // P744 — machine/timing-sensitive live test: the Wait() wall-clock deadlines
    // are scaled by the measured machine speed factor so a slower Docker runner
    // no longer hits them; a sim-progression regression still does (see Wait()).
    [Trait("Category", "Perf")]
    public void Research_upgrades_and_resets_through_two_complete_local_matches(int civA, int civB, int size)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            Port = 0, Showcase = new() { TechMode = "research", IncomeMultiplier = 500, ResearchTimeScale = 16 },
            SideSelectAutoSeatSeconds = 30,   // P763: ConnectionSession peers in this suite never send POSITIONREQ
            LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { IntermissionSeconds = 2 } },
            Games = [new() { Name = "P705 two-match proof", Teams = 2, CivIds = [civA, civB], SideNames = ["Alpha", "Bravo"],
                MaxTeamImbalance = 32767,
                MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 } }],
        });
        var guard = new object();
        using var stop = new CancellationTokenSource();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    lock (guard)
                    {
                        // Run ordinary 20 UPS ticks in batches, accelerating only wall time.
                        // Isolate research/purchase/lifecycle from battle RNG. Native construction
                        // ships still travel/build normally; only combat fixture peers are spaced.
                        for (int tick = 0; tick < 5; tick++)
                        {
                            foreach (var live in server.Ships.Where(l => !l.Ship.IsNpc && l.Ship.StationId is null))
                            { live.Ship.Position = new(10000 + live.Ship.Id % 64 * 150, 5000, 0); live.Ship.Velocity = Vector3.Zero; }
                            server.TickSim();
                        }
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { tickerError = ex; }
        }) { IsBackground = true };
        var timeline = new List<object>();
        var watch = Stopwatch.StartNew();
        void Event(string kind, int match, object? detail = null)
        {
            var row = new { kind, match, wall = watch.Elapsed.TotalSeconds, sim = server.SimNow, detail };
            timeline.Add(row); output.WriteLine(JsonSerializer.Serialize(row));
            var proof = Environment.GetEnvironmentVariable("ALLEGIANCE_P705_PROOF_DIRECTORY");
            if (proof is not null)
            {
                Directory.CreateDirectory(proof);
                File.WriteAllText(Path.Combine(proof, $"{civA}-{civB}.json"), JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Action? sample = null;
        double nextSample = 0;
        void Wait(Func<bool> done, string reason, int seconds = 300)
        {
            // P744 — hardware-relative deadline: the wall-clock budget was tuned
            // on the reference workstation; scale it by the machine speed factor
            // measured in this same run so a slower Docker runner no longer trips
            // it. SimulateRegressionFactor (env P744_SIMULATE_REGRESSION>1) inflates
            // only the effective elapsed time — never the calibration — modelling a
            // machine/regression N× slower than its scaled budget, so the deadline
            // still fires on a real sim-progression slowdown.
            double speed = PerfCalibration.CpuSpeedFactor;
            double regr = PerfCalibration.SimulateRegressionFactor;
            var deadline = watch.Elapsed.TotalSeconds + seconds * speed;
            while (watch.Elapsed.TotalSeconds * regr < deadline)
            {
                Assert.Null(tickerError);
                lock (guard)
                {
                    if (done()) return;
                    if (watch.Elapsed.TotalSeconds >= nextSample)
                    {
                        sample?.Invoke(); nextSample = watch.Elapsed.TotalSeconds + 10;
                    }
                }
                Thread.Sleep(50);
            }
            Assert.Fail(reason + " timed out; sim=" + server.SimNow);
        }
        Assert.All(server.Missions[0].Sides, s => Assert.Equal(new ResearchState(new ResearchTree(server.Missions[0].Core, s.Civilization.CivilizationId)).DevelopmentTechs, s.DevelopmentTechs));
        Assert.All(server.Missions[0].Sides, s => Assert.DoesNotContain(s.Buckets, b => b.Completed && b.ItemType == BucketItemType.BitDevelopment));
        Event("tech-reset", 1, new { epoch = server.Missions[0].ResearchEpoch });
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            // Let research-only peers ready the mission before purchasing combat ships.
            // A fresh rig is likewise started by its probe/commanders before the fleet.
            Wait(() => server.Missions[0].Stage == MissionStage.Started, "initial commander ready/start");
            Event("started", 1);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"size":10,"staging":false,"refill":true,"finale_after_minutes":0}""")!;
            args["size"] = JsonSerializer.SerializeToElement(size);
            args["hull"] = JsonSerializer.SerializeToElement(Showcase.RepresentativeHullOfRole(Catalog.RepresentativeCore(), Showcase.HullRole.Bomber)!.Value.Name);
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            Wait(() => manager.ActiveBots().Count == size * 2, $"{size}v{size} spawn", 90);
            var bots = ((System.Collections.IEnumerable)typeof(BotManager).GetField("_bots", Fields)!.GetValue(manager)!).Cast<object>().ToArray();
            Assert.Equal(size * 2, bots.Length);
            sample = () =>
            {
                var proof = Environment.GetEnvironmentVariable("ALLEGIANCE_P705_PROOF_DIRECTORY");
                if (proof is not null) File.WriteAllText(Path.Combine(proof, $"{civA}-{civB}-sample.json"), JsonSerializer.Serialize(new
                {
                    wall = watch.Elapsed.TotalSeconds, epoch = server.Missions[0].ResearchEpoch, sim = server.SimNow, research = manager.ResearchStatus(),
                    bots = bots.Select(b => new { id = Field<short>(b, "ShipId"), side = Field<short>(b, "Side"), role = Field<string>(b, "Role"),
                        hull = Field<short>(b, "HullId"), pod = Field<bool>(b, "IsPod"), dock = Field<bool>(b, "PodDocked"),
                        warning = Field<string?>(b, "GrantWarning"),
                        actual = server.Ships.Where(l => l.Ship.Id == Field<short>(b, "ShipId")).Select(l => new { hull = l.Ship.HullType.HullId, dock = l.Ship.StationId, parts = l.Ship.ExportInventoryForAck() }).ToArray(),
                        loadout = Field<TechLoadoutSelector.Selection?>(b, "PreviousLegalLoadout") }).ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            };
            for (int match = 1; match <= 2; match++)
            {
                var mission = server.Missions[0];
                var initial = mission.Sides.ToDictionary(s => s.Id, s => new ResearchState(new ResearchTree(mission.Core, s.Civilization.CivilizationId)).Techs);
                Wait(() => mission.Stage == MissionStage.Started, "automatic match start");
                if (match > 1) Event("started", match);
                Wait(() => bots.All(b => server.Ships.Any(l => l.Ship.Id == Field<short>(b, "ShipId")
                    && l.Ship.StationId is null && !l.Ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod))), $"all {size * 2} initial combat launches", 60);
                var completed = new HashSet<(short Side, short Dev)>();
                var upgraded = new HashSet<short>();
                var cycled = new HashSet<short>();
                Wait(() =>
                {
                    foreach (var side in mission.Sides)
                    {
                        var observer = Field<Allegiance.ClientCore.Session.ConnectionSession>(bots.First(b => Field<short>(b, "Side") == side.Id), "Cs");
                        short[] observed;
                        lock (observer.Gate) observed = observer.World.CompletedDevelopments.ToArray();
                        foreach (short development in observed)
                            if (completed.Add((side.Id, development)))
                                Event("research-completed", match, new { side = side.Id, development, mission.Core.Developments[development].Name });
                        if (upgraded.Contains(side.Id)) continue;
                        var startingDock = side.Civilization.InitialStationTypeId;
                        var home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == side.Id && s.Type.HasCapability(DataStationTypeIGC.SabmRestart)).Type.StationTypeId;
                        var choice = TechLoadoutSelector.Select(mission.Core, side.Civilization.CivilizationId, Showcase.HullRole.Bomber,
                            side.Techs, home);
                        if (choice is null || TechLoadoutSelector.Legal(mission.Core, side.Civilization.CivilizationId,
                            initial[side.Id], startingDock, choice.HullId, choice.Parts)) continue;
                        foreach (var bot in bots.Where(b => Field<short>(b, "Side") == side.Id))
                        {
                            short id = Field<short>(bot, "ShipId");
                            var ship = server.Ships.Single(l => l.Ship.Id == id).Ship;
                            var cs = Field<Allegiance.ClientCore.Session.ConnectionSession>(bot, "Cs");
                            if (ship.StationId is null && !ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) && cycled.Add(id))
                                ship.ReceiveDamage(0, 1_000_000, server.SimNow, ship.Position, ship.Position, null);
                            if (ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) && ship.StationId is null
                                && P660NextMatchLiveTests.Read(cs, () => cs.World.Ships.TryGetValue(id, out var own) && Catalog.IsLifepodHull(own.HullId)))
                            {
                                var station = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == side.Id);
                                typeof(Srv).GetMethod("PerformStationRescue", Fields)!.Invoke(server, [ship, station]);
                            }
                            var loadout = Field<TechLoadoutSelector.Selection?>(bot, "PreviousLegalLoadout");
                            if (loadout is null || ship.StationId is not null || ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;
                            if (TechLoadoutSelector.Legal(mission.Core, side.Civilization.CivilizationId, initial[side.Id], startingDock, loadout.HullId, loadout.Parts)) continue;
                            Assert.Contains(cs.BuyLoadoutAcks, ack => ack.FLaunch && ack.FBoughtEverything && ack.HullId == ship.HullType.HullId);
                            Assert.Equal(loadout.HullId, ship.HullType.HullId);
                            Assert.Equal(loadout.Parts.OrderBy(p => p.PartId).Select(p => p.PartId), ship.ExportInventoryForAck().OrderBy(p => p.PartId).Select(p => p.PartId));
                            upgraded.Add(side.Id);
                            Event("first-upgraded-launch", match, new { side = side.Id, ship = id, hull = loadout.HullId, parts = loadout.Parts.Select(p => p.PartId).ToArray(), legalAtStart = false });
                            break;
                        }
                    }
                    return upgraded.Count == 2 && completed.Select(c => c.Side).Distinct().Count() == 2;
                }, "both sides real development and newly legal upgraded launch");
                lock (guard)
                {
                    Assert.All(manager.ResearchStatus(), r => Assert.NotEmpty(r.CompletedTechs));
                    // Exercise the normal station-destruction / elimination / GAME_OVER chain.
                    // Lethal fixture damage bounds the proof duration; no direct DeclareGameOver.
                    Conquer(server);
                    Assert.NotSame(mission, server.Missions[0]);
                    Assert.All(server.Missions[0].Sides, s => Assert.Equal(new ResearchState(new ResearchTree(mission.Core, s.Civilization.CivilizationId)).DevelopmentTechs, s.DevelopmentTechs));
                    Assert.All(server.Missions[0].Sides, s => Assert.DoesNotContain(s.Buckets, b => b.Completed && b.ItemType == BucketItemType.BitDevelopment));
                    Event("game-over", match);
                    Event("tech-reset", match + 1, new { epoch = server.Missions[0].ResearchEpoch });
                }
                Wait(() => P660NextMatchLiveTests.Read(manager.Probe, () => manager.Probe.World.GameOverResult is not null), "wire GAME_OVER", 30);
                if (match == 1)
                    Wait(() => bots.All(b => server.Ships.Any(l => l.Ship.Id == Field<short>(b, "ShipId") && ReferenceEquals(l.Ship.Mission, server.Missions[0])))
                        && P660NextMatchLiveTests.Read(manager.Probe, () => manager.Probe.World.ResearchBriefing?.Epoch == server.Missions[0].ResearchEpoch), "reseat and research epoch reset", 60);
            }
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally
        {
            stop.Cancel(); Assert.True(ticker.Join(5000));
            var path = Environment.GetEnvironmentVariable("ALLEGIANCE_P705_PROOF_DIRECTORY");
            if (path is not null) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, $"{civA}-{civB}.json"), JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true })); }
        }
    }

    private static void Conquer(Srv server)
    {
        var mission = server.Missions[0];
        var attacker = server.Ships.First(l => l.Ship.SideId == 0).Ship;
        foreach (var sector in mission.Sectors)
        foreach (var station in sector.StationRuntimes.Where(s => s.SideId == 1).ToArray())
        {
            if (!ReferenceEquals(server.Missions[0], mission)) return;
            Assert.Equal(DamageResult.Killed, station.ReceiveDamage(0, 1_000_000, server.SimNow, station.Center, station.Center, attacker));
            sector.RemoveStation(station);
            mission.Site.KillStationEvent(station, attacker, 1_000_000);
            if (ReferenceEquals(server.Missions[0], mission)) mission.Site.SideLostRestartStation(mission, 1);
        }
        Assert.NotSame(mission, server.Missions[0]);
    }
}
