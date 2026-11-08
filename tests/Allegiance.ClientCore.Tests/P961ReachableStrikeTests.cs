using System.Collections;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P961ReachableStrikeTests
{
    private static StrikeVolley.Loadout Ab2() => new(23, [new(8, 500, 2, 4)]);

    [SkippableFact]
    public void Crowded_idle_garrison_and_outpost_use_only_threats_to_the_path()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var staging = new Vector3(3500, 0, 0);
        var release = new Vector3(2000, 0, 0);
        var crowd = Enumerable.Range(0, 65).Select(i => new Vector3(15000, i * 10, 0)).ToArray();
        int idle = crowd.Count(p => FinaleAssembly.ThreatensPath(p, staging, release, 2500, false));
        Assert.Equal(0, idle);
        Assert.True(FinaleAssembly.ThreatensPath(new(2700, 2400, 0), staging, release, 2500, false));
        Assert.False(FinaleAssembly.ThreatensPath(new(2700, 2501, 0), staging, release, 2500, false));
        Assert.True(FinaleAssembly.ThreatensPath(crowd[0], staging, release, 2500, true));
        Assert.False(FinaleAssembly.ThreatensPath(staging, staging, release, 2500, false, canAttack: false));
        Assert.True(FinaleAssembly.ThreatensPath(staging, staging, release, 2500, true, canAttack: false));

        var garrison = StrikeVolley.Profile(core, 315)!.Value;
        Assert.Equal(20000, garrison.MaxArmor); Assert.Equal(10000, garrison.MaxShield);
        // AB2: 5000 damage to each pool, two rounds per hull, real regen ->
        // four hulls kill, five with P801's spare; 65 idle hulls add zero.
        var quiet = FinaleAssembly.Compute(garrison, [Ab2()], 12, 1, 0, idle, 1);
        Assert.Equal(4, quiet.Planner.KillBombers); Assert.Equal(5, quiet.Required);
        Assert.Equal(0, quiet.ThreatExtra); Assert.Equal(4, quiet.MinimumUseful);
        var engaging = FinaleAssembly.Compute(garrison, [Ab2()], 12, 1, 0, 3, 1);
        Assert.Equal(8, engaging.Required); Assert.Equal(3, engaging.ThreatExtra);
        var observedLoss = FinaleAssembly.Compute(garrison, [Ab2()], 12, 1, 4, 1, 1);
        Assert.Equal(9, observedLoss.Required);
        // Landing factor 1/.5 = 2; minimum useful = ceil(4/.5) = 8;
        // ideal plus spare = ceil(5/.5) + max(4,1) = 14, capped at 12.
        var half = FinaleAssembly.Compute(garrison, [Ab2()], 12, 1, 4, 1, .5);
        Assert.Equal(12, half.Required); Assert.Equal(8, half.MinimumUseful);

        var outpost = StrikeVolley.Profile(core, 2)!.Value;
        Assert.Equal(6666, outpost.MaxArmor); Assert.Equal(6666, outpost.MaxShield);
        var small = FinaleAssembly.Compute(outpost, [Ab2()], 12, 1, 0, idle, 1);
        Assert.Equal(1, small.Planner.KillBombers); Assert.Equal(2, small.Required);
        Assert.Equal(3, FinaleAssembly.Compute(outpost, [Ab2()], 12, 1, 0, 1, 1).Required);
    }

    [SkippableFact]
    public void Reachable_cap_never_mislabels_two_hulls_as_a_garrison_kill()
    {
        Require.Artwork();
        var profile = StrikeVolley.Profile(Catalog.RepresentativeCore(), 315)!.Value;
        var shortWing = FinaleAssembly.Compute(profile, [Ab2()], 2, 1, 0, 65, 1);
        Assert.Equal(2, shortWing.Required); Assert.Equal(4, shortWing.Planner.KillBombers);
        Assert.False(shortWing.ReachableKill);
        Assert.Null(FinaleAssembly.CommitReason(2, shortWing, 0, 0, 900, 901, 45));
        var feasible = FinaleAssembly.Compute(profile, [Ab2()], 6, 1, 0, 3, 1);
        Assert.Equal(6, feasible.Required); Assert.True(feasible.ReachableKill);
        Assert.Equal(0, FinaleAssembly.Compute(profile, [Ab2()], 0, 1, 0, 0, 1).Required);
        var ineffective = new StrikeVolley.Loadout(23, [new(8, .001f, 1, 4)]);
        Assert.False(FinaleAssembly.Compute(profile, [ineffective], 18, 1, 0, 0, 1).ReachableKill);
        Assert.False(FinaleAssembly.Compute(profile, [], 18, 1, 0, 0, 1).ReachableKill);
        var heavy = new StrikeVolley.Loadout(889, [new(8, 5000, 5, 4)]);
        var defendedGarrison = FinaleAssembly.Compute(profile, [heavy], 1, 1, 0, 8, 1, garrison: true);
        Assert.Equal(1, defendedGarrison.Planner.KillBombers);
        Assert.Equal(2, defendedGarrison.MinimumUseful);
        Assert.False(defendedGarrison.ReachableKill); // no lone bomber at a defended garrison
    }

    [Theory]
    [InlineData(6, 110, 1000, "massed")]
    [InlineData(5, 144, 1000, null)]
    [InlineData(5, 145, 1000, "plateau")]
    [InlineData(4, 145, 1000, null)]
    [InlineData(3, 500, 1000, null)]
    [InlineData(3, 965, 1000, null)]
    [InlineData(4, 960, 1000, "deadline-forced")]
    [InlineData(0, 960, 1000, null)]
    public void Commit_table_never_sends_a_provably_useless_volley(int assembled, double now, double deadline, string? expected)
    {
        var planner = new StrikeVolley.Requirement(8, 4, 7, 20, 50, 5000, 5000, 1);
        var demand = new FinaleAssembly.Demand(6, 4, 6, 3, planner);
        Assert.Equal(expected, FinaleAssembly.CommitReason(assembled, demand, 80, 100, now, deadline, 45));
        if (expected == "plateau")
        {
            // A new high-water mark restarts the justified waiting interval.
            Assert.Null(FinaleAssembly.CommitReason(assembled, demand, 80, now - 1, now, deadline, 45));
            Assert.Null(FinaleAssembly.CommitReason(assembled, demand, 80, 100, now, deadline, 0));
        }
    }

    [Fact]
    public void Arrival_requires_a_live_armed_reserved_hull_and_time_for_its_volley()
    {
        Assert.True(FinaleAssembly.CanArrive(true, true, true, false, 64, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(true, true, true, false, 66, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(false, true, true, false, 0, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(true, false, true, false, 0, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(true, true, false, false, 0, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(true, true, true, true, 0, 10, 100));
        Assert.False(FinaleAssembly.CanArrive(true, true, true, false, double.PositiveInfinity, 10, 100));
        Assert.True(FinaleAssembly.CanArrive(true, true, true, false, 600, 10, double.PositiveInfinity));
    }

    [Fact]
    public void Preparation_has_one_trip_and_legal_fallbacks_never_upgrade_via_an_extra_trip()
    {
        Assert.True(FinaleAssembly.ShouldRefit(false, false, false));
        Assert.False(FinaleAssembly.ShouldRefit(false, false, true));
        Assert.False(FinaleAssembly.ShouldRefit(true, false, false));
        Assert.False(FinaleAssembly.ShouldRefit(true, false, true));
        // A genuinely empty magazine can use the existing resupply path.
        Assert.True(FinaleAssembly.ShouldRefit(true, true, true));
    }

    [Fact]
    public void New_strategy_is_opt_in_and_settings_are_bounded()
    {
        var settings = new ShowcaseFinale.Settings();
        Assert.False(settings.ReachableStrike); Assert.Equal(45, settings.PlateauSeconds);
        Assert.Equal(2500, settings.EngagementRange);
        new ShowcaseFinale.Settings(ReachableStrike: true, PlateauSeconds: 0).Validate();
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(PlateauSeconds: -1).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(PlateauSeconds: double.NaN).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(EngagementRange: 0).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(EngagementRange: float.NaN).Validate());
    }
}

public sealed partial class BotFixRegressionTests
{
    private static Array P961BotArray(object[] bots)
    {
        var typed = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) typed.SetValue(bots[i], i);
        return typed;
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P961_Unreachable_garrison_retargets_an_observed_killable_outpost_without_rearming_deadline(bool currentArmed)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 2, ReachableStrike: true), perSide: 2);
        rig.Mature();
        var run = Get<object>(rig.Bots[0], "Finale");
        var plan = Get<IDictionary>(run, "Plans")[(short)0]!;
        Set(plan, "TargetTypeId", (short)315);
        double deadline = rig.Started + 2700;
        Set(plan, "DeadlineAt", deadline);
        var armed = Array.CreateInstance(BotType, 2);
        for (int i = 0; i < 2; i++)
        {
            var bot = rig.Bots[i]; var cs = rig.Sessions[i];
            IReadOnlyList<ExpandedPartData> parts = [new(158, 2, 0, ExpandedPartData.FullyMounted)];
            cs.World.Apply(new ResearchBriefing { TechMode = "research" });
            cs.World.Apply(new ExportShip { ShipId = (short)(i + 1), HullId = 23, SideId = 0 });
            cs.World.Apply(new ExportStation { Station = rig.Stations[1] with { StationTypeId = 315 } });
            cs.World.Apply(new ExportStation { Station = Station(102, 1, 1) with { StationTypeId = 2 } });
            Set(bot, "HullId", (short)23); Set(bot, "Parts", parts);
            Set(bot, "Granted", new BotManager.LoadoutGrant(23, parts));
            Set(bot, "FinaleBomberReserved", true); Set(bot, "FinaleBomberActive", currentArmed);
            Set(bot, "FinaleGrantVerified", currentArmed); Set(bot, "FinalePreparationRefitUsed", true);
            armed.SetValue(bot, i);
        }
        if (!currentArmed) armed = Array.CreateInstance(BotType, 0);
        Assert.True((bool)Call("TryRetargetReachableFinale", rig.Manager, run, plan, (short)0, armed, rig.Started + 1801)!);
        Assert.Equal((short)102, Get<short>(plan, "TargetId"));
        Assert.Equal(deadline, Get<double>(plan, "DeadlineAt"));
        foreach (var bot in rig.Bots.Take(2)) Assert.True(Get<bool>(bot, "FinalePreparationRefitUsed"));
        Assert.True(Get<FinaleAssembly.Demand>(plan, "Demand").ReachableKill);
        // Retarget cooldown prevents flipping between stations every coordinator pass.
        Assert.False((bool)Call("TryRetargetReachableFinale", rig.Manager, run, plan, (short)0, armed, rig.Started + 1802)!);
    }

    [SkippableFact]
    public void P961_Active_planner_and_delivery_feedback_use_the_reachable_subset()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 2, ReachableStrike: true), perSide: 6);
        rig.Mature();
        var run = Get<object>(rig.Bots[0], "Finale");
        var plan = Get<IDictionary>(run, "Plans")[(short)0]!;
        Set(plan, "TargetTypeId", (short)315);
        for (int i = 0; i < 2; i++)
        {
            var bot = rig.Bots[i]; var cs = rig.Sessions[i];
            short hull = i == 0 ? (short)889 : (short)23;
            IReadOnlyList<ExpandedPartData> parts = [new(i == 0 ? (short)601 : (short)157,
                i == 0 ? (short)5 : (short)2, 0, ExpandedPartData.FullyMounted)];
            cs.World.Apply(new ExportShip { ShipId = (short)(i + 1), SideId = 0, HullId = hull });
            cs.World.Apply(new SetCluster { SectorId = i == 0 ? (short)4 : (short)99, Ship = Pose((short)(i + 1), 3500) });
            Set(bot, "HullId", hull); Set(bot, "Parts", parts); Set(bot, "Granted", new BotManager.LoadoutGrant(hull, parts));
            Set(bot, "FinaleBomberReserved", true); Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", true);
        }
        Call("FinaleStrikeMassTick", rig.Manager, run, P961BotArray(rig.Bots), rig.Started + 1801);
        var demand = Get<FinaleAssembly.Demand>(plan, "Demand");
        Assert.Equal(1, demand.Reachable);
        Assert.Equal(2, demand.Planner.KillBombers); // reachable 5-round heavy; unreachable 2-round AB1 stealth needs five
        Assert.Equal(7, demand.Planner.ShotsToKill); // real AB3: 5000 damage/pool; unreachable AB1 yields 4000 and nine shots
        Assert.Equal(5000f, demand.Planner.ShieldDamagePerRound);
        Assert.Equal(5000f, demand.Planner.ArmorDamagePerRound);
        Assert.Equal(demand.Planner.KillBombers, Get<int>(plan, "PlannerKill"));
        Assert.Equal(demand.Planner.ShotsToKill, Get<int>(plan, "ShotsNeeded"));
        Assert.Equal(demand.Planner.ShieldDamagePerRound, Get<float>(plan, "ShieldDamagePerRound"));
        Assert.Equal(demand.Planner.ArmorDamagePerRound, Get<float>(plan, "ArmorDamagePerRound"));
    }

    [SkippableFact]
    public void P961_Prearm_preserves_the_legal_magazine_without_another_purchase()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, PrearmMinutes: 2));
        var bot = rig.Bots[0]; var cs = rig.Sessions[0];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = 23 });
        IReadOnlyList<ExpandedPartData> parts = [new(158, 2, 0, ExpandedPartData.FullyMounted)];
        Set(bot, "HullId", (short)23); Set(bot, "Parts", parts);
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, parts));
        rig.Tick(1680); // 28 minutes: preparation, not the 30-minute arm.
        Assert.True(Get<bool>(bot, "FinaleBomberReserved"));
        Assert.True((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1681)!);
        Assert.False(Get<bool>(bot, "FinaleBomberActive"));
        Assert.False(Get<bool>(bot, "FinaleRefitting")); Assert.Equal(0, Get<int>(bot, "RelaunchAttempts"));
        Assert.Equal(0, cs.World.OwnMissilesFired);
        // After the existing arm the same inventory flies; it is not recalled.
        rig.Mature();
        Assert.False((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.True(Get<bool>(bot, "FinaleBomberActive"));
        Assert.Equal(0, Get<int>(bot, "RelaunchAttempts"));
    }

    [SkippableFact]
    public void P961_Wave_inventory_uses_granted_rounds_minus_confirmed_launches()
    {
        Require.Artwork(); using var cs = Session(true);
        var bot = Bot(cs); var core = Catalog.RepresentativeCore();
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = 23 });
        Set(bot, "HullId", (short)23);
        // The requested six-round magazine was only granted TWO rounds.
        Set(bot, "Parts", (IReadOnlyList<ExpandedPartData>)[new(158, 6, 0, ExpandedPartData.FullyMounted)]);
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, [new(158, 2, 0, ExpandedPartData.FullyMounted)]));
        StrikeVolley.Loadout? Inventory() => (StrikeVolley.Loadout?)Call("FinaleGrantedStrikeLoadout", null, bot);
        var profile = StrikeVolley.Profile(core, 315)!.Value;
        Assert.Equal(2, StrikeVolley.RoundsInWindow(Inventory()!, profile));
        for (short i = 0; i < 2; i++) cs.World.Apply(new FireMissile { LauncherId = 1,
            Entries = [new(Vector3.Zero, new(0, 0, -1), Vector3.Zero, (short)(900 + i))] });
        Assert.Null(Inventory());
        // A normal purchase ACK establishes a new inventory epoch, not a grant.
        Set(bot, "FinaleMissileLaunchBase", cs.World.OwnMissilesFired);
        Assert.Equal(2, StrikeVolley.RoundsInWindow(Inventory()!, profile));
    }

    [SkippableFact]
    public void P961_Preparation_latch_survives_death_and_clears_for_the_next_mission()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        var bot = rig.Bots[0];
        Set(bot, "FinalePreparationRefitUsed", true);
        Call("ResetNewLife", null, bot, rig.Started + 1, true);
        Assert.True(Get<bool>(bot, "FinalePreparationRefitUsed"));
        Call("RestoreFinaleForNextMission", null, bot, rig.Started + 2, null);
        Assert.False(Get<bool>(bot, "FinalePreparationRefitUsed"));
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P961_Committed_bomber_and_escort_keep_the_plan_destination(bool escort)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Bombers: 1));
        rig.Mature();
        var bot = rig.Bots[0];
        var run = Get<object>(bot, "Finale");
        var plans = Get<IDictionary>(run, "Plans"); var plan = plans[(short)0]!;
        rig.Sessions[0].World.Apply(new ResearchBriefing { TechMode = "research" });
        Set(bot, "FinaleEscort", escort); Set(bot, "Role", escort ? "fighter" : "bomber");
        Set(bot, "TargetSector", (short?)999); Set(bot, "IsExplorer", true); Set(bot, "UseStaging", true);
        Call("OrderFinaleWingBot", null, bot, run, plan, rig.Started + 1801);
        Assert.Equal(Get<short>(plan, "Sector"), Get<short?>(bot, "TargetSector"));
        Assert.Equal(escort ? "fighter" : "bomber", Get<string>(bot, "Role"));
        Assert.False(Get<bool>(bot, "IsExplorer")); Assert.False(Get<bool>(bot, "UseStaging"));
        Assert.True((bool)Call("HasFinaleDestination", null, bot)!);
        Assert.False(FinaleAssembly.OwnsDestination(true, false, false, true));
        Assert.False(FinaleAssembly.OwnsDestination(true, false, true, false));
    }
}
