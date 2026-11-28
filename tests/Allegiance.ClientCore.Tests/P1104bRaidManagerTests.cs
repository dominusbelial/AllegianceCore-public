using System.Runtime.CompilerServices;
using Allegiance.ClientCore.State;
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
    [SkippableFact, Trait("Tier", "Fast")]
    public void P1104b_Owner_fit_needs_a_restart_a_grant_both_route_ends_and_time_to_fire()
    {
        Require.Artwork();
        using var cs = Session();
        var world = cs.World;
        var core = Catalog.RepresentativeCore();
        world.MissionCoreResolver = _ => core;
        world.NowSeconds = () => 10;
        world.Apply(new MissionDef { SzIGCStaticFile = "PCore001", TimeStart = 100_000, GameLengthSeconds = 100 });
        world.Apply(new JoinedMission { ShipId = 1 });
        world.ObserveServerTime(100_000, 10_000);
        world.Apply(new ResearchBriefing { TechMode = "research", Epoch = "A", Plan = new() { CoreVersion = core.Version } });
        DefenseMap(world);
        var home = Station(100, 0, 1) with { StationTypeId = core.Civilizations[25].InitialStationTypeId };
        world.Apply(new ExportStation { Station = home });
        world.Apply(new ExportStation { Station = Station(91, 1, 2) });
        world.Apply(new ExportShip { ShipId = 1, HullId = 23, SideId = 0 });
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(1, 0, default, 1,
            default, Orientation.Identity, default, [0, 0, 0], 1, 1, 1, 1, 1, 1, 1) });
        ExpandedPartData[] parts = [new(158, 2, 0, ExpandedPartData.FullyMounted)];
        world.Apply(new BuyLoadoutAck { HullId = 23, Parts = parts, FLaunch = true, FBoughtEverything = true });
        var bot = Bot(cs);
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, parts));
        SustainableRaid.Fit Fit() => (SustainableRaid.Fit)Call("ObserveRaidFit", null, bot, world, world.Ships[1])!;
        Assert.Equal(home.StationId, Fit().HomeStation);
        Assert.NotNull(Fit().Weapons);
        world.Apply(new ExportWarp { Warp = new(20, 21, 60, "", "", "exit", [500, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 1) });
        Assert.Empty(Fit().TravelSeconds);
        world.Apply(new ExportWarp { Warp = new(21, 20, 60, "", "", "entry", [0, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 2) });
        Assert.Equal(5 + 500 / (.65 * core.HullTypes[23].Speed), Fit().TravelSeconds[91], 5);
        foreach (string duty in new[] { "IsExplorer", "FinaleBomberReserved", "FinaleRefitting" })
        {
            Set(bot, duty, true);
            Assert.True(Fit().Protected);
            Assert.Empty(Fit().TravelSeconds);
            Set(bot, duty, false);
        }
        world.ObserveServerTime(176_000, 10_000);
        Assert.Empty(Fit().TravelSeconds);
        world.Apply(new ExportStation { Station = home with { SideId = 1 } });
        Assert.Null(Fit().HomeStation);
    }

    [SkippableTheory, Trait("Tier", "Fast")]
    [InlineData("same")]
    [InlineData("guard")]
    [InlineData("empty")]
    [InlineData("replaced")]
    [InlineData("renewed")]
    [InlineData("epoch")]
    [InlineData("mission")]
    public void P1104b_Order_expiry_only_clears_the_original_accepted_assignment(string change)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var cs = Session(true);
        var bot = Bot(cs);
        DefenseNoSocketSender(bot);
        var manager = Manager(bot);
        Set(manager, "_raidEndCallbacks", new Dictionary<short, (ClientWorld, Action)>());
        var world = cs.World;
        world.Apply(new ResearchBriefing { Epoch = "A" });
        Set(bot, "LifeGeneration", world.MissionGeneration);
        Set(bot, "LifeSession", cs);
        bool guard = change == "guard";
        var accepted = new QueuedCommandState(guard ? OrderChange.CidDefend : OrderChange.CidAttack,
            IgcObjectType.Station, guard ? (short)10 : (short)91);
        var outgoing = new ResearchTactics.Order(1, accepted.CommandId, accepted.TargetType, accepted.TargetId);
        world.Apply(new ExportStation { Station = Station(10, 0, 1) });
        Set(bot, "OrderedCommand", accepted);
        var policy = new ResearchTactics();
        var fit = new SustainableRaid.Fit(new(17, [new(0, 250, 1, 1)]), new() { [91] = 0 }, null, false);
        ResearchTactics.Pilot[] crew = [new(guard ? (short)2 : (short)1, 1, default, false, false, Raid: fit),
            new(guard ? (short)1 : (short)2, 1, default, false, false, Raid: fit with { HomeStation = 10 })];
        var target = new ResearchTactics.Target(91, IgcObjectType.Station, 2, default, false,
            Profile: new(100, 100, 0, 0, 0, 0, [[1]]));
        var sight = new SustainableRaid.Observation(10, [91], false, _ => null, "opening");
        policy.Tick(10, true, crew, [target], raid: sight);
        var wing = policy.ActiveRaid;
        policy.Tick(11, true, crew, [target], raid: sight with { Closing = true });
        var roster = Array.CreateInstance(BotType, 1);
        roster.SetValue(bot, 0);
        ResearchTactics.Order[] replacements = change == "renewed" ? [outgoing] : [];
        Call("UpdateRaidOrders", manager, world, (short)0, "A", policy, wing, roster, replacements);
        Assert.Equal(change == "renewed", Get<object?>(bot, "ExpiredRaidOrder") is null);

        if (change == "empty")
        {
            Set(bot, "OrderedCommand", null);
            Assert.False((bool)Call("DriveCommanderOrder", manager, bot, 10d)!);
            Assert.Null(Get<object?>(bot, "ExpiredRaidOrder"));
            Set(bot, "OrderedCommand", accepted);
        }
        if (change == "replaced")
        {
            var inFlightExpiry = Get<object?>(bot, "ExpiredRaidOrder");
            world.Apply(new QueuedCommandUpdate { CommandId = accepted.CommandId, TargetId = accepted.TargetId, TargetType = accepted.TargetType });
            Call("HandleOrders", manager, bot, 10d);
            Assert.Null(Get<object?>(bot, "ExpiredRaidOrder"));
            // Research can finish publishing the old expiry after combat accepts the replacement.
            Set(bot, "ExpiredRaidOrder", inFlightExpiry);
        }
        if (change is "epoch" or "mission")
        {
            if (change == "mission") world.BeginNextMission();
            world.Apply(new ResearchBriefing { Epoch = change == "epoch" ? "B" : "A" });
            Call("RefreshLife", manager, bot, cs, 10d);
            Assert.Null(Get<object?>(bot, "ExpiredRaidOrder"));
            world.Apply(new ExportStation { Station = Station(10, 0, 1) });
            Set(bot, "OrderedCommand", accepted);
        }
        Call("DriveCommanderOrder", manager, bot, 11d);
        Assert.Equal(change is "same" or "guard" ? (QueuedCommandState?)null : accepted, Get<QueuedCommandState?>(bot, "OrderedCommand"));
        Assert.Null(Get<object?>(bot, "ExpiredRaidOrder"));
    }

    [SkippableFact, Trait("Tier", "Fast")]
    public void P1104b_Backfill_sides_share_a_manager_and_keep_their_sortie_across_roster_changes()
    {
        Require.Artwork();
        using var left = Session(true);
        using var right = Session(true);
        using var leftGuard = Session(true);
        using var rightGuard = Session(true);
        var core = Catalog.RepresentativeCore();
        object Pilot(Allegiance.ClientCore.Session.ConnectionSession cs, short id, short side, bool home)
        {
            var bot = Bot(cs, id);
            Set(bot, "Side", side);
            var w = cs.World;
            w.MissionCoreResolver = _ => core;
            w.Apply(new MissionDef { SzIGCStaticFile = "PCore001" });
            w.Apply(new JoinedMission { ShipId = id });
            w.Apply(new ResearchBriefing { TechMode = "research", Epoch = "A", SideId = side, Plan = new() { CoreVersion = core.Version } });
            DefenseMap(w);
            w.Apply(new ExportStation { Station = Station(100, home ? side : (short)(1 - side), 1) });
            w.Apply(new ExportStation { Station = Station(91, (short)(1 - side), 1) });
            w.Apply(new ExportShip { ShipId = id, SideId = side, HullId = 23 });
            w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(id, 0, default, 1, default,
                Orientation.Identity, default, [0, 0, 0], 1, 1, 1, 1, 1, 1, 1) });
            ExpandedPartData[] parts = [new(158, 2, 0, 240)];
            w.Apply(new BuyLoadoutAck { HullId = 23, Parts = parts, FLaunch = true, FBoughtEverything = true });
            Set(bot, "Granted", new BotManager.LoadoutGrant(23, parts));
            return bot;
        }
        var manager = Manager(Pilot(left, 1, 0, false), Pilot(right, 2, 1, false),
            Pilot(leftGuard, 3, 0, true), Pilot(rightGuard, 4, 1, true));
        Set(manager, "_researchTactics", new Dictionary<short, (string, ResearchTactics)>());
        Assert.Null(manager.OrdinaryRaidEpoch(left.World));
        manager.SustainableRaidEnabled = DriverOptions.Parse(["local", "1", "--backfill-seats", "5", "--sustainable-raid", "true"]).SustainableRaid;
        var target = new ResearchTactics.Target(91, IgcObjectType.Station, 1, default, false, Hull: 1, Shield: 1,
            Profile: new(1, 1, 0, 0, 0, 0, core.Constants.DamageConstants));
        var sight = new SustainableRaid.Observation(10, [91], false, _ => null, "opening");
        ResearchTactics Tick(short side, ClientWorld world, double time)
        {
            var epoch = manager.OrdinaryRaidEpoch(world);
            Assert.NotNull(epoch);
            var snapshot = (ITuple)Call("ResearchTacticalSnapshot", manager, side, epoch)!;
            var policy = manager.TacticsForSide(side, epoch);
            policy.Tick(time, true, (ResearchTactics.Pilot[])snapshot[0]!, [target], raid: sight with { At = time });
            return policy;
        }
        var a = Tick(0, left.World, 10);
        Assert.Equal(new short[] { 1 }, a.ActiveRaid!.Members);
        a.Tick(11, true, [], [target], raid: sight with { Destroyer = _ => 1 });
        Assert.Contains(a.TakeRaidEvents(), e => e.Detail.Contains("result=credited-kill"));
        var b = Tick(1, right.World, 12);
        Assert.Equal(new short[] { 2 }, b.ActiveRaid!.Members);
        // An incoming combat seat on the same side cannot reset its opportunity.
        var roster = Get<System.Collections.IList>(manager, "_bots");
        roster[0] = Pilot(left, 17, 0, false);
        Assert.Empty(Tick(0, left.World, 13).TakeRaidEvents());
        foreach (var w in new[] { left.World, leftGuard.World })
            w.Apply(new ResearchBriefing { Epoch = "B", TechMode = "research", Plan = new() { CoreVersion = core.Version } });
        Assert.NotNull(Tick(0, left.World, 14).ActiveRaid);
        Assert.Same(b, Tick(1, right.World, 14));
        Assert.Single(b.TakeRaidEvents(), e => e.Stage == "launch");
        left.World.Apply(new ResearchBriefing { TechMode = "unlocked", Epoch = "B" });
        Assert.Null(manager.OrdinaryRaidEpoch(left.World));
        left.World.Apply(new ResearchBriefing { TechMode = "research", Epoch = "B" });
        left.World.Apply(new MissionDef { Goals = MissionGoals.Preset("conquest") });
        Assert.Null(manager.OrdinaryRaidEpoch(left.World));
    }

    [SkippableTheory, Trait("Tier", "Fast")]
    [InlineData("docked", false)]
    [InlineData("reserved", false)]
    [InlineData("refit", false)]
    [InlineData("turnback", true)]
    [InlineData("pod", true)]
    [InlineData("killed", true)]
    public void P1104b_Tactical_snapshot_keeps_the_reason_a_pilot_left(string reason, bool lost)
    {
        Require.Artwork();
        using var cs = Session(true);
        var bot = Bot(cs);
        cs.World.Apply(new ResearchBriefing { Epoch = "A", TechMode = "research" });
        if (reason == "docked") cs.World.Apply(new DockedNotify { StationId = 10 });
        if (reason == "reserved") Set(bot, "FinaleBomberReserved", true);
        if (reason == "refit") Set(bot, "FinaleRefitting", true);
        if (reason == "turnback") Set(bot, "KillRecovering", true);
        if (reason == "pod") Set(bot, "IsPod", true);
        if (reason == "killed") cs.World.Apply(new KillShip { ShipId = 1, BDeathCredit = true });
        var snapshot = (ITuple)Call("ResearchTacticalSnapshot", Manager(bot), (short)0, "A")!;
        Assert.Empty((ResearchTactics.Pilot[])snapshot[0]!);
        var members = (Dictionary<short, SustainableRaid.MemberObservation>)snapshot[5]!;
        Assert.Equal(lost ? SustainableRaid.MemberState.Lost : SustainableRaid.MemberState.Withdrawn, members[1].State);
    }
}
