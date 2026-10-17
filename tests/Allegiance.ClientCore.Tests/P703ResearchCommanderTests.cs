using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P703ResearchCommanderTests
{
    private sealed class Fixture
    {
        internal readonly ResearchTree Tree = new(Catalog.RepresentativeCore(), 1);
        internal readonly ResearchPlan Plan;
        internal readonly Dictionary<short, short> Reservations = new();
        internal readonly ResearchCommander Commander;
        internal ResearchCommander.Snapshot World;
        internal Fixture()
        {
            var state = new ResearchState(Tree);
            var dev = Tree.Nodes.First(n => n.Kind == "develop" && state.CanApply(n));
            var develop = new ResearchStep { Action = "develop", Id = dev.Id, Name = dev.Name };
            state.Apply(develop);
            var station = Tree.Nodes.First(n => n.Kind == "station" && n.Id != Tree.Civ.InitialStationTypeId && state.CanApply(n));
            var build = new ResearchStep { Action = "station", Id = station.Id, Name = station.Name,
                AsteroidClass = (short)(station.AsteroidMask & -station.AsteroidMask) };
            state.Apply(build);
            var drone = Tree.Nodes.First(n => n.Kind == "drone" && state.CanApply(n));
            Plan = new() { Core = "PCore001", CoreVersion = Tree.Core.Version, CivId = 1, Name = "test",
                Steps = [develop, build, new() { Action = "drone", Id = drone.Id, Name = drone.Name },
                    new() { Action = "wait", Name = "hold", WaitSeconds = 10, RequiredTechs = state.Techs.ToBytes() }] };
            Commander = new(0, "epoch", Tree, Plan, Tree.Settings.StartingTechs(Tree.Civ), Reservations);
            World = new(1, true, 1000000, Tree.Settings.StartingTechs(Tree.Civ), [Tree.Civ.InitialStationTypeId], [], [],
                [new(10, station.AsteroidMask, true), new(11, station.AsteroidMask, true)], new Dictionary<short, ResearchCommander.Funding>());
        }
        internal IReadOnlyList<ResearchCommander.Command> Tick() => Commander.Tick(World);
        internal void CompleteDevelopment()
        {
            Assert.Contains(Tick(), c => c.Kind == "donate");
            var dev = Tree.Core.Developments[Plan.Steps[0].Id];
            World = World with { Time = 2, CompletedDevelopments = [dev.DevelopmentId],
                Developments = World.Developments.Or(TechTreeBitMask.FromBytes(dev.TtbmEffects)) };
            Tick();
        }
        internal void CompleteStation()
        {
            CompleteDevelopment(); Tick();
            World = World with { Time = 3, Stations = [Tree.Civ.InitialStationTypeId, Plan.Steps[1].Id] };
            Tick();
        }
    }

    [SkippableFact]
    public void Every_step_spends_real_wallet_and_waits_for_observed_completion()
    {
        Require.Artwork(); var f = new Fixture();
        f.World = f.World with { Started = false };
        Assert.Empty(f.Tick());
        f.World = f.World with { Started = true, Wallet = 0 };
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "donate");
        Assert.Equal("waiting for money", f.Commander.Telemetry!.Status);
        f.World = f.World with { Wallet = 7 };
        Assert.Equal(7, Assert.Single(f.Tick(), c => c.Kind == "donate").Amount);
        f.World = f.World with { Wallet = 1000000, Time = 8 };
        f = new Fixture(); f.CompleteStation();
        Assert.Empty(f.Reservations);
        var drone = f.Plan.Steps[2].Id;
        var cap = Enumerable.Range(100, 4).Select(id => new ResearchCommander.Unit((short)id, drone, -1, true)).ToArray();
        f.World = f.World with { Units = cap, Time = 10 };
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "donate");
        Assert.Contains("cap", f.Commander.Telemetry!.Status);
        f.World = f.World with { Units = cap.Take(3).ToArray(), Time = 11 };
        Assert.Contains(f.Tick(), c => c.Kind == "donate");
        f.World = f.World with { Units = [.. cap.Take(3), new(200, drone, -1, true)], Time = 12 };
        f.Tick();
        f.World = f.World with { Time = 13 }; f.Tick();
        Assert.Equal("wait", f.Commander.Telemetry!.Action);
        f.World = f.World with { Time = 22 }; f.Tick();
        Assert.True(f.Commander.Telemetry!.ProgressPercent < 100);
        f.World = f.World with { Time = 23 }; f.Tick(); f.Tick();
        Assert.Equal("complete; idle", f.Commander.Telemetry!.Status);
        Assert.Equal(100, f.Commander.Telemetry.ProgressPercent);
        Assert.Empty(f.Tick());
    }

    [SkippableFact]
    public void Constructor_death_rock_loss_and_lost_prerequisites_recover_without_tech_grants()
    {
        Require.Artwork(); var f = new Fixture(); f.CompleteDevelopment();
        Assert.Contains(f.Tick(), c => c.Kind == "donate");
        Assert.Equal((short)0, f.Reservations[10]);
        var station = f.Plan.Steps[1].Id;
        var drone = f.Tree.Core.StationTypes[station].ConstructionDroneTypeId;
        f.World = f.World with { Time = 4, Units = [new(100, drone, station, true)] };
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);
        f.World = f.World with { Time = 10, Rocks = [new(10, f.Plan.Steps[1].AsteroidClass!.Value, false, true), f.World.Rocks[1]] };
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "build"); // Do not interrupt our active build.
        f.World = f.World with { Time = 16, Rocks = [f.World.Rocks[1]] };
        Assert.Equal((short)11, Assert.Single(f.Tick(), c => c.Kind == "build").Target);
        Assert.False(f.Reservations.ContainsKey(10));
        f.World = f.World with { Time = 22, Units = [new(100, drone, station, false)] };
        Assert.Contains(f.Tick(), c => c.Kind == "donate");
        f.World = f.World with { Time = 28, Units = [new(100, drone, station, false), new(101, drone, station, true)] };
        Assert.Equal((short)101, Assert.Single(f.Tick(), c => c.Kind == "build").Id);
        f.World = f.World with { Time = 35, Stations = [f.Tree.Civ.InitialStationTypeId, station] }; f.Tick();
        Assert.Empty(f.Reservations);
        f.World = f.World with { Time = 40, Stations = [f.Tree.Civ.InitialStationTypeId], Units = [] };
        Assert.Contains(f.Tick(), c => c.Kind == "donate");
        Assert.Contains("rebuilding lost prerequisite", f.Commander.Telemetry!.Status);
        Assert.Equal(station, f.Plan.Steps[1].Id);
    }

    [SkippableFact]
    public void A_dead_paused_constructor_cannot_reserve_the_only_rock_needed_for_another_repair()
    {
        Require.Artwork(); var f = new Fixture(); f.CompleteStation();
        var home = f.Tree.Civ.InitialStationTypeId;
        var station = f.Plan.Steps[1].Id;
        var excluded = new HashSet<short>();
        foreach (short start in new[] { home, station })
            for (short id = start; id >= 0 && excluded.Add(id); id = f.Tree.Core.StationTypes[id].SuccessorStationTypeId) { }
        short support = f.Tree.Core.StationTypes.Keys.First(id => !excluded.Contains(id));
        var sharedRock = f.World.Rocks[0] with { Capabilities = (short)(f.World.Rocks[0].Capabilities | f.Tree.Core.StationTypes[home].AabmBuild) };
        f.World = f.World with { Time = 10, Stations = [home, support], Units = [], Rocks = [sharedRock] };
        f.Tick();
        var type = f.Tree.Core.StationTypes[station];
        f.World = f.World with { Time = 16, Units = [new(100, type.ConstructionDroneTypeId, station, true)] };
        Assert.Contains(f.Tick(), c => c.Kind == "build");
        Assert.Equal((short)0, f.Reservations[10]);
        f.World = f.World with { Time = 22, Stations = [support], Units = [new(100, type.ConstructionDroneTypeId, station, false)] };
        Assert.Contains(f.Tick(), c => c.Kind == "donate");
        Assert.Equal(f.Tree.Core.StationTypes[home].Name, f.Commander.Telemetry!.Name);
        Assert.Equal((short)10, f.Commander.Telemetry.ReservedRock);
    }

    [SkippableFact]
    public void Constructor_uses_native_goto_before_building_in_another_sector()
    {
        Require.Artwork(); var f = new Fixture(); f.CompleteDevelopment(); f.Tick();
        var type = f.Tree.Core.StationTypes[f.Plan.Steps[1].Id];
        f.World = f.World with { Time = 4, Units = [new(100, type.ConstructionDroneTypeId, type.StationTypeId, true, 1)],
            Rocks = [new(10, type.AabmBuild, true, Sector: 9)] };
        Assert.Equal("goto", Assert.Single(f.Tick(), c => c.Id == 100).Kind);
        f.World = f.World with { Time = 10, Units = [new(100, type.ConstructionDroneTypeId, type.StationTypeId, true, 9)] };
        Assert.Equal("build", Assert.Single(f.Tick(), c => c.Id == 100).Kind);
    }

    [SkippableFact]
    public void Reservations_are_shared_across_three_sides_and_released_when_no_rock_survives()
    {
        Require.Artwork(); var f = new Fixture(); f.CompleteDevelopment();
        var peers = new[] { f.Commander, new ResearchCommander(1, "epoch", f.Tree, f.Plan,
            f.Tree.Settings.StartingTechs(f.Tree.Civ), f.Reservations), new ResearchCommander(2, "epoch", f.Tree, f.Plan,
            f.Tree.Settings.StartingTechs(f.Tree.Civ), f.Reservations) };
        foreach (var p in peers) { p.Tick(f.World); p.Tick(f.World); }
        Assert.Equal(2, f.Reservations.Count);
        Assert.Contains("available compatible asteroid", peers[2].Telemetry!.Status);
        var empty = f.World with { Time = 50, Rocks = [] };
        foreach (var p in peers) Assert.DoesNotContain(p.Tick(empty), c => c.Kind == "donate");
        Assert.Empty(f.Reservations);
    }

    [SkippableFact]
    public void Donations_wait_for_ack_and_completion_is_not_inferred_from_elapsed_time()
    {
        Require.Artwork(); var f = new Fixture();
        var donation = Assert.Single(f.Tick(), c => c.Kind == "donate");
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "donate");
        f.World = f.World with { Time = 500, Wallet = f.World.Wallet - donation.Amount,
            FundingByBucket = new Dictionary<short, ResearchCommander.Funding> { [donation.Id] = new(donation.Amount, 0) } };
        f.Tick();
        f.World = f.World with { Time = 1500 }; f.Tick();
        Assert.Equal("develop", f.Commander.Telemetry!.Action);
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "donate");
        f.World.CompletedDevelopments.Add(f.Plan.Steps[0].Id); f.Tick();
        Assert.Contains(f.Tick(), c => c.Kind == "donate"); // The observed DEV_COMPLETED advances to the station.
    }

    [SkippableFact]
    public void Waits_require_both_duration_and_live_tech_mask_and_loop_is_opt_in()
    {
        Require.Artwork(); var f = new Fixture();
        var plan = new ResearchPlan { Core = "PCore001", CoreVersion = f.Tree.Core.Version, CivId = 1,
            Name = "wait", Steps = [new() { Action = "wait", Name = "hold", WaitSeconds = 10,
                RequiredTechs = f.World.Developments.ToBytes() }] };
        var commander = new ResearchCommander(2, "epoch", f.Tree, plan, f.World.Developments, f.Reservations, loop: true);
        var world = f.World with { Developments = TechTreeBitMask.Empty };
        commander.Tick(world);
        commander.Tick(world with { Time = 20 });
        Assert.Equal("waiting for tech mask", commander.Telemetry!.Status);
        commander.Tick(f.World with { Time = 21 });
        Assert.Equal(100, commander.Telemetry!.ProgressPercent);
        commander.Tick(f.World with { Time = 22 });
        commander.Tick(f.World with { Time = 23 });
        Assert.Equal(1, commander.Telemetry!.Cycles);
        Assert.Equal("wait", commander.Telemetry.Action);
        Assert.True(commander.Telemetry.ProgressPercent < 100);
    }

    [SkippableFact]
    public void Briefing_production_dev_history_and_asteroid_destruction_use_real_receive_dispatch()
    {
        Require.Artwork(); var f = new Fixture();
        using var cs = new ConnectionSession();
        void Feed(IFedMessage m) => ((ITransportSite)cs).OnReceive(ConnectionId.Server, FedPacket.Pack(m.Encode()));
        var briefing = new ResearchBriefing { Epoch = "test", SideId = 0, Plan = f.Tree.Generate("PCore001"),
            IncomeMultiplier = 50, CompletedDevelopments = [f.Plan.Steps[0].Id] };
        Feed(briefing);
        Assert.Equal(briefing.Plan.Steps.Count, cs.World.ResearchBriefing!.Plan.Steps.Count);
        Assert.Equal(50, cs.World.ResearchBriefing.IncomeMultiplier);
        Feed(new ResearchProduction { SideId = 0, ShipId = 7, DroneTypeId = 8, StationTypeId = 9 });
        Assert.Equal((short)9, cs.World.ResearchProduction[7].StationTypeId);
        Feed(new ExportShip { ShipId = 7, HullId = f.Tree.Core.HullTypes.Keys.First(), SideId = 0 });
        var development = BucketCatalog.Describe(f.Tree.Core, f.World.Developments,
            GlobalAttributeSet.FromArray(f.Tree.Civ.GasBaseAttributes)).First(b => b.ItemType == BucketItemType.BitDevelopment);
        Assert.Null(BotManager.FindNewDrone(cs, 0, [], development));
        Feed(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrTerminate });
        Assert.True(cs.World.Ships[7].IsDead);
        Assert.False(Assert.Single(BotManager.ResearchSnapshot(cs, 0).Units).Alive);
        Feed(new ResearchProduction { SideId = 0, ShipId = 8, DroneTypeId = 8, StationTypeId = 9 });
        Feed(new ShipDelete { ShipId = 8, Sdr = ShipDelete.SdrKilled });
        Assert.False(Assert.Single(BotManager.ResearchSnapshot(cs, 0).Units, u => u.Id == 8).Alive);
        Assert.False(cs.World.Ships.ContainsKey(8)); // Death before the first pose creates no phantom.
        for (short id = 0; id < 100; id++) Feed(new DevCompleted { DevId = id });
        Assert.True(cs.World.CompletedDevelopments.Count >= 100); // Independent of 64-event sound ring.
        var rock = new DataAsteroidIGC(1, [0,0,0], [0,1,0], [1,0,0], [0,0,0,1], 0,0,1, 10,0,60,"rock","","",0,"test",0);
        Feed(new ExportAsteroid { Asteroid = rock });
        Feed(new EnterGame()); // Native live-object messages follow the sync-complete barrier.
        Feed(new AsteroidDestroyed { AsteroidId = 10 });
        Assert.Empty(cs.World.Asteroids);
        var bytes = briefing.Encode();
        Assert.Throws<ArgumentException>(() => ResearchBriefing.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<ArgumentException>(() => ResearchProduction.Decode(new byte[12]));
        cs.World.Reset();
        Assert.Null(cs.World.ResearchBriefing); Assert.Empty(cs.World.ResearchProduction); Assert.Empty(cs.World.CompletedDevelopments);
    }
}
