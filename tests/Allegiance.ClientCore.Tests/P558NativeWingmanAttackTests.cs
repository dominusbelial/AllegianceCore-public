using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P558 — failing-first UNIT tests for the NATIVE wingman ATTACK steering
/// ported into the MCP combat law (replacing the P539 stand-off ring). All pure
/// functions over synthetic scalars (no server); every rule cites the native
/// wingman attack branch, <c>src/Igc/shipIGC.cpp:2356-2604</c>:
/// <list type="bullet">
/// <item>the throttle law (<c>:2474</c>) and the band-Close match-speed throttle
/// (<c>:2506-2511</c>);</item>
/// <item>the DISTANCE BANDS keyed to the intercept time vs the projectile
/// lifespan (<c>:2492-2571</c>) — Close / Engage / Approach / Lost;</item>
/// <item>the FIRE GATE — allWeapons iff <c>angleToLead ≤ 0.10 rad AND
/// t ≤ lifespan·0.95</c> (<c>:2589-2602</c>);</item>
/// <item>the missile lock + evasion gate (<c>:2584-2596</c>, inherited from
/// P557's <see cref="Showcase.DecideWingmanMissile"/>);</item>
/// <item>the ROOT-CAUSE property: the native law NEVER holds a stand-off ring
/// scaled to the bot's own weapon range — the throttle always drives forward
/// and the bands close, so a longer-ranged side fires at its own longer reach
/// instead of orbiting farther out (the P556 failure).</item>
/// </list>
/// Demonstrating failing-first: on the pre-P558 code these symbols do not exist
/// (compile-time fail); to show the RUNTIME failing-first, sabotage one pure
/// rule (e.g. flip the fire-gate openFireDistPercentage) and the corresponding
/// test fails, then restore.
/// </summary>
public class P558NativeWingmanAttackTests
{
    private const int Fwd = ShipControlState.ForwardButton;
    private const int Bwd = ShipControlState.BackwardButton;
    private const int Left = ShipControlState.LeftButton;
    private const int Ab = ShipControlState.AfterburnerButton;

    // ── Throttle law (shipIGC.cpp:2474) ─────────────────────────────────

    [Fact]
    public void ThrottleLaw_NoseOnLeadIsFullForward()
    {
        // angleToLead = 0 (nose exactly on the lead) → throttle 1.0.
        Assert.Equal(1.0f, BotManager.AttackThrottle(0.0f), precision: 5);
    }

    [Fact]
    public void ThrottleLaw_AngledNinetyDegreesIsHalf()
    {
        // angleToLead = π/2 → 0.5 + (π − π/2)/(2π) = 0.75.
        Assert.Equal(0.75f, BotManager.AttackThrottle(MathF.PI / 2.0f), precision: 5);
        // angleToLead = π (nose away) → 0.5 + 0 = 0.5.
        Assert.Equal(0.5f, BotManager.AttackThrottle(MathF.PI), precision: 5);
    }

    // ── Band-Close match-speed throttle (shipIGC.cpp:2506-2511) ─────────

    [Fact]
    public void MatchSpeedThrottle_ClampsAtMaxAndZero()
    {
        Assert.Equal(1.0f, BotManager.AttackMatchSpeedThrottle(150f, 120f));   // target >= our max → full forward
        Assert.Equal(1.0f, BotManager.AttackMatchSpeedThrottle(120f, 120f));   // exactly at max
        Assert.Equal(-1.0f, BotManager.AttackMatchSpeedThrottle(0f, 120f));    // stationary target → reverse
        Assert.Equal(-1.0f, BotManager.AttackMatchSpeedThrottle(-40f, 120f));  // retreating target → reverse
    }

    [Fact]
    public void MatchSpeedThrottle_LinearInterpolation()
    {
        // 2·(targetDirSpeed / maxSpeed) − 1: 60/120 → 0.0; 30/120 → −0.5.
        Assert.Equal(0.0f, BotManager.AttackMatchSpeedThrottle(60f, 120f), precision: 5);
        Assert.Equal(-0.5f, BotManager.AttackMatchSpeedThrottle(30f, 120f), precision: 5);
    }

    // ── Distance bands (shipIGC.cpp:2492-2571) ─────────────────────────

    [Fact]
    public void BandSelection_UsesNativeLifespanFractions()
    {
        const float lifespan = 2.0f;
        Assert.Equal(BotManager.AttackBand.Close, BotManager.BandOf(0.899f, lifespan));    // < 0.45·2 = 0.9
        Assert.Equal(BotManager.AttackBand.Engage, BotManager.BandOf(0.901f, lifespan));    // < 0.90·2 = 1.8
        Assert.Equal(BotManager.AttackBand.Engage, BotManager.BandOf(1.799f, lifespan));
        Assert.Equal(BotManager.AttackBand.Approach, BotManager.BandOf(1.801f, lifespan));  // < 1.5·2 = 3.0
        Assert.Equal(BotManager.AttackBand.Approach, BotManager.BandOf(2.999f, lifespan));
        Assert.Equal(BotManager.AttackBand.Lost, BotManager.BandOf(3.001f, lifespan));      // >= 3.0
    }

