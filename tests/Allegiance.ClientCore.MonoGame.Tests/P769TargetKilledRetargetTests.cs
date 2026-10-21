using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P769/SIM-0197 — pins the <c>WinTrekClient::TargetKilled</c> retarget rule
/// (<c>WinTrek/trekigc.cpp:2934-2974</c>) through the pure static decision
/// (<see cref="Game1.DecideTargetKilledRetarget"/>) that
/// <c>Game1.OnTargetKilled</c> now calls:
/// <list type="bullet">
/// <item>act only when the killed model is the c_cmdCurrent target (:2936-2938);</item>
/// <item>the source ship (the parent for a gunner) must be in a cluster (:2940-2941);</item>
/// <item>enemy-ship mask: rescue-capable first, then miner/builder, then any
/// (:2944-2960) — native FindTarget with abm = c_habmRescue and the two
/// fallbacks;</item>
/// <item>never re-pick the dead model (:2962-2963);</item>
/// <item>re-engage the autopilot only when the c_cmdPlan target was the killed
/// model (:2968-2969).</item>
/// </list>
/// The selector delegate is the port's FindTarget stand-in:
/// <see cref="Game1.FilterCandidates"/> (the same pure filter the live
/// <c>SelectFilteredTarget</c> runs), nearest-first — so "nearer but
/// non-matching" and role-bit semantics are the production ones.
/// </summary>
public sealed class P769TargetKilledRetargetTests
{
    private static readonly SimVec Origin = new(0, 0, 0);

    private static ClientWorld.TargetKilledModel Kill(short id) =>
        new(IgcObjectType.Ship, id);

    private static QueuedCommandState Current(short id) =>
        new(OrderChange.CidAttack, IgcObjectType.Ship, id);

    private static Game1.CandidateObject Enemy(short id, short role, float x) =>
        new(new SimVec(x, 0, 0), Game1.TargetKind.Ship, id, $"enemy{id}", 1, role);

    private static Game1.CandidateObject Friendly(short id, short role, float x) =>
        new(new SimVec(x, 0, 0), Game1.TargetKind.Ship, id, $"friend{id}", 0, role);

    /// <summary>The FindTarget stand-in: run the production pure filter for the
    /// spec (my side 0, self id 9) and take the nearest match, exactly the
    /// shape Game1.OnTargetKilled feeds the decision.</summary>
    private static Func<Game1.TargetFilterSpec, Game1.AutopilotTarget?> Selector(
        IReadOnlyList<Game1.CandidateObject> objs)
        => spec
        =>
        {
            var matches = Game1.FilterCandidates(objs, 0, 9, Origin, spec);
            return matches.Count > 0 ? matches[0] : null;
        };

    private static Game1.TargetKilledDecision Decide(
        QueuedCommandState? current,
        bool enemy,
        Func<Game1.TargetFilterSpec, Game1.AutopilotTarget?> select,
        bool inCluster = true,
        bool engaged = false,
        Game1.AutopilotTarget? plan = null,
        short victimId = 11)
        => Game1.DecideTargetKilledRetarget(current, Kill(victimId), enemy, inCluster, select, engaged, plan);

    // ── gate: not the c_cmdCurrent target → nothing ──────────────────────

    [Fact]
    public void Victim_not_the_current_target_is_a_noop_not_even_a_clear()
    {
        var objs = new List<Game1.CandidateObject> { Enemy(12, 0, 5) };
        // c_cmdCurrent points at ship 7; the victim is 11 → native returns
        // before FindTarget/SetTarget (trekigc.cpp:2936-2938).
        var d = Decide(Current(7), enemy: true, Selector(objs));
        Assert.False(d.Acted);
        Assert.Null(d.Pick);
        Assert.False(d.Reengage);
    }

    [Fact]
    public void No_current_command_at_all_is_a_noop()
    {
        var objs = new List<Game1.CandidateObject> { Enemy(12, 0, 5) };
        var d = Decide(null, enemy: true, Selector(objs));
        Assert.False(d.Acted);
    }

    // ── gate: source ship not in a cluster → nothing ─────────────────────

    [Fact]
    public void Source_not_in_a_cluster_is_a_noop()
    {
        var objs = new List<Game1.CandidateObject> { Enemy(12, 0, 5) };
        var d = Decide(Current(11), enemy: true, Selector(objs), inCluster: false);
        Assert.False(d.Acted);
    }

