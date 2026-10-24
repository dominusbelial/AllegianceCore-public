using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [Fact]
    public void P745_Own_missile_quantity_counts_confirmed_salvos_and_survives_sector_transit()
    {
        using var cs = Session();
        MissileLaunchData Entry(short id) => new(Vector3.Zero, new(0,0,-1), Vector3.Zero, id);
        cs.World.Apply(new FireMissile { LauncherId = 9, Entries = [Entry(901)] });
        cs.World.Apply(new FireMissile { LauncherId = 1, Entries = [] });
        Assert.Equal(0, cs.World.OwnMissilesFired);
        cs.World.Apply(new FireMissile { LauncherId = 1, Entries = [Entry(902), Entry(903)] });
        Assert.Equal(2, cs.World.OwnMissilesFired);
        cs.World.Apply(new SetCluster { SectorId = 2, Ship = Pose(1) });
        Assert.Equal(2, cs.World.OwnMissilesFired);
        cs.World.BeginNextMission(); Assert.Equal(0, cs.World.OwnMissilesFired);
    }

    [SkippableTheory]
    [InlineData("research", true)] [InlineData("unlocked", false)]
    public void P745_Empty_station_missiles_trigger_a_real_refit_only_after_confirmed_launches(string mode, bool recalls)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = mode });
        cs.World.Apply(new SetMoney { Money = 1_000_000, ShipId = 1 });
        var core = Catalog.RepresentativeCore(); var target = core.StationTypes[rig.Stations[1].StationTypeId];
        var accepted = TechLoadoutSelector.Select(core, 3, Showcase.HullRole.Bomber, core.ComputeRequiredTechUnion(),
            core.Civilizations[3].InitialStationTypeId, target)!;
        Assert.NotNull(accepted.Strike);
        Set(bot, "PreviousLegalLoadout", accepted); Set(bot, "Granted", new BotManager.LoadoutGrant(accepted.HullId, accepted.Parts));
        Set(bot, "HullId", accepted.HullId); Set(bot, "Parts", accepted.Parts);
        Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", true);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = accepted.HullId });
        rig.Mature();
        int missiles = accepted.Parts.Where(p => core.Launchers.TryGetValue(p.PartId, out var launcher)
            && core.MissileTypes.ContainsKey(launcher.ExpendabletypeId)).Sum(p => (int)p.Amount);
        Assert.True(missiles > 0);
        Assert.False((bool)Call("ResearchSiegeNeedsResupply", null, bot, cs, target)!);
        for (int shot = 0; shot < missiles; shot++)
        {
            Assert.False((bool)Call("ResearchSiegeNeedsResupply", null, bot, cs, target)!);
            cs.World.Apply(new FireMissile { LauncherId = 1,
                Entries = [new MissileLaunchData(Vector3.Zero, new(0,0,-1), Vector3.Zero, (short)(900 + shot))] });
        }
        Assert.Equal(recalls, (bool)Call("ResearchSiegeNeedsResupply", null, bot, cs, target)!);
        if (recalls)
        {
            Assert.True((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
            Assert.True(Get<bool>(bot, "FinaleRefitting")); Assert.False(Get<bool>(bot, "IsPod"));
            Assert.Equal(0, Get<int>(bot, "RelaunchAttempts")); Assert.Equal((short)1, Get<short>(bot, "ShipId"));
            Set(bot, "RelaunchAttempts", 1); Set(bot, "RelaunchAckHandled", false);
            ((Allegiance.Core.Transport.ITransportSite)cs).OnReceive(Allegiance.Core.Transport.ConnectionId.Server,
                FedPacket.Pack(new BuyLoadoutAck { HullId = accepted.HullId, Parts = accepted.Parts,
                    FBoughtEverything = true, FLaunch = true }.Encode()));
            Call("HandleRelaunchAck", null, bot, cs);
            Assert.Equal(cs.World.OwnMissilesFired, Get<long>(bot, "FinaleMissileLaunchBase"));
            Assert.False((bool)Call("ResearchSiegeNeedsResupply", null, bot, cs, target)!);
        }
    }

    [SkippableTheory]
    [InlineData("research", true, false)]
    [InlineData("research", false, true)]
    [InlineData("unlocked", true, true)]
    public void P745_Siege_dodge_ignores_retired_ship_poses_in_the_native_null_cluster(string mode, bool active, bool ghostDodged)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes.Values.First(h => Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber);
        cs.World.Apply(new ExportShip { ShipId = 1, HullId = hull.HullId, SideId = 0 });
        cs.World.Apply(new SetCluster { Ship = Pose(1, 0), SectorId = 1 });
        cs.World.Apply(new ResearchBriefing { TechMode = mode });
        var bot = Bot(cs); Set(bot, "HullId", hull.HullId); Set(bot, "FinaleBomberActive", active);
        cs.World.Apply(new ExportShip { ShipId = 9, SideId = 1, HullId = 15 });
        cs.World.Apply(new ShipStatusMessage { ShipId = 9, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
        cs.World.Apply(new HeavyShipsUpdate { TimeReference = 1, PositionReference = Vector3.Zero,
            HeavyShipEntries = [HeavyShipUpdateEntry.From(1, Vector3.Zero,
                new Ship(9, core.HullTypes[15], new Vector3(1,0,0), Vector3.Zero, Orientation.Identity), 1)] });
        var seen = cs.World.Ships[9];
        Assert.True((bool)Call("TryDodgeGoal", null, cs, bot, Vector3.Zero, null)!,
            $"sector={seen.SectorId} pose={seen.PoseSectorId} unknown={seen.Unknown} state={seen.State} station={seen.StationId} parent={seen.ParentId} gone={seen.IsGone} draw={seen.CanDrawInSector(1)}");
        cs.World.Apply(new ShipDelete { ShipId = 9, Sdr = ShipDelete.SdrLeftSector });
        Assert.NotNull(cs.World.Ships[9].Latest); Assert.True(cs.World.Ships[9].IsGone);
        Assert.Equal(ghostDodged, (bool)Call("TryDodgeGoal", null, cs, bot, Vector3.Zero, null)!);
    }

    [SkippableTheory]
    [InlineData("research", true)] [InlineData("unlocked", false)]
    public void P745_Siege_continues_to_an_observed_restart_base_after_clearing_the_original_sector(string mode, bool advances)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = mode }); rig.Mature();
        Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", true);
        Set(bot, "TargetSector", (short?)4);
        cs.World.Apply(new StationDestroyed { StationId = 101 });
        cs.World.Apply(new ExportStation { Station = Station(102, 1, 3) });
        Call("AdvanceResearchSiegeDuty", null, bot, cs);
        Assert.Equal(advances ? (short?)3 : (short?)4, Get<short?>(bot, "TargetSector"));
        Assert.Equal((short)1, cs.World.Ships[1].SectorId);
        Assert.False(cs.World.Stations.ContainsKey(101)); Assert.True(cs.World.Stations.ContainsKey(102));
    }

    [SkippableFact]
    public void P745_Accepted_fallback_fighter_returns_for_newly_available_legal_bomber()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        cs.World.Apply(new SetMoney { Money = 1_000_000, ShipId = 1 });
        // Remove bomber records only in this selector fixture to obtain a real
        // legal fallback, as in P733's available-strike regression.
        var file = IgcReader.ParseFile(Path.Combine(Catalog.ArtRoot, "PCore001.igc"));
        var fallbackCore = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version,
            Body = file.Body, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed,
            Records = file.Records.Where(r => r.Type != IgcObjectType.HullType
                || Showcase.ClassifyHull(DataHullTypeIGC.Decode(file.Body.Slice(r.PayloadOffset, r.Size).Span)) != Showcase.HullRole.Bomber).ToArray() });
        var target = fallbackCore.StationTypes[rig.Stations[1].StationTypeId];
        var accepted = TechLoadoutSelector.SelectAvailableStrike(fallbackCore, 25, fallbackCore.ComputeRequiredTechUnion(),
            fallbackCore.Civilizations[25].InitialStationTypeId, target, 1_000_000)!;
        Assert.NotNull(accepted.Strike);
        Assert.NotEqual(Showcase.HullRole.Bomber, Showcase.ClassifyHull(fallbackCore.HullTypes[accepted.HullId]));
        Set(bot, "PreviousLegalLoadout", accepted); Set(bot, "Granted", new BotManager.LoadoutGrant(accepted.HullId, accepted.Parts));
        Set(bot, "HullId", accepted.HullId); Set(bot, "Parts", accepted.Parts);
        Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", true);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = accepted.HullId });
        rig.Mature();
        Assert.True((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.True(Get<bool>(bot, "FinaleRefitting"));
        Assert.Equal(0, Get<int>(bot, "RelaunchAttempts")); Assert.Equal((short)1, Get<short>(bot, "ShipId"));
    }

    [SkippableFact]
    public void P745_Refit_keeps_native_payday_instead_of_requiring_a_death_insurance_payment()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        cs.World.Apply(new SetMoney { Money = 0, ShipId = 1 }); rig.Mature();
        Assert.False((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.True(Get<bool>(bot, "FinaleKeepsPayday")); Assert.False(Get<bool>(bot, "FinaleRefitting"));
        Assert.Equal(0, cs.World.MyMoney); Assert.Equal(0, Get<int>(bot, "RelaunchAttempts"));
    }

    [SkippableTheory]
    [InlineData(true)] [InlineData(false)]
    public void P745_Refit_launch_ack_resumes_flight_even_after_a_partial_grant_without_counting_a_pod_cycle(bool verified)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" }); rig.Mature();
        Set(bot, "FinaleRefitting", true); Set(bot, "FinaleRefitLaunchPending", true);
        Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", verified);
        Set(bot, "RelaunchAckHandled", true); Set(bot, "PodDocked", true);
        int cycles = Get<int>(bot, "CompletedPodCycles");
        Assert.False((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.False(Get<bool>(bot, "FinaleRefitting")); Assert.False(Get<bool>(bot, "PodDocked"));
        Assert.Equal(cycles, Get<int>(bot, "CompletedPodCycles"));
        Assert.Equal(BotManager.StageReleasedPhase, Get<byte>(bot, "StagePhase"));
        Assert.Equal((short)1, Get<short>(bot, "ShipId"));
    }

    [SkippableFact]
    public void P745_Research_reserves_an_accepted_flying_strike_without_waiting_for_a_death_or_new_purchase()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        var core = Catalog.RepresentativeCore(); var target = core.StationTypes[rig.Stations[1].StationTypeId];
        var chosen = ShowcaseFinale.SelectLoadout(core, target)!;
        var accepted = new TechLoadoutSelector.Selection(chosen.HullId, chosen.HullName, chosen.Parts, chosen.Price, "accepted", chosen);
        Set(bot, "PreviousLegalLoadout", accepted); Set(bot, "Granted", new BotManager.LoadoutGrant(chosen.HullId, chosen.Parts));
        Set(bot, "HullId", chosen.HullId); Set(bot, "Parts", chosen.Parts);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = chosen.HullId });
        rig.Mature();
        Assert.False((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.True(Get<bool>(bot, "FinaleBomberActive")); Assert.True(Get<bool>(bot, "FinaleGrantVerified"));
        Assert.False(Get<bool>(bot, "FinaleRefitting")); Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
        Assert.Same(chosen.Parts, Get<IReadOnlyList<ExpandedPartData>>(bot, "Parts"));
        Assert.Equal(0, Get<int>(bot, "RelaunchAttempts")); Assert.Equal((short)1, Get<short>(bot, "ShipId"));
    }

    [SkippableTheory]
    [InlineData("research", true, true)]
    [InlineData("research", false, false)]
    [InlineData("unlocked", true, false)]
    public void P745_Research_refit_is_a_return_to_dock_duty_only_when_a_legal_strike_exists(string mode, bool tech, bool refits)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1), allTech: tech);
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = mode });
        cs.World.Apply(new SetMoney { Money = 1_000_000, ShipId = 1 });
        rig.Mature();
        Assert.Equal(refits, (bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.Equal(refits, Get<bool>(bot, "FinaleRefitting"));
        Assert.False(Get<bool>(bot, "FinaleBomberActive")); Assert.Equal(0, Get<int>(bot, "RelaunchAttempts"));
        Assert.Equal((short)1, Get<short>(bot, "ShipId"));
    }

    [SkippableFact]
    public void P745_Research_strike_visible_bombers_share_the_shortest_enemy_home_entry()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 2));
        foreach (var cs in rig.Sessions) cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        // Two equally legal entry routes: 1->2->4 and 1->3->4.
        foreach (var cs in rig.Sessions)
        {
            typeof(Allegiance.ClientCore.State.ClientWorld).GetProperty("Graph")!.SetValue(cs.World, null);
            cs.World.Apply(new ClusterInfo { Clusters = [new(1,true),new(2,false),new(3,false),new(4,false)],
                Warps = [new(1,2),new(2,4),new(1,3),new(3,4)] });
        }
        rig.Mature();
        foreach (var bot in rig.Bots.Take(2))
        {
            Set(bot, "PodDocked", true);
            Call("PrepareFinaleRelaunch", null, bot, rig.Started + 1801);
        }
        var routes = rig.Bots.Take(2).Select(b => Get<List<short>?>(b, "RoutePath")).ToArray();
        Assert.All(routes, r => { Assert.NotNull(r); Assert.Equal((short)1,r[0]); Assert.Equal((short)4,r[^1]); });
        // P801 follow-up (rig 221 batch 26): the visible element shares the shortest entry so the wave can
        // mass; only cloaked lone wolves take alternative entries (BotManager.FinaleRouteIndex, P801StrikeMassTests).
        Assert.Equal(string.Join(',', routes[0]!), string.Join(',', routes[1]!));
    }

    [SkippableTheory]
    [InlineData("research", true, false)]
    [InlineData("research", false, true)]
    [InlineData("unlocked", true, true)]
    public void P745_Travelling_siege_keeps_station_duty_when_a_fighter_hits_it(string mode, bool active, bool interrupts)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); cs.World.Apply(new ResearchBriefing { TechMode = mode });
        var bot = Bot(cs); var manager = Manager(bot); DefenseNoSocketSender(bot);
        Set(bot, "TargetSector", (short?)4); Set(bot, "Role", "bomber"); Set(bot, "FinaleBomberActive", active);
        DefenseEnemy(cs.World, x: 5000);
        cs.World.Apply(new ProjectileHullHit { SourceId = 9, TargetId = 1, ClusterId = 1 });
        Call("DriveCombat", manager, bot, 10.0, null);
        Assert.Equal(interrupts ? (short?)9 : null, Get<short?>(bot, "TargetId"));
        Assert.Equal(!interrupts, Get<List<short>>(bot, "Path").Count > 0);
    }

    [SkippableTheory]
    [InlineData("research", true, false)]
    [InlineData("research", false, true)]
    [InlineData("unlocked", true, true)]
    public void P745_Station_siege_dodges_other_obstacles_but_skips_its_attack_target(string mode, bool active, bool dodges)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes.Values.First(h => Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = hull.HullId });
        cs.World.Apply(new ResearchBriefing { TechMode = mode });
        var station = Station(101, 1, 1, 20);
        cs.World.Apply(new ExportStation { Station = station });
        var bot = Bot(cs); Set(bot, "HullId", hull.HullId); Set(bot, "Role", "bomber");
        Set(bot, "FinaleBomberActive", active); Set(bot, "TargetId", (short?)101);
        object?[] args = [cs, bot, Vector3.Zero, null];
        bool result = (bool)Call("TryDodgeGoal", null, args)!;
        Assert.Equal(dodges, result);
        // The attack-target exception must never disable avoidance of another station.
        cs.World.Apply(new ExportStation { Station = Station(102, 1, 1, -20) });
        Assert.True((bool)Call("TryDodgeGoal", null, args)!);
    }
}

