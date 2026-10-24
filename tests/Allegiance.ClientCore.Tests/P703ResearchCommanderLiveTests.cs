using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P703ResearchCommanderLiveTests(ITestOutputHelper output)
{
    private static string PlanDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "showcase-plans"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", "showcase-plans");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remote_constructor_death_reaches_team_state_without_remote_combat_effects(bool quiet)
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(new ServerConfig
        {
            ArtPath = Catalog.ArtRoot, StaticCore = "PCore001.igc", Port = 0,
            // P763 — these ConnectionSession peers never send POSITIONREQ; the
            // opt-in 30 s auto-seat preserves the harness's seat-and-join flow.
            SideSelectAutoSeatSeconds = 30,
            LiveSettings = new() { TestUnlock = new() { Enabled = false },
                TestHarness = new() { Enabled = false, IgnoreTechGate = false } },
            Games = [new() { Name = "P703 remote death", Teams = 2, CivIds = [1, 2], SideNames = ["A", "B"],
                ResearchPlanDirectory = PlanDirectory(), MapGen = new() { Type = "SingleRing" } }],
        });
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        var side = mission.Sides[0];
        var home = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == side.Id));
        var remote = mission.Sectors.First(s => s != home && s.StationRuntimes.Count == 0);
        var type = side.Buckets.First(b => b.ItemType == BucketItemType.BitStationType && side.CanBuy(b)).StationType!.Value;
        // Fixture unit placement isolates lifecycle visibility; the separate match proof buys
        // every constructor through real wallets and buckets.
        var builder = server.SpawnNpcShip(mission, remote, side.Id, new(7000, 7000, 7000), type.ConstructionDroneTypeId);
        builder.BuilderStationType = type; builder.WaitsForBuildOrder = true;
        using var observer = P660NextMatchLiveTests.Connect(server, "P703-lifecycle", 70301);
        Assert.Equal(side.Id, observer.World.MySideId);
        Assert.Same(home, server.Ships.Single(s => s.Ship.Id == observer.World.MyShipId).Ship.Sector);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(observer,
            () => observer.World.ResearchProduction.ContainsKey(builder.Id) && observer.World.Ships.ContainsKey(builder.Id)));
        Assert.True(P660NextMatchLiveTests.Read(observer, () => observer.World.IsResearchProductionAlive(builder.Id)));
        if (quiet)
        {
            // Native quiet constructor/layer consumption: parts removed, then the normal
            // zero-damage KillShipEvent (buildingeffectigc.h:328-343; FedSrv.CPP:13842-13860).
            builder.ClearLoadout(); builder.Ammo = 0; builder.SetFuel(0);
            mission.Site.KillShipEvent(mission.LastUpdate, builder, null, 0, builder.Position, default);
        }
        else
            Assert.Equal(DamageResult.Killed, builder.ReceiveDamage(0, 1_000_000, mission.LastUpdate,
                builder.Position, builder.Position, null));
        Assert.True(builder.Dead);
        Assert.DoesNotContain(builder, remote.Models);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(observer,
            () => !observer.World.IsResearchProductionAlive(builder.Id)));
        lock (observer.Gate)
        {
            Assert.True(observer.World.Ships[builder.Id].IsDead);
            // Native KILL_SHIP is mission-wide for nonzero damage, including
            // off-sector constructors (FedSrv.CPP:11791-11798,11914-11915).
            // It supplies the feed/death state; explosion effects stay local.
            if (quiet)
                Assert.DoesNotContain(observer.World.Events.Kills, k => k.ShipId == builder.Id);
            else
            {
                var kill = Assert.Single(observer.World.Events.Kills, k => k.ShipId == builder.Id);
                Assert.True(kill.BDeathCredit);
                Assert.False(kill.BKillCredit);
                Assert.Equal((short)-1, kill.IdCredit);
            }
            Assert.Empty(observer.World.Explosions);
            Assert.False(Assert.Single(BotManager.ResearchSnapshot(observer, side.Id).Units, u => u.Id == builder.Id).Alive);
        }
    }

    [SkippableTheory]
    [InlineData(25, 3, false)]
    [InlineData(1, 2, true)]
    public void Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(int civA, int civB, bool generated)
    {
        Require.Artwork();
        var config = new ServerConfig
        {
            ArtPath = Catalog.ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Showcase = new() { TechMode = "research", IncomeMultiplier = 500, ResearchTimeScale = 16 },
            LiveSettings = new()
            {
                TestUnlock = new() { Enabled = false },
                TestHarness = new() { Enabled = false, IgnoreTechGate = false },
            },
            Games = [new() { Name = "P703 real economy", Teams = 2, CivIds = [civA, civB], SideNames = ["Alpha", "Bravo"],
                MaxTeamImbalance = 32767, ResearchPlanDirectory = PlanDirectory(),
                ResearchPlans = new() { [0] = generated ? "generated" : "plan_a", [1] = generated ? "generated" : "plan_b" },
                MapGen = new() { Type = "SingleRing", PlayerSectorSpecialAsteroids = 8, PlayerSectorAsteroids = 20 } }],
        };
        using var server = new Srv(); server.Boot(config);
        var mission = server.Missions[0];
        var firstBranch = mission.ResearchPlans.ToDictionary(kv => kv.Key, kv => kv.Value.Steps.First(s =>
            s.Action == "station" && new[] { "Supremacy", "Tactical", "Expansion", "Shipyard" }.Contains(s.Name)));
        // A resource-complete test map, derived from each actual plan. The current map generator
        // emits generic special rocks (0x10), not every core-specific build capability. This is
        // fixture map content, not a commander cheat or a grant of tech/money/constructors.
        short rockId = 10000;
        foreach (var side in mission.Sides)
        {
            var sector = mission.Sectors.Single(s => s.StationRuntimes.Any(st => st.SideId == side.Id));
            var home = sector.StationRuntimes.First(st => st.SideId == side.Id).Data.Position;
            int slot = 0;
            foreach (short capability in mission.ResearchPlans[side.Id].Steps.Where(s => s.Action == "station")
                .Select(s => s.AsteroidClass!.Value).Distinct())
                for (int copy = 0; copy < 3; copy++)
                {
                    double angle = slot++ * 2.399963;
                    float x = home[0] + 3000 * (float)Math.Cos(angle), y = home[1] + 3000 * (float)Math.Sin(angle);
                    var astDef = Allegiance.Core.NativeAsteroidTypes.GetTypeDefaults(Allegiance.Core.NativeAsteroidTypes.ResolveTypeIndex(capability));
                    mission.PlaceAsteroid(new Asteroid(new(1, [x, y, home[2] + copy * 800], [0,1,0], [1,0,0], [0,0,0,1],
                        0, 0, capability, rockId++, 1000, 60, astDef.ModelName, astDef.TextureName, astDef.IconName, sector.Id, "Research map resource", 255)), sector);
                }
            Assert.Contains(sector.Asteroids, a => (a.AabmCapabilities & firstBranch[side.Id].AsteroidClass!.Value) != 0);
        }
        var guard = new object();
        using var stop = new CancellationTokenSource();
        Exception? tickerError = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { lock (guard) server.TickSim(); Thread.Sleep(1); } }
            catch (Exception ex) { tickerError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var proofDirectory = Environment.GetEnvironmentVariable("ALLEGIANCE_P703_PROOF_DIRECTORY");
            if (proofDirectory is not null) Directory.CreateDirectory(proofDirectory);
            var watch = Stopwatch.StartNew();
            double nextSample = 0;
            var finished = new HashSet<short>();
            var paidWallet = new HashSet<short>();
            var techCompleted = new HashSet<short>();
            // Native civ build-time attributes differ substantially; leave room for the
            // slowest first branch plus constructor travel under shared-machine load.
            // Station-first plans can finish their first branch before their
            // first development. Wait for every asserted piece of evidence.
            while (watch.Elapsed.TotalSeconds < 300
                && (finished.Count < 2 || techCompleted.Count < 2 || paidWallet.Count < 2))
            {
                lock (guard)
                {
                    var reports = manager.ResearchStatus();
                    foreach (var side in mission.Sides)
                    {
                        if (mission.Sectors.SelectMany(s => s.StationRuntimes).Any(s => s.SideId == side.Id && s.Type.StationTypeId == firstBranch[side.Id].Id)
                            && reports.Any(r => r.Side == side.Id && r.Stations.Contains(firstBranch[side.Id].Name))) finished.Add(side.Id);
                        if (side.Buckets.Any(b => b.Completed && b.ItemType == BucketItemType.BitDevelopment)) techCompleted.Add(side.Id);
                    }
                    foreach (var ship in server.Ships.Where(s => s.Ship.DroneTypeId < 0 && s.Wallet.Money > 0 && s.Ship.SideId.HasValue))
                        paidWallet.Add(ship.Ship.SideId!.Value);
                }
                if (proofDirectory is not null && watch.Elapsed.TotalSeconds >= nextSample)
                {
                    lock (guard)
                        File.AppendAllText(Path.Combine(proofDirectory, $"{civA}-{civB}.jsonl"), JsonSerializer.Serialize(new
                        {
                            wall = watch.Elapsed.TotalSeconds, sim = mission.LastUpdate, stage = mission.Stage.ToString(),
                            research = manager.ResearchStatus(),
                            stations = mission.Sectors.SelectMany(s => s.StationRuntimes).Select(s => new { s.SideId, s.Type.Name }).ToArray(),
                            builders = mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().Where(s => s.PilotType == Ship.PilotTypeBuilder)
                                .Select(s => new { s.Id, s.SideId, s.Position, s.IsBuilding, s.WaitsForBuildOrder,
                                    target = s.BuilderAsteroidTarget?.Data.AsteroidId, type = s.BuilderStationType?.Name }).ToArray(),
                        }) + Environment.NewLine);
                    nextSample = watch.Elapsed.TotalSeconds + 2;
                }
                Assert.Null(tickerError);
                Thread.Sleep(100);
            }
            output.WriteLine(JsonSerializer.Serialize(manager.ResearchStatus()));
            lock (guard)
            {
                output.WriteLine($"civs={civA}/{civB} generated={generated} wall={watch.Elapsed.TotalSeconds:F1}s sim={mission.LastUpdate:F1}s completed={string.Join(',', finished)}");
                foreach (var side in mission.Sides)
                    output.WriteLine($"side {side.Id}: {string.Join(',', mission.Sectors.SelectMany(s => s.StationRuntimes).Where(s => s.SideId == side.Id).Select(s => s.Type.Name))}");
                Assert.Equal(2, finished.Count);
                Assert.Equal(2, techCompleted.Count);
                Assert.Equal(2, paidWallet.Count);
                Assert.All(mission.ResearchPlans.Values, p => Assert.Equal(generated ? "generated" : p.CivId == civA ? "plan_a" : "plan_b", p.Name));
                Assert.Equal(0, server.SimPassStageFailures);
            }
            var perf = McpTools.Handle("get_perf_stats", new Dictionary<string, JsonElement>(), manager);
            Assert.False(perf.IsError ?? false);
            var json = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)perf.Content[0]).Text);
            Assert.Equal(2, json.RootElement.GetProperty("research").GetArrayLength());
            Assert.All(manager.ResearchStatus(), r => Assert.NotEmpty(r.CompletedTechs));
            Assert.False(config.LiveSettings.TestUnlock.Enabled);
            Assert.False(config.LiveSettings.TestHarness.IgnoreTechGate);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}
