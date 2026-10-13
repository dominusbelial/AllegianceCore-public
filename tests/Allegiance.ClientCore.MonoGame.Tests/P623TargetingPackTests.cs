using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P623 — the parity-wave-1 TARGETING PACK: class-filtered target cycling
/// (enemy/friendly bomber/fighter/miner/constructor/lifepod/major-base),
/// nearest/previous step semantics, and TK_TargetHostile (target the thing
/// that did the most damage to you recently). These tests pin the faithful
/// core against native's <c>TrekFindTarget</c> (WinTrek.cpp:8957-9030 →
/// <c>FindTarget</c> common.cpp:470) and <c>FindableModel</c>
/// (common.cpp:254-353):
/// <list type="bullet">
/// <item><see cref="Game1.ClassRoleBits"/> maps each class to native's abm
/// argument (Bomber→c_habmThreatToStation, Fighter→c_habmFighter, Miner→
/// c_habmMiner, Constructor→c_habmBuilder, Lifepod→c_habmLifepod —
/// WinTrek.cpp:10162-10331).</item>
/// <item><see cref="Game1.ClassFilterSpec"/> is a ship filter with that role
/// bit; <see cref="Game1.FilterCandidates"/>/<see cref="Game1.MatchesFilter"/>
/// only match same-class enemy/friendly ships (the <c>FindableModel</c>
/// ability-bit test, common.cpp:291-321).</item>
/// <item><see cref="Game1.StepCycle"/> implements native's nearest/next/prev
/// triad: Nearest = head, Next = advance one (wrap), Previous = retreat one
/// (wrap), any direction falls back to nearest with no current target
/// (TrekFindTarget c_ttNearest/c_ttPrevious, WinTrek.cpp:8963-8969;
/// FindTarget walk + wrap, common.cpp:505-510, 611-614).</item>
/// <item><see cref="Game1.MajorBaseSpec"/> = stations flagged c_sabmFlag,
/// any sector (WinTrek.cpp:10202-10206 / 10266-10270).</item>
/// <item><see cref="Game1.HostileDamageTracker"/> reproduces native's 40 s
/// rolling damage window (8 × 5 s slots, igc.h:4926-4928) and severity ranking
/// for TK_TargetHostile (WinTrek.cpp:10101-10124).</item>
/// </list>
/// </summary>
public class P623TargetingPackTests
{
    private static Game1.CandidateObject Ship(short id, short? side, short role, SimVec pos) =>
        new(pos, Game1.TargetKind.Ship, id, $"ship{id}", side, role);

    private static Game1.AutopilotTarget T(short id) => new(Game1.TargetKind.Ship, id, $"ship{id}");

    // ── class → ability-bit mapping (native abm argument per class) ─────────

    [Fact]
    public void Class_role_bit_matches_native_abm()
    {
        // Each class maps to native's abm argument (WinTrek.cpp:10162-10331):
        Assert.Equal(DataHullTypeIGC.HabmThreatToStation, Game1.ClassRoleBits(Game1.TargetClass.Bomber));
        Assert.Equal(DataHullTypeIGC.HabmFighter, Game1.ClassRoleBits(Game1.TargetClass.Fighter));
        Assert.Equal(DataHullTypeIGC.HabmMiner, Game1.ClassRoleBits(Game1.TargetClass.Miner));
        Assert.Equal(DataHullTypeIGC.HabmBuilder, Game1.ClassRoleBits(Game1.TargetClass.Constructor));
        Assert.Equal(DataHullTypeIGC.HabmLifepod, Game1.ClassRoleBits(Game1.TargetClass.Lifepod));
    }

    /// <summary>⭐ A bomber filter is a SHIP filter carrying the ThreatToStation
    /// role bit, relation enemy — native c_ttShip | c_habmThreatToStation
    /// (WinTrek.cpp:10166).</summary>
    [Fact]
    public void Bomber_class_spec_is_enemy_ship_threat_to_station()
    {
        var spec = Game1.ClassFilterSpec(Game1.TargetRelation.Enemy, Game1.TargetClass.Bomber);
        Assert.Equal(Game1.TargetRelation.Enemy, spec.Relation);
        Assert.Equal(DataHullTypeIGC.HabmThreatToStation, spec.RoleBitmask);
        Assert.False(spec.StationOnly);
        Assert.False(spec.MajorBaseOnly);
    }