    /// <summary>The gunner mapping: the SOURCE is the PARENT ship (native
    /// <c>GetSourceShip()</c>, trekigc.cpp:2940) — a boarded pod whose own
    /// sector is unset still retargets when its carrier is in a cluster, and
    /// NOT when the carrier is out of the cluster even if the pod itself
    /// reports one. Built through the real wire-apply path.</summary>
    [Fact]
    public void Gunner_uses_the_parent_carrier_as_the_source()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = 30, SideId = 0, CharacterName = "Carrier" });
        world.Apply(new ShipStatusMessage { ShipId = 30, SectorId = 1, ParentId = -1, StationId = -1 });
        world.Apply(new ExportShip { ShipId = 31, SideId = 0, CharacterName = "Pod" });
        world.Apply(new ShipStatusMessage { ShipId = 31, SectorId = -1, ParentId = 30, StationId = -1 });
        var pod = world.Ships[31];
        Assert.Equal(30, pod.ParentId);   // boarded: the parent is the carrier

        // The pod itself has no cluster (SectorId -1); the CARRIER is in one —
        // the gate must consult the parent (trekigc.cpp:2940-2941,
        // GetSourceShip()->GetCluster()).
        Assert.True(Game1.TargetKilledSourceInCluster(world, pod));

        // Now the carrier docks (SectorId -1) while the pod still reports one —
        // the SOURCE (the parent) is what gates, so the gate closes.
        world.Apply(new ShipStatusMessage { ShipId = 30, SectorId = -1, ParentId = -1, StationId = 5 });
        Assert.False(Game1.TargetKilledSourceInCluster(world, pod));

        // Control: a plain pilot (ParentId -1) gates on its own cluster.
        world.Apply(new ExportShip { ShipId = 9, SideId = 0, CharacterName = "Self" });
        world.Apply(new ShipStatusMessage { ShipId = 9, SectorId = 1, ParentId = -1, StationId = -1 });
        Assert.True(Game1.TargetKilledSourceInCluster(world, world.Ships[9]));
    }

    // ── enemy mask: rescue > miner/builder > any ─────────────────────────

    [Fact]
    public void Enemy_rescue_capable_wins_over_a_nearer_plain_ship()
    {
        // Ship 12 is NEARER but has no ability bits; ship 13 is a rescue-capable
        // hull farther out. Native's first FindTarget leg (abm = c_habmRescue,
        // trekigc.cpp:2944-2947) must win despite the distance.
        var objs = new List<Game1.CandidateObject>
        {
            Enemy(12, 0, 5),
            Enemy(13, DataHullTypeIGC.HabmRescue, 50),
        };
        var d = Decide(Current(11), enemy: true, Selector(objs));
        Assert.True(d.Acted);
        Assert.NotNull(d.Pick);
        Assert.Equal(13, Assert.NotNull(d.Pick).Id);
    }

    [Fact]
    public void Enemy_without_rescue_falls_back_to_miner_or_builder()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Enemy(12, 0, 5),
            Enemy(13, DataHullTypeIGC.HabmMiner, 50),
            Enemy(14, DataHullTypeIGC.HabmBuilder, 60),
        };
        var d = Decide(Current(11), enemy: true, Selector(objs));
        Assert.Equal(13, Assert.NotNull(d.Pick).Id);   // nearest of the miner/builder matches

        var objs2 = new List<Game1.CandidateObject>
        {
            Enemy(12, 0, 5),
            Enemy(14, DataHullTypeIGC.HabmBuilder, 60),
        };
        var d2 = Decide(Current(11), enemy: true, Selector(objs2));
        Assert.Equal(14, Assert.NotNull(d2.Pick).Id);
    }

    [Fact]
    public void Enemy_with_only_plain_ships_takes_the_nearest_any()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Enemy(12, 0, 5),
            Enemy(13, 0, 9),
        };
        var d = Decide(Current(11), enemy: true, Selector(objs));
        Assert.Equal(12, Assert.NotNull(d.Pick).Id);
    }

    // ── friendly victim → friendly mask ──────────────────────────────────

    [Fact]
    public void Friendly_victim_targets_the_nearest_friendly_not_an_enemy()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Enemy(20, 0, 3),                       // nearer — but wrong mask
            Friendly(21, DataHullTypeIGC.HabmRescue, 40),
            Friendly(22, 0, 70),
        };
        var d = Decide(Current(11), enemy: false, Selector(objs));
        Assert.True(d.Acted);
        // The friendly mask is a single abm = 0 pass (trekigc.cpp:2961): no
        // rescue priority, plain nearest friendly.
        Assert.Equal(21, Assert.NotNull(d.Pick).Id);
    }

    // ── the dead model is never re-picked ────────────────────────────────

    [Fact]
    public void The_dead_model_is_never_re_picked_even_when_nearest()
    {
        // The victim itself is the nearest rescue-capable match: native nulls
        // the pick outright (trekigc.cpp:2962-2963, `pmodelTarget == pmodel →
        // NULL`) WITHOUT retrying the cheaper masks — SetTarget(NULL) clears
        // the slot even though ship 12 also exists.
        var objs = new List<Game1.CandidateObject>
        {
            Enemy(11, DataHullTypeIGC.HabmRescue, 1),   // the victim itself, nearest + matching
            Enemy(12, 0, 30),
        };
        var d = Decide(Current(11), enemy: true, Selector(objs));
        Assert.True(d.Acted);          // the slot is committed (cleared)…
        Assert.Null(d.Pick);           // …but never to the dead model itself
        Assert.False(d.Reengage);

        // Control: with a non-matching victim mask the nearest OTHER candidate
        // is picked normally.
        var others = new List<Game1.CandidateObject>
        {
            Enemy(11, 0, 1),                            // victim, plain hull
            Enemy(12, DataHullTypeIGC.HabmRescue, 30),  // rescue-capable wins the leg
        };
        var d2 = Decide(Current(11), enemy: true, Selector(others));
        Assert.True(d2.Acted);
        Assert.Equal(12, Assert.NotNull(d2.Pick).Id);

        // The victim being the ONLY candidate → SetTarget(NULL): the slot is
        // cleared (Acted) but nothing is picked and nothing re-engages.
        var only = new List<Game1.CandidateObject> { Enemy(11, DataHullTypeIGC.HabmRescue, 1) };
        var d3 = Decide(Current(11), enemy: true, Selector(only));
        Assert.True(d3.Acted);
        Assert.Null(d3.Pick);
        Assert.False(d3.Reengage);
    }

    // ── autopilot re-engagement ──────────────────────────────────────────

    [Fact]
    public void Autopilot_reengages_only_when_the_plan_target_was_the_victim()
    {
        var objs = new List<Game1.CandidateObject> { Enemy(12, 0, 5) };

        // Plan target was the victim → SetAutoPilot(true) toward the new pick
        // (trekigc.cpp:2968-2969).
        var engagedOnVictim = Decide(Current(11), enemy: true, Selector(objs),
            engaged: true, plan: new Game1.AutopilotTarget(Game1.TargetKind.Ship, 11, "victim"));
        Assert.True(engagedOnVictim.Acted);
        Assert.NotNull(engagedOnVictim.Pick);
        Assert.True(engagedOnVictim.Reengage);

        // Autopilot on but the plan pointed elsewhere → no re-engage.
        var engagedOther = Decide(Current(11), enemy: true, Selector(objs),
            engaged: true, plan: new Game1.AutopilotTarget(Game1.TargetKind.Ship, 77, "other"));
        Assert.False(engagedOther.Reengage);

        // Plan was the victim but the autopilot is off → no re-engage.
        var disengaged = Decide(Current(11), enemy: true, Selector(objs),
            engaged: false, plan: new Game1.AutopilotTarget(Game1.TargetKind.Ship, 11, "victim"));
        Assert.False(disengaged.Reengage);

        // Nothing matched (pick null) → nothing to re-engage even on the victim
        // plan (native SetAutoPilot(true) runs after SetTarget; with a NULL
        // target the flight layer's own target-lost disengage owns the fallback).
        var none = new List<Game1.CandidateObject> { Enemy(11, 0, 1) };
        var cleared = Decide(Current(11), enemy: true, Selector(none),
            engaged: true, plan: new Game1.AutopilotTarget(Game1.TargetKind.Ship, 11, "victim"));
        Assert.Null(cleared.Pick);
        Assert.False(cleared.Reengage);
    }
}