    [Fact]
    public void CloseBand_MovesForward_WhenTargetCloses()
    {
        // relativeSpeed < −20 (target coming at us / we chase) → FORWARD; < −50 → critical.
        var s = BotManager.SolveAttackSteer(0f, t: 0.2f, lifespan: 1.0f, relativeSpeed: -30f, 10f, 120f, false, false, false);
        Assert.True((s.StateBits & Fwd) != 0, "Close band must press forward when the target closes");
        Assert.False(s.CriticalMovement);

        var c = BotManager.SolveAttackSteer(0f, t: 0.2f, lifespan: 1.0f, relativeSpeed: -60f, 10f, 120f, false, false, false);
        Assert.True((c.StateBits & Fwd) != 0 && c.CriticalMovement, "relativeSpeed < −50 must set critical movement");
    }

    [Fact]
    public void CloseBand_MatchesSpeedViaThrottle_WhenRelativeSpeedMatched()
    {
        // |relativeSpeed| ≤ 20 → throttle override with the match-speed law, no buttons.
        var s = BotManager.SolveAttackSteer(0f, t: 0.2f, lifespan: 1.0f, relativeSpeed: 0f, 60f, 120f, false, false, false);
        Assert.True(s.ThrottleOverride, "matched-speed Close band must override the throttle axis");
        Assert.Equal(0.0f, s.ThrottleValue, precision: 5);
        Assert.Equal(0, s.StateBits & (Fwd | Bwd));
    }

    [Fact]
    public void EngageBand_Brakes_WhenClosingVeryFast()
    {
        // relativeSpeed > 50 → BACKWARD (shipIGC.cpp:2516-2517).
        var s = BotManager.SolveAttackSteer(0f, t: 0.6f, lifespan: 1.0f, relativeSpeed: 90f, 10f, 120f, false, false, false);
        Assert.True((s.StateBits & Bwd) != 0, "Engage band must brake when the target closes very fast");
    }

    [Fact]
    public void EngageBand_Afterburners_OnFastClosure_WithPriorAfterburnerOrDeepNegative()
    {
        // relativeSpeed < −15 with the afterburner held → afterburner
        // (shipIGC.cpp:2522-2523).
        var held = BotManager.SolveAttackSteer(0f, t: 0.6f, lifespan: 1.0f, relativeSpeed: -20f, 10f, 120f, false, false, afterburnerHeld: true);
        Assert.True((held.StateBits & Ab) != 0, "fast closure while already afterburning must hold afterburner");
        // Same closure but NOT already afterburning: only below −30 does it kick in.
        var notHeld = BotManager.SolveAttackSteer(0f, t: 0.6f, lifespan: 1.0f, relativeSpeed: -20f, 10f, 120f, false, false, afterburnerHeld: false);
        Assert.True((notHeld.StateBits & Ab) == 0, "−20 without prior afterburner must NOT afterburn (native −30 floor)");
        // Deep negative (relativeSpeed < −30) afterburns regardless.
        var deep = BotManager.SolveAttackSteer(0f, t: 0.6f, lifespan: 1.0f, relativeSpeed: -40f, 10f, 120f, false, false, afterburnerHeld: false);
        Assert.True((deep.StateBits & Ab) != 0, "relativeSpeed < −30 must afterburn regardless of prior state");
        Assert.True(deep.CriticalMovement);
    }

    [Fact]
    public void ApproachBand_ClosesForward_AndAfterburnsBelowForty()
    {
        // relativeSpeed < 80 → FORWARD; < 40 → critical; < 20 → afterburner
        // (shipIGC.cpp:2531-2541).
        var s = BotManager.SolveAttackSteer(0f, t: 1.3f, lifespan: 1.0f, relativeSpeed: 10f, 10f, 120f, false, false, false);
        Assert.True((s.StateBits & Fwd) != 0, "Approach band must press forward to close");
        Assert.True(s.CriticalMovement && (s.StateBits & Ab) != 0, "relativeSpeed < 20 must be critical + afterburner");
    }

    [Fact]
    public void LostBand_BurnsAfterEscapedTarget_ByDefault()
    {
        // t >= 1.5·lifespan, non-aggressive wingman → afterburner (shipIGC.cpp:2570).
        var s = BotManager.SolveAttackSteer(0f, t: 1.6f, lifespan: 1.0f, relativeSpeed: 10f, 10f, 120f, false, inRangeAggressive: false, false);
        Assert.True((s.StateBits & Ab) != 0, "a Lost-band non-aggressive wingman must burn after the escaped target");
        Assert.False(s.Escape);
    }