    /// <summary>⭐ FilterCandidates with an enemy-bomber spec keeps ONLY enemy
    /// ships whose hull has the ThreatToStation ability bit, sorted nearest-first
    /// (FindableModel ability test, common.cpp:291-321).</summary>
    [Fact]
    public void Enemy_bomber_filter_matches_only_enemy_bombers()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 2, DataHullTypeIGC.HabmThreatToStation, new SimVec(0, 0, 10)), // enemy bomber
            Ship(2, 2, DataHullTypeIGC.HabmFighter, new SimVec(0, 0, 5)),          // enemy fighter — NO
            Ship(3, 1, DataHullTypeIGC.HabmThreatToStation, new SimVec(0, 0, 8)),  // friendly bomber — NO
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Game1.ClassFilterSpec(Game1.TargetRelation.Enemy, Game1.TargetClass.Bomber));
        Assert.Equal(new short[] { 1 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ A friendly-lifepod filter matches only FRIENDLY ships with the
    /// Lifepod ability bit (native c_habmLifepod, WinTrek.cpp:10331).</summary>
    [Fact]
    public void Friendly_lifepod_filter_matches_only_friendly_lifepods()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 1, DataHullTypeIGC.HabmLifepod, new SimVec(0, 0, 10)),  // friendly lifepod
            Ship(2, 2, DataHullTypeIGC.HabmLifepod, new SimVec(0, 0, 5)),   // enemy lifepod — NO
            Ship(3, 1, DataHullTypeIGC.HabmFighter, new SimVec(0, 0, 8)),   // friendly fighter — NO
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Game1.ClassFilterSpec(Game1.TargetRelation.Friendly, Game1.TargetClass.Lifepod));
        Assert.Equal(new short[] { 1 }, got.Select(x => x.Id).ToArray());
    }

    // ── major base spec (native c_ttStation|AnyCluster|c_sabmFlag) ──────────

    [Fact]
    public void Enemy_major_base_spec_is_enemy_flag_stations()
    {
        var spec = Game1.MajorBaseSpec(Game1.TargetRelation.Enemy);
        Assert.Equal(Game1.TargetRelation.Enemy, spec.Relation);
        Assert.Equal(DataStationTypeIGC.SabmFlag, spec.RoleBitmask);
        Assert.True(spec.StationOnly);
        Assert.False(spec.MajorBaseOnly);   // the flag test is via RoleBitmask
    }

    // ── nearest / next / previous step semantics (TrekFindTarget triad) ─────

    /// <summary>⭐ Nearest always returns the head of the nearest-first list,
    /// ignoring the current target (native c_ttNearest picks the global nearest,
    /// common.cpp:575-578).</summary>
    [Fact]
    public void Nearest_ignores_current_and_returns_closest()
    {
        var list = new List<Game1.AutopilotTarget> { T(1), T(2), T(3) };
        Assert.Equal((short)1, Game1.StepCycle(list, T(2), Game1.StepDirection.Nearest).Id);
    }

    /// <summary>⭐ Next advances one past the current in list order and wraps at
    /// the end (native mLink->next() + wrap, common.cpp:611-614).</summary>
    [Fact]
    public void Next_steps_forward_and_wraps()
    {
        var list = new List<Game1.AutopilotTarget> { T(1), T(2), T(3) };
        Assert.Equal((short)3, Game1.StepCycle(list, T(2), Game1.StepDirection.Next).Id);
        Assert.Equal((short)1, Game1.StepCycle(list, T(3), Game1.StepDirection.Next).Id);   // wrap
    }

    /// <summary>⭐ Previous retreats one and wraps at the front (native
    /// c_ttPrevious, mLink->txen(), common.cpp:605-607).</summary>
    [Fact]
    public void Previous_steps_backward_and_wraps()
    {
        var list = new List<Game1.AutopilotTarget> { T(1), T(2), T(3) };
        Assert.Equal((short)1, Game1.StepCycle(list, T(2), Game1.StepDirection.Previous).Id);
        Assert.Equal((short)3, Game1.StepCycle(list, T(1), Game1.StepDirection.Previous).Id);  // wrap
    }

    /// <summary>⭐ With no current target any step direction falls back to the
    /// nearest (native forces c_ttNearest when pmodelCurrent is null,
    /// common.cpp:505-510).</summary>
    [Fact]
    public void No_current_falls_back_to_nearest_in_any_direction()
    {
        var list = new List<Game1.AutopilotTarget> { T(1), T(2), T(3) };
        Assert.Equal((short)1, Game1.StepCycle(list, null, Game1.StepDirection.Next).Id);
        Assert.Equal((short)1, Game1.StepCycle(list, null, Game1.StepDirection.Previous).Id);
        Assert.Equal((short)1, Game1.StepCycle(list, null, Game1.StepDirection.Nearest).Id);
    }

    // ── HostileDamageTracker: native 40 s rolling window + severity ranking ──

    /// <summary>⭐ Damage is summed per attacker and ranked; the highest total in
    /// the window is the hostile target (native bucket list sorted by
    /// totalDamage, common.cpp:2624-2660).</summary>
    [Fact]
    public void Hostile_ranks_damagers_by_severity_in_window()
    {
        var tr = new Game1.HostileDamageTracker();
        long t0 = 100_000L;
        tr.Record(1, 50f, t0 + 1000);
        tr.Record(2, 120f, t0 + 2000);
        tr.Record(1, 10f, t0 + 3000);
        var d = tr.Damagers(t0 + 3000);
        // Attacker 2 has the higher total (120 > 60) → tops the severity rank.
        Assert.Equal(new short[] { 2, 1 }, d.Select(x => x.AttackerId).ToArray());
        Assert.Equal(120f, d[0].Total);
        Assert.Equal(60f, d[1].Total);
    }

    /// <summary>⭐ Damage fades out of the window as it ages past 40 s (native
    /// 8 × 5 s slot rotation; a bucket's total drops as its oldest slot is
    /// discarded, igc.h:4926-4928 / common.cpp:2572-2589).</summary>
    [Fact]
    public void Hostile_damage_decays_out_after_the_window()
    {
        var tr = new Game1.HostileDamageTracker();
        long t0 = 100_000L;
        tr.Record(1, 100f, t0);
        // Fresh within the window: still the tracked top damager.
        Assert.Equal(100f, tr.Damagers(t0 + 1_000)[0].Total);
        // 41 s later the slot has left the 8×5 s window entirely → no damagers.
        Assert.Empty(tr.Damagers(t0 + 41_000));
    }

    /// <summary>⭐ Damage accrued in DISTINCT slots is aggregated within the
    /// window; the tracker holds the sum until the oldest slot ages out.</summary>
    [Fact]
    public void Hostile_aggregates_across_slots_within_window()
    {
        var tr = new Game1.HostileDamageTracker();
        long t0 = 100_000L;
        tr.Record(7, 30f, t0 + 1_000);
        tr.Record(7, 40f, t0 + 6_000);   // next 5 s slot
        Assert.Equal(70f, tr.Total(7));
    }
}
