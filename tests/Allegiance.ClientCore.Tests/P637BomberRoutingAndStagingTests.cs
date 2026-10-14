using System.Collections.Generic;
using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;
using H = Allegiance.Tools.McpSpawner.BotTargetClass;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P637 — failing-first tests for the march-mode fixes:
///   • <see cref="BotManager.ResolveRoleTarget"/> — a BOMBER's own target is the
///     ENEMY HOME sector, not the fighter rendezvous (its own route/staging);
///   • <see cref="BotManager.SameStagingGroup"/> — staging/release grouping keys
///     by (side, goal, route), so a bomber (goal = enemy home) is NEVER in a
///     fighter wing's group and never waits on a fighter's release;
///   • <see cref="BotManager.IsStageReleased"/> — a relaunched (reinforcement)
///     combat bot is placed in the RELEASED phase so it routes straight to the
///     fight instead of re-entering a staging barrier its wing already left
///     (scope 4 — respawned bots idle at home);
///   • <see cref="CombatTargetSelector.PickBy"/> — focus-fire (fill the
///     most-engaged target to its cap before starting another) vs P622's spread
///     (least-engaged first), the P622 follow-up (scope 5).
/// On the pre-P637 code these symbols / behaviours do not exist or are the old
/// spread behaviour, so the tests fail first (compile for the new symbols,
/// assertion for the changed rules) and pass after — the repo's accepted
/// failing-first form (see P622CombatTargetSpreadTests).
/// </summary>
public class P637BomberRoutingAndStagingTests
{
    // ── 1. Role → target (bomber vs fighter) ─────────────────────────────

    /// <summary>A BOMBER's own objective is the ENEMY HOME sector (its garrison);
    /// a fighter converges on the rendezvous. Without this, bombers ride the
    /// fighter wing to the rendezvous and never press the enemy base.</summary>
    [Fact]
    public void RoleTarget_BomberTargetsEnemyHome()
    {
        // rendezvous = Alderan 1052; enemy home (this side's opponent) = 1051.
        Assert.Equal((short)1051, BotManager.ResolveRoleTarget(Showcase.HullRole.Bomber, 1052, 1051));
    }

    /// <summary>Every non-bomber role keeps the rendezvous (fighters fight at the
    /// rendezvous; only bombers split off to the enemy home).</summary>
    [Fact]
    public void RoleTarget_NonBomberKeepsRendezvous()
    {
        Assert.Equal((short)1052, BotManager.ResolveRoleTarget(Showcase.HullRole.Fighter, 1052, 1051));
        Assert.Equal((short)1052, BotManager.ResolveRoleTarget(Showcase.HullRole.Scout, 1052, 1051));
        Assert.Equal((short)1052, BotManager.ResolveRoleTarget(Showcase.HullRole.Interceptor, 1052, 1051));
    }

    /// <summary>If the enemy home can't be resolved, even a bomber falls back to
    /// the rendezvous (never a bogus -1 target that strands it at home).</summary>
    [Fact]
    public void RoleTarget_BomberFallsBackToRendezvousWhenNoEnemyHome()
        => Assert.Equal((short)1052, BotManager.ResolveRoleTarget(Showcase.HullRole.Bomber, 1052, -1));

    // ── 2. Staging grouping by (side, goal, route) ───────────────────────

    /// <summary>A bomber (goal = enemy home 1051) and a fighter (goal =
    /// rendezvous 1052) on the SAME side and SAME route index are NOT the same
    /// staging group: the bomber must not wait on — or be counted into — the
    /// fighter wing's release.</summary>
    [Fact]
    public void StagingGroup_BomberAndFighterDifferentGoal_NotSameGroup()
    {
        // side 1, route 0, but different goals (the P637 bug).
        Assert.False(BotManager.SameStagingGroup(1, 1051, 0, 1, 1052, 0));
        // Different side, different goal, different route — obviously not.
        Assert.False(BotManager.SameStagingGroup(0, 1052, 0, 1, 1051, 2));
    }

    /// <summary>Bots converging on the SAME target along the SAME route ARE one
    /// group (the multi-route wing retains its per-wing release).</summary>
    [Fact]
    public void StagingGroup_SameGoalSameRoute_IsSameGroup()
    {
        Assert.True(BotManager.SameStagingGroup(1, 1052, 0, 1, 1052, 0));
        Assert.True(BotManager.SameStagingGroup(0, 1051, 1, 0, 1051, 1));
    }

    // ── 3. Relaunched reinforcement skips staging (scope 4) ──────────────

    /// <summary>A bot in the HOLD phase (1) is NOT released — it is waiting on
    /// its wing. A relaunched reinforcement is placed in the RELEASED phase (2)
    /// so it routes straight to the fight instead of re-entering the barrier.</summary>
    [Fact]
    public void StagePhases_HoldNotReleased_ReleasedIs()
    {
        Assert.False(BotManager.IsStageReleased(BotManager.StageHoldPhase));
        Assert.False(BotManager.IsStageReleased(BotManager.StageTransitPhase));
        Assert.True(BotManager.IsStageReleased(BotManager.StageReleasedPhase));
    }

    // ── 5. Idle detection (scope 6) ──────────────────────────────────────