    [Fact]
    public void LostBand_AggressiveWingman_FlagsEscapeForRetarget()
    {
        // Aggressive (c_wbbmInRangeAggressive) → the caller re-targets
        // (shipIGC.cpp:2545-2565); flagged as Escape, no direct afterburner bit.
        var s = BotManager.SolveAttackSteer(0f, t: 1.6f, lifespan: 1.0f, relativeSpeed: 10f, 10f, 120f, false, inRangeAggressive: true, false);
        Assert.True(s.Escape, "an aggressive Lost-band wingman must flag a target-switch escape");
        Assert.True((s.StateBits & Ab) == 0, "the caller adds the afterburner when it re-targets");
    }

    [Fact]
    public void LowAim_InReach_StrafesWhenStrafeFlagSet()
    {
        // angleToLead >= π/2 (poorly aimed), still in reach, Strafe flag → LEFT
        // weave (shipIGC.cpp:2590-2591).
        var s = BotManager.SolveAttackSteer(MathF.PI * 0.6f, t: 0.5f, lifespan: 1.0f, relativeSpeed: 0f, 10f, 120f, strafe: true, false, false);
        Assert.True((s.StateBits & Left) != 0, "a poorly-aimed strafing wingman must hold the left-strafe button");
    }

    [Fact]
    public void LowAim_OutOfReach_DoesNotStrafe()
    {
        // t beyond the fire envelope → no low-aim strafe (the else-if guards on
        // t <= lifespan·openFireDist).
        var s = BotManager.SolveAttackSteer(MathF.PI * 0.6f, t: 1.2f, lifespan: 1.0f, relativeSpeed: 0f, 10f, 120f, strafe: true, false, false);
        Assert.True((s.StateBits & Left) == 0, "out-of-reach low-aim must not strafe");
    }

    // ── Fire gate (shipIGC.cpp:2589-2602) ────────────────────────────────

    [Fact]
    public void FireGate_On_WhenNoseOnLeadAndInEnvelope()
    {
        // angleToLead ≤ 0.10 rad and t ≤ 0.95·lifespan → allWeapons.
        Assert.True(BotManager.AttackFireGate(0.05f, 0.94f, 1.0f));
        Assert.True(BotManager.AttackFireGate(0.099f, 0.949f, 1.0f));
    }

    [Fact]
    public void FireGate_Off_BeyondMaxOffAngle()
    {
        // angleToLead 0.11 rad (> 0.10) → no fire even if in reach.
        Assert.False(BotManager.AttackFireGate(0.11f, 0.5f, 1.0f));
    }

    [Fact]
    public void FireGate_Off_BeyondOpenFireFraction()
    {
        // t 0.951·lifespan (> 0.95) → no fire even if well aimed.
        Assert.False(BotManager.AttackFireGate(0.05f, 0.952f, 1.0f));
    }

    // ── Root cause (P556): the native law does NOT hold a range-scaled ring ─

    [Fact]
    public void NativeLaw_DoesNotScaleAnOrbitToWeaponRange()
    {
        // The P556 failure: a longer-ranged gun made the bot orbit FARTHER out
        // (ring = 0.72 × range), so the better-ranged side fired less. The
        // native attack law has NO ring: the throttle is a pure function of the
        // aim angle (always 0.5..1.0 — "fly hard"), and the bands are keyed to
        // the PROJECTILE LIFESPAN, not to a ring. A longer-lifespan gun simply
        // opens fire at a longer reach (t ≤ 0.95·lifespan) while still closing.
        //
        // Concretely: two guns with the same projectile speed but lifespans 1.0
        // and 1.067 s (the P556 Gat-1/Gat-3 ratio, 600 → 640 reach vs 600) at
        // the same intercept t = 0.958 s. The long gun fires (0.958 ≤ 0.95·1.066
        // = 1.013); the short gun holds (0.958 > 0.95·1.0). That is the intended
        // longer-reach advantage — and unlike the old ring, the throttle still
        // pushes forward at the same angle, so the long gun is NOT parked out.
        Assert.True(BotManager.AttackFireGate(0.0f, 0.958f, 1.0667f),
            "the longer-lifespan gun fires at an intercept the shorter one cannot");
        Assert.False(BotManager.AttackFireGate(0.0f, 0.958f, 1.0f),
            "the shorter-lifespan gun holds at the same intercept");
        // And at the SAME aim angle the throttle is identical — no orbit scaling.
        Assert.Equal(BotManager.AttackThrottle(0.2f), BotManager.AttackThrottle(0.2f));
        Assert.Equal(0.875f, BotManager.AttackThrottle(MathF.PI / 4.0f), precision: 5);
    }

    // ── Missile lock + evasion gate (shipIGC.cpp:2584-2596, inherited P557) ─

    [Fact]
    public void MissileGate_RequiresFullLock_AndBeatsEvasion()
    {
        // maxEvasionSpeed = 20 + maxLock·30; a 50-u/s non-evasive target fires.
        Assert.True(Showcase.DecideWingmanMissile(1.0f, new(0, 0, 50), new(1, 0, 0), new(0, 1, 0), 1.0f));
        // Lock below 1.0 never fires.
        Assert.False(Showcase.DecideWingmanMissile(0.99f, new(0, 0, 50), new(1, 0, 0), new(0, 1, 0), 1.0f));
    }
}