public sealed class P745StationDamageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData((short)3, (short)25)]
    [InlineData((short)25, (short)3)]
    public void Native_bomber_weapons_reduce_station_pools_with_the_native_matrix_and_regen(short attacker, short defender)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var target = core.StationTypes[core.Civilizations[defender].InitialStationTypeId];
        var selected = TechLoadoutSelector.Select(core, attacker, Showcase.HullRole.Bomber, core.ComputeRequiredTechUnion(),
            core.Civilizations[attacker].InitialStationTypeId, target);
        Assert.NotNull(selected); Assert.NotNull(selected.Strike);
        output.WriteLine(selected.Reason);
        var map = MissionMap.FromLists([new DataClusterIGC(0, [0, 1, 0], 0, 0, 0, 1, 0, 0, "test", "", "", 0, 0, 0, true, false)], [], [], []);
        var mission = Mission.Assemble(core, map, [], new NullIgcSite());
        var ship = new Ship(1, core.HullTypes[selected.HullId], Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        int damaged = 0;
        foreach (var part in selected.Parts)
        {
            byte damageType; float power;
            if (core.PartTypes.TryGetValue(part.PartId, out var type) && type.Type is DataPartTypeIGC.Weapon gun)
            {
                var round = core.ProjectileTypes[gun.ProjectileTypeId];
                damageType = round.DamageType; power = round.Power;
            }
            else if (core.Launchers.TryGetValue(part.PartId, out var launcher) && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile))
            { damageType = missile.DamageType; power = missile.Power; }
            else continue;
            var station = new Station(new([0,0,0], [0,1,0], [0,0,-1], [0,0,0,1], 1, 0, 101, target.StationTypeId, 255,255,"target"), target) { Mission = mission };
            float matrix = core.Constants.DamageConstants[damageType & 0x1f][target.DefenseTypeShield];
            station.ReceiveDamage(damageType, power, 0, Vector3.Zero, Vector3.Zero, ship);
            if (power <= 0 || matrix <= 0) continue;
            damaged++;
            Assert.Equal(Math.Max(0, 1 - power * matrix / target.MaxShieldHitPoints), station.ShieldFraction, 5);
            float before = station.ShieldFraction;
            if (station.ArmorFraction == 1)
            {
                station.Regenerate(.1f);
                Assert.Equal(Math.Min(1, before + .1f * target.ShieldRegeneration / target.MaxShieldHitPoints), station.ShieldFraction, 5);
            }
            output.WriteLine($"part={part.PartId} type={damageType} power={power} shieldMatrix={matrix} shield={before}");
        }
        Assert.True(damaged > 0);
    }
}
