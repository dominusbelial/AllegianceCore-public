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

    [Fact]
    public void Support_phase_signal_precedence_and_destination()
    {
        // Opening: the fighters' rendezvous mode wins over where they happen to be.
        var opening = BotManager.SupportPhaseFrom(false, null, false, -1, [(7, 5), (8, 2)], [(2, 9)]);
        Assert.Equal(BotManager.SupportPhaseKind.Opening, opening.Kind);
        Assert.Equal((short?)7, opening.AnchorSector);
        // No rendezvous knowledge (fog): where the fighters actually are (tie keeps the first).
        Assert.Equal((short?)2, BotManager.SupportPhaseFrom(false, null, false, -1, [], [(2, 9), (3, 9)]).AnchorSector);
        // An active SC-7/8/9 strike wave outranks the opening...
        var wave = BotManager.SupportPhaseFrom(false, null, true, 5, [(7, 5)], []);
        Assert.Equal(BotManager.SupportPhaseKind.Strike, wave.Kind);
        Assert.Equal((short?)5, wave.AnchorSector);
        // ...and an active finale (bomb runs) outranks the strike wave.
        var finale = BotManager.SupportPhaseFrom(true, 9, true, 5, [(7, 5)], []);
        Assert.Equal(BotManager.SupportPhaseKind.Strike, finale.Kind);
        Assert.Equal((short?)9, finale.AnchorSector);
        // No signal at all → Opening with no anchor (the pilot keeps its own rendezvous).
        Assert.Null(BotManager.SupportPhaseFrom(false, null, false, -1, [], []).AnchorSector);
        // Same side state, different phase → different destination.
        var phase = new BotManager.SupportPhaseSignal(BotManager.SupportPhaseKind.Opening, 7);
        Assert.Equal(((short?)7, true), BotManager.PickSupportDestination(null, phase, 3));
        var strike = new BotManager.SupportPhaseSignal(BotManager.SupportPhaseKind.Strike, 9);
        Assert.Equal(((short?)9, true), BotManager.PickSupportDestination(null, strike, 3));
        // A damaged own station recalls the pilot in ANY phase.
        Assert.Equal(((short?)4, false), BotManager.PickSupportDestination(4, strike, 3));
        // Without a phase signal the pilot keeps its own rendezvous.
        Assert.Equal(((short?)3, false), BotManager.PickSupportDestination(null,
            new BotManager.SupportPhaseSignal(BotManager.SupportPhaseKind.Opening, null), 3));
    }

    [Fact]
    public void Support_hold_hysteresis_drops_full_stale_stuck_and_leashed_targets()
    {
        const double now = 1000, started = 990, lastImprove = 998;
        const float best = .8f;
        var Keep = BotManager.SupportHoldDecision.Keep;
        // A healthy, fresh, improving observation keeps the hold.
        Assert.Equal(Keep, BotManager.EvaluateRepairHold(now, started, best, .85f, .4, lastImprove));
        // Full → completed (native stop law, shipIGC.cpp:2668-2671).
        Assert.Equal(BotManager.SupportHoldDecision.DroppedFull, BotManager.EvaluateRepairHold(now, started, best, 1f, .4, lastImprove));
        // Dead/gone is not a completion.
        Assert.Equal(BotManager.SupportHoldDecision.DroppedStale, BotManager.EvaluateRepairHold(now, started, best, 0f, .4, lastImprove));
        // A stale observation is dropped.
        Assert.Equal(BotManager.SupportHoldDecision.DroppedStale, BotManager.EvaluateRepairHold(now, started, best, .85f, 6.1, lastImprove));
        // A target that stopped improving is dropped.
        Assert.Equal(BotManager.SupportHoldDecision.DroppedNoImprove, BotManager.EvaluateRepairHold(now, started, best, .85f, .4, 989));
        // The leash: never repair one target longer than RepairLeashSeconds.
        Assert.Equal(BotManager.SupportHoldDecision.DroppedLeash, BotManager.EvaluateRepairHold(now, 950, best, .9f, .4, 999.9));
        // Precedence: full beats the leash; the leash beats stale.
        Assert.Equal(BotManager.SupportHoldDecision.DroppedFull, BotManager.EvaluateRepairHold(now, 900, best, 1f, 99, 900));
        Assert.Equal(BotManager.SupportHoldDecision.DroppedLeash, BotManager.EvaluateRepairHold(now, 900, best, .85f, 99, 900));
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
    public void SC2_Support_pick_respects_phase_damage_bars_support_ring_rule_cooldown_and_P778a_order()
    {
        Require.Artwork();
        using var cs = Session(); var w = cs.World;
        DefenseMap(w);
        void Friend(short id, float hull, short side = 0, short sector = 2, short hullId = 15)
        {
            w.Apply(new ExportShip { ShipId = id, SideId = side, HullId = hullId });
            var bytes = new byte[ServerShipUpdate.Width];
            var writer = new WireWriter(bytes); Pose(id, id * 10).EncodeBody(ref writer); bytes[40] = (byte)(hull * 240);
            var reader = new WireReader(bytes);
            w.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
            w.Apply(new ShipStatusMessage { ShipId = id, HullId = hullId, SectorId = sector, StationId = -1, ParentId = -1 });
        }
        // Home = sector 2, battle = sector 1 (no candidates there unless placed).
        HashSet<short> supports = [1, 2];   // the pilot (1) and its peer support pilot (2)
        // P791 ring rule: a slightly-damaged support pilot at home is never a target.
        Friend(2, .9f);
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 2, Vector3.Zero, battleSector: 1, supportShipIds: supports));
        // ...in the battle sector it is a target only when genuinely damaged.
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 2, Vector3.Zero, battleSector: 2, supportShipIds: supports));
        Friend(2, .8f);
        Assert.Equal((short)2, BotManager.PickRepairTarget(w, 0, 1, 2, Vector3.Zero, battleSector: 2, supportShipIds: supports)!.Value.Id);
        // Non-support ship at home: the 0.85 bar (bumps above it are ignored)...
        w.Apply(new KillShip { ShipId = 2, BDeathCredit = true });
        Friend(5, .9f);
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 2, Vector3.Zero, battleSector: 1, supportShipIds: supports));
        // ...and real damage starts a repair at home.
        w.Apply(new KillShip { ShipId = 5, BDeathCredit = true });
        Friend(6, .8f);
        Assert.Equal((short)6, BotManager.PickRepairTarget(w, 0, 1, 2, Vector3.Zero, battleSector: 1, supportShipIds: supports)!.Value.Id);
        // In the battle sector ANY damage still qualifies (P778a behaviour unchanged).
        w.Apply(new KillShip { ShipId = 6, BDeathCredit = true });
        Friend(7, .95f, sector: 1);
        Assert.Equal((short)7, BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, battleSector: 1, supportShipIds: supports)!.Value.Id);
        // A target under cooldown is skipped for the tick.
        Assert.Null(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, battleSector: 1, supportShipIds: supports, cooldownTarget: 7));
        // P778a ordering unchanged with the new parameters: bomber > pusher > station > ship.
        w.Apply(new KillShip { ShipId = 7, BDeathCredit = true });
        Friend(8, .5f, sector: 1);                  // plain damaged ship
        w.Apply(new StationsUpdate { Entries = [new(100, 120, 240)] });   // damaged own station
        var withFinale = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, [9], [7], battleSector: 1, supportShipIds: supports);
        Assert.True(withFinale!.Value.Station);     // dead/unknown finale ids fall through to the station
        var ordered = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, [8], [], battleSector: 1, supportShipIds: supports);
        Assert.True(ordered!.Value.FinaleBomber);   // bomber outranks the station
        ordered = BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, [], [8], battleSector: 1, supportShipIds: supports);
        Assert.True(ordered!.Value.FinalePusher);   // pusher outranks the station
        Assert.True(BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, battleSector: 1, supportShipIds: supports)!.Value.Station);
        w.Apply(new StationsUpdate { Entries = [new(100, 240, 240)] });   // station full again
        Assert.Equal((short)8, BotManager.PickRepairTarget(w, 0, 1, 1, Vector3.Zero, battleSector: 1, supportShipIds: supports)!.Value.Id);
    }

    [SkippableFact]
    public void SC2_Support_hold_drops_a_full_target_and_cools_it_down()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World);
        void SetHull(short id, float hull)
        {
            var bytes = new byte[ServerShipUpdate.Width];
            var writer = new WireWriter(bytes); Pose(id, id * 10).EncodeBody(ref writer); bytes[40] = (byte)(hull * 240);
            var reader = new WireReader(bytes);
            cs.World.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        }
        cs.World.Apply(new ExportShip { ShipId = 2, SideId = 0, HullId = 15 });
        cs.World.Apply(new ShipStatusMessage { ShipId = 2, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
        SetHull(2, .5f);
        var bot = Bot(cs); Set(bot, "Role", "support");
        var manager = Manager(bot);
        var target = new BotManager.RepairTarget(2, false, 1, new Vector3(100, 0, 0), Vector3.Zero, 10);
        // First tick starts the hold and returns the target (observed fraction from the world).
        var held = Call("ApplyRepairHold", manager, bot, cs, target, 10.0, (short)1, (short?)1);
        Assert.NotNull(held);
        Assert.Equal((short?)2, Get<short?>(bot, "SupportHoldTarget"));
        Assert.True(MathF.Abs(Get<float>(bot, "SupportHoldLast") - .5f) < .01f);
        // The same target observed full next tick is dropped and cooled down.
        SetHull(2, 1f);
        Assert.Null(Call("ApplyRepairHold", manager, bot, cs, target, 12.0, (short)1, (short?)1));
        Assert.Null(Get<short?>(bot, "SupportHoldTarget"));
        Assert.Equal((short)2, Get<short>(bot, "SupportCooldownTarget"));
        Assert.True(Get<double>(bot, "SupportCooldownUntil") >= 22.0);
        // A fresh hold (new target change) starts again regardless of the cooldown.
        Assert.NotNull(Call("ApplyRepairHold", manager, bot, cs, target, 40.0, (short)1, (short?)1));
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