    /// <summary>A docked bot is never "idle" (it is parked, not wandering); a
    /// bot that hasn't moved for > IdleWindowSec at ~zero speed IS idle.</summary>
    [Fact]
    public void IdleRule_StillSlowAndUndocked_IsIdle()
    {
        double now = 100.0;
        // Moves were seen up to 1 s ago → not idle.
        Assert.False(BotManager.IsBotIdle(docked: false, now, lastMoveAt: 99.0, speed: 2.0));
        Assert.False(BotManager.IsBotIdle(docked: false, now, lastMoveAt: 99.5, speed: 0.0));
        // Still for > IdleWindowSec at ~zero speed, undocked → idle.
        Assert.True(BotManager.IsBotIdle(docked: false, now, lastMoveAt: 94.0, speed: 0.0));
        Assert.True(BotManager.IsBotIdle(docked: false, now, lastMoveAt: 90.0, speed: 0.2));
        // Docked (or moving) → never idle.
        Assert.False(BotManager.IsBotIdle(docked: true, now, lastMoveAt: 90.0, speed: 0.0));
        Assert.False(BotManager.IsBotIdle(docked: false, now, lastMoveAt: 90.0, speed: 5.0));
    }

    // ── 6. Focus-fire vs spread (scope 5, P622 follow-up) ────────────────

    private static BotTargetCandidate Cand(short id, H cls, bool threat = false, float dist = 100f)
        => new(id, new Vector3(dist, 0f, 0f), cls, threat) { Distance = dist };

    private static BotSelectionContext Ctx(Dictionary<short, int> engagers, int teammates = 30)
        => new(SelfShipId: 9900, MyCluster: 1052, teammates, engagers);

    /// <summary>FOCUS-FIRE (P637 default): after bot1 engages target A, the NEXT
    /// bot keeps filling A (most-engaged, under cap) instead of spreading to B —
    /// so a fight concentrates and kills land.</summary>
    [Fact]
    public void FocusFire_FillsMostEngagedTargetBeforeStartingAnother()
    {
        var a = Cand(9102, H.Fighter, dist: 100f);
        var b = Cand(9103, H.Fighter, dist: 120f);
        var all = new[] { a, b };

        // Bot1 takes the nearest (both at 0 engagers).
        var b1 = CombatTargetSelector.Select(all, Ctx(new Dictionary<short, int>()));
        Assert.Equal((short)9102, b1.TargetId);

        // Bot2: focus-fire keeps piling onto 9102 (1 engager, under cap 3) —
        // NOT the spread to 9103 that P622 did.
        var b2 = CombatTargetSelector.Select(all,
            Ctx(new Dictionary<short, int> { [(short)b1.TargetId!.Value] = 1 }));
        Assert.False(b2.Hold);
        Assert.Equal((short)9102, b2.TargetId!.Value);
    }

    /// <summary>Focus-fire still respects the cap: a target at its fighter cap (3)
    /// is no longer eligible, so the next bot starts the OTHER target.</summary>
    [Fact]
    public void FocusFire_CappedTargetOverflowsToNext()
    {
        var a = Cand(9102, H.Fighter, dist: 100f);
        var b = Cand(9103, H.Fighter, dist: 120f);
        var all = new[] { a, b };

        var ch = CombatTargetSelector.Select(all,
            Ctx(new Dictionary<short, int> { [9102] = CombatTargetSelector.CapFighter }));
        Assert.False(ch.Hold);
        Assert.Equal((short)9103, ch.TargetId!.Value);
    }

    /// <summary>The P622 SPREAD mode (measurement switch) still picks the
    /// least-engaged target — exposed via PickBy(false, …) so the legacy
    /// ALLEGIANCE_P622_LEAST_ENGAGED=1 behaviour is preserved for A/B.</summary>
    [Fact]
    public void SpreadSwitch_StillPicksLeastEngaged()
    {
        var a = Cand(9102, H.Fighter, dist: 100f);
        var b = Cand(9103, H.Fighter, dist: 120f);
        var all = new List<BotTargetCandidate> { a, b };
        var ctx = Ctx(new Dictionary<short, int> { [9102] = 1 });

        var spread = CombatTargetSelector.PickBy(concentrate: false, all, ctx);
        Assert.Equal((short)9103, spread!.Value.TargetId!.Value);   // least-engaged

        var focused = CombatTargetSelector.PickBy(concentrate: true, all, ctx);
        Assert.Equal((short)9102, focused!.Value.TargetId!.Value);  // most-engaged (fill-to-cap)
    }

    /// <summary>Tie-break at equal engagement stays NEAREST under focus-fire too
    /// (the native nearest cue is retained within a tier).</summary>
    [Fact]
    public void FocusFire_EqualEngagementTiesToNearest()
    {
        var near = Cand(9102, H.Fighter, dist: 80f);
        var far = Cand(9103, H.Fighter, dist: 640f);
        var all = new[] { far, near };
        var ch = CombatTargetSelector.Select(all, Ctx(new Dictionary<short, int>()));
        Assert.Equal((short)9102, ch.TargetId);   // nearer wins at equal (0) engagement
    }
}
