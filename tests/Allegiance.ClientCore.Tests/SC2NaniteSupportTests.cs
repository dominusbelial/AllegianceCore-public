using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class SC2NaniteSupportTests(ITestOutputHelper output)
{
    [Fact]
    public void Support_share_is_bounded_configurable_and_distributed_per_side()
    {
        Assert.Equal(.1, BotManager.SupportSettings.Read(_ => null).Share);
        Assert.Equal(.1, BotManager.SupportSettings.Read(_ => "NaN").Share);
        Assert.Equal(0, BotManager.SupportSettings.Read(_ => "-1").Share);
        Assert.Equal(1, BotManager.SupportSettings.Read(_ => "2").Share);
        Assert.Equal(.25, BotManager.SupportSettings.Read(_ => ".25").Share);
        Assert.Equal(2, Enumerable.Range(0, 15).Count(i => BotManager.SupportSlot(i, 15, .1)));
        Assert.Equal(10, Enumerable.Range(0, 100).Count(i => BotManager.SupportSlot(i, 100, .1)));
        Assert.Equal(0, Enumerable.Range(0, 15).Count(i => BotManager.SupportSlot(i, 15, 0)));
        Assert.Equal(15, Enumerable.Range(0, 15).Count(i => BotManager.SupportSlot(i, 15, 1)));
    }

    [SkippableTheory]
    [InlineData((short)25)] [InlineData((short)3)]
    public void Support_selection_obeys_research_dock_budget_and_mounts(short civ)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, civ));
        Assert.Null(TechLoadoutSelector.SelectSupport(core, civ, TechTreeBitMask.Empty, state.Tree.Civ.InitialStationTypeId, 1_000_000));
        Assert.Null(TechLoadoutSelector.SelectSupport(core, civ, state.Techs, -1, 1_000_000));
        int checks = 0;
        foreach (var step in state.Tree.Generate("PCore001").Steps)
        {
            state.Apply(step);
            foreach (short dock in state.Stations.Order())
            {
                var selected = TechLoadoutSelector.SelectSupport(core, civ, state.Techs, dock, 1_000_000);
                if (selected is null) continue;
                checks++;
                Assert.True(TechLoadoutSelector.Legal(core, civ, state.Techs, dock, selected.HullId, selected.Parts), selected.Reason);
                Assert.True(selected.Price <= 1_000_000);
                Assert.Equal(selected.Parts, TechLoadoutSelector.SelectSupport(core, civ, state.Techs, dock, selected.Price)!.Parts);
                Assert.Null(TechLoadoutSelector.SelectSupport(core, civ, state.Techs, dock, -1));
                var gun = selected.Parts.Single(p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.Weapon);
                Assert.True(core.HullTypes[selected.HullId].Hardpoints[gun.MountId].Fixed);
                var part = TechLoadoutSelector.Part(core, gun.PartId);
                var w = (DataPartTypeIGC.Weapon)part.Type;
                var round = core.ProjectileTypes[w.ProjectileTypeId];
                Assert.True(round.Power < 0);
                Assert.True(w.EnergyPerShot <= core.HullTypes[selected.HullId].MaxEnergy);
                if (checks == 1) output.WriteLine($"civ={civ} step={step}: {selected.Reason}; round={round.ProjectileTypeId} power={round.Power} speed={round.Speed} lifespan={round.Lifespan}");
            }
        }
        Assert.True(checks > 0, $"no legal repair equipment in completed civ {civ} research");
        var initial = new ResearchState(new(core, civ));
        output.WriteLine($"initial support={TechLoadoutSelector.SelectSupport(core,civ,initial.Techs,initial.Tree.Civ.InitialStationTypeId,1_000_000)?.Reason ?? "locked"}");
    }
}

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void SC2_Repair_targets_exclude_enemy_full_dead_pod_docked_and_other_sectors()
    {
        Require.Artwork();
        using var cs = Session(); var w = cs.World;
        DefenseMap(w);
        void Friend(short id, float hull, short side = 0, short sector = 1, short hullId = 15)
        {
            w.Apply(new ExportShip { ShipId = id, SideId = side, HullId = hullId });
            var bytes = new byte[ServerShipUpdate.Width];
            var writer = new WireWriter(bytes); Pose(id, id * 10).EncodeBody(ref writer); bytes[40] = (byte)(hull * 240);
            var reader = new WireReader(bytes);
            w.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
            w.Apply(new ShipStatusMessage { ShipId = id, HullId = hullId, SectorId = sector, StationId = -1, ParentId = -1 });
        }
        Friend(2, .5f); Friend(3, 1); Friend(4, .2f, 1); Friend(5, .1f, sector: 2);
        Friend(6, .2f, hullId: Catalog.RepresentativeCore().HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId); Friend(7, 0);
        Assert.Equal((short)2, BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero)!.Value.Id);
        w.Apply(new StationsUpdate { Entries = [new(100, 120, 240)] });
        var station = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero)!.Value;
        Assert.True(station.Station); Assert.Equal((short)100, station.Id);
        w.Apply(new StationsUpdate { Entries = [new(100, 240, 240)] });
        w.Apply(new KillShip { ShipId = 2, BDeathCredit = true });
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero));
        Friend(8, .3f); w.Apply(new ShipStatusMessage { ShipId = 8, HullId = 15, SectorId = 1, StationId = 100, ParentId = -1 });
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero));
    }
    [SkippableFact]
    public void SC2_Equipped_support_engages_friendlies_and_retains_role_through_finale_and_life_reset()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseEnemy(cs.World, 9);
        var core = Catalog.RepresentativeCore(); var state = new ResearchState(new(core, 25));
        var selected = TechLoadoutSelector.SelectSupport(core, 25, state.Techs, state.Tree.Civ.InitialStationTypeId, 1_000_000)!;
        var bot = Bot(cs); Set(bot, "Role", "support"); Call("ApplyTechLoadout", null, bot, selected);
        DefenseNoSocketSender(bot);
        var bytes = new byte[ServerShipUpdate.Width]; var writer = new WireWriter(bytes); Pose(2, 100).EncodeBody(ref writer); bytes[40] = 120;
        var reader = new WireReader(bytes);
        cs.World.Apply(new ExportShip { ShipId = 2, SideId = 0, HullId = 15 });
        cs.World.Apply(new SingleShipUpdate { OidTarget = 2, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        cs.World.Apply(new ShipStatusMessage { ShipId = 2, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
        Call("EngageCombat", Manager(bot), bot, 10.0, null);
        Assert.Equal((short?)2, Get<short?>(bot, "TargetId")); // nearby enemy 9 never receives nanites
        cs.World.Apply(new StationsUpdate { Entries = [new(100, 120, 240)] });
        Call("EngageCombat", Manager(bot), bot, 11.0, null);
        Assert.Equal((short?)100, Get<short?>(bot, "TargetId"));
        Call("ResetNewLife", null, bot, 12.0, true);
        Assert.Equal("support", Get<string>(bot, "Role")); Assert.Null(Get<short?>(bot, "SupportTraceTarget"));
        using var rig = new FinaleRig(new(Bombers: 1));
        foreach (var peer in rig.Sessions) peer.World.Apply(new ResearchBriefing { TechMode = "research" });
        Set(rig.Bots[0], "Role", "support"); rig.Mature();
        Assert.False(Get<bool>(rig.Bots[0], "FinaleBomberReserved"));
        Assert.False(Get<bool>(rig.Bots[0], "FinaleEscort"));
    }

    [SkippableFact]
    public void SC2_Locked_support_refits_at_a_legal_dock_and_waits_for_the_real_purchase_ack()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World); DefenseNoSocketSender(Bot(cs));
        var core = Catalog.RepresentativeCore(); var state = new ResearchState(new(core, 25));
        cs.World.Apply(new SideRoster { Sides = [new(0, 25, 1, "Alpha")] });
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1) with { StationTypeId = state.Tree.Civ.InitialStationTypeId } });
        cs.World.Apply(new SideTechChange { SideId = 0, TtbmDevelopments = TechTreeBitMask.Empty.ToBytes() });
        cs.World.Apply(new SetMoney { ShipId = 1, Money = 0 });
        var bot = Bot(cs); Set(bot, "Role", "support"); Set(bot, "TechLoadouts", true);
        var manager = Manager(bot);
        Assert.False((bool)Call("TryDriveSupport", manager, bot, 10.0)!);
        Assert.False(Get<bool>(bot, "SupportRefitting"));
        cs.World.Apply(new SideTechChange { SideId = 0, TtbmDevelopments = state.Techs.ToBytes() });
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 16.0)!);
        Assert.True(Get<bool>(bot, "SupportRefitting")); Assert.Equal((short)100, Get<short>(bot, "PodStationId"));
        Assert.Equal(0, Get<int>(bot, "RelaunchAttempts")); Assert.False(Get<bool>(bot, "IsPod"));
        cs.World.Apply(new DockedNotify { StationId = 100 });
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 17.0)!);
        Assert.Equal(1, Get<int>(bot, "RelaunchAttempts"));
        var selected = Get<TechLoadoutSelector.Selection>(bot, "PendingTechLoadout");
        Assert.Contains("Support", selected.Reason);
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 17.1)!); // purchase alone is not a launch
        ((Allegiance.Core.Transport.ITransportSite)cs).OnReceive(Allegiance.Core.Transport.ConnectionId.Server,
            FedPacket.Pack(new BuyLoadoutAck { HullId = selected.HullId, Parts = selected.Parts, FBoughtEverything = true, FLaunch = true }.Encode()));
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = selected.HullId });
        cs.World.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) });
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 18.0)!);
        Assert.False(Get<bool>(bot, "SupportRefitting")); Assert.False(Get<bool>(bot, "PodDocked"));
        Assert.Equal("support", Get<string>(bot, "Role")); Assert.NotNull(Get<TechLoadoutSelector.Selection?>(bot, "PreviousLegalLoadout"));
    }

}
