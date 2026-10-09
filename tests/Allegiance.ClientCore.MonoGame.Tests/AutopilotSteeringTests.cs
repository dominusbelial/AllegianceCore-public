using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="AutopilotSteering"/> — the standalone port of
/// the sim's <c>SteerToward</c> "face and thrust" law (Ship.cs:3720). No
/// graphics device involved: every case feeds a plain own <see cref="Vector3"/>/
/// <see cref="Orientation"/> and a target position and checks the resulting
/// <see cref="ControlData"/>. Identity orientation is used throughout (forward =
/// (0,0,-1)), so expectations are easy to reason about.
/// </summary>
public class AutopilotSteeringTests
{
    private static readonly Orientation Identity = Orientation.Identity;

    [Fact]
    public void NoseAlreadyOnTarget_FarAway_NoTurnStrongForwardThrottle()
    {
        // Identity ship at the origin, forward = (0,0,-1). Target 1000 units
        // dead ahead → cross(forward, dir) = 0 → zero turn; far beyond the
        // brake ramp and perfectly aligned → throttle slams to +1.
        var c = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(0f, 0f, -1000f), Vector3.Zero);

        AssertNear(0f, c.Yaw);
        AssertNear(0f, c.Pitch);
        AssertNear(0f, c.Roll);                 // autopilot never rolls
        Assert.True(c.Throttle > 0.9f, $"expected near-full forward throttle, got {c.Throttle}");
    }

    [Fact]
    public void TargetMostlyBehind_OffAxis_TurnsDefinitely_NoStallOrNaN()
    {
        // Mostly astern (behind is +z for an identity ship whose nose is -z)
        // but offset to +x, so forward × dir is a definite non-degenerate
        // turn signal — not the degenerate ~0 of a perfectly antiparallel
        // target, and never NaN.
        var c = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(200f, 0f, 900f), Vector3.Zero);

        Assert.True(float.IsFinite(c.Yaw) && float.IsFinite(c.Pitch), "turn must not be NaN");
        Assert.True(MathF.Abs(c.Yaw) > 0.1f, $"expected a definite yaw turn, got {c.Yaw}");
        AssertNear(0f, c.Roll);
    }

    [Fact]
    public void WithinArrivalRadius_ThrottleNegative_BrakesInsteadOfFlyingThrough()
    {
        // Target 30 units dead ahead (arrival radius is 60) — inside the
        // "close enough to stop" zone, so the two-zone ramp has shed power to
        // nil (distFactor = 0) and the throttle must go negative (hold/brake),
        // even though the nose is perfectly aligned.
        var c = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(0f, 0f, -30f), Vector3.Zero);

        Assert.True(c.Throttle < 0f, $"expected braking/holding throttle, got {c.Throttle}");
        AssertNear(0f, c.Roll);
    }

    [Fact]
    public void FarAwayAndMisaligned_TurnDominates_ThrottleBacksOffWhileTurning()
    {
        // Far enough that distFactor = 1, but the nose is NOT on the target
        // (align < 1), so the throttle is pulled back from +1 — the "turn then
        // burn" behaviour that stops the ship orbiting a target it can't track
        // at full speed. Still forward (≥ the -1 floor) because some of the
        // target is ahead, but weaker than full thrust.
        var c = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(500f, 0f, -1000f), Vector3.Zero);

        Assert.True(c.Throttle > -1f && c.Throttle < 1f,
            $"expected a partial (turning) throttle, got {c.Throttle}");
        Assert.True(MathF.Abs(c.Yaw) > 0.1f, "misaligned target must still turn");
        AssertNear(0f, c.Roll);
    }

    [Fact]
    public void Pursue_AlwaysFullThrottle_EvenInsideBrakingZone_ForWarpPassThrough()
    {
        // P540b — Pursue is the ROUTED (warp) leg's law: it faces the mouth and
        // pins throttle to +1 with no braking, so the ship flies THROUGH the aleph
        // (a warp is a transit point, not a stop). Contrast the same geometry with
        // SteerToward, which brakes to a stop inside its arrival radius — a small
        // hull creeping into a warp exits slow and re-crosses it, oscillating.
        // Pursue holds +1 even when the target is only a few units dead ahead.
        var c = AutopilotSteering.Pursue(Vector3.Zero, Identity, new Vector3(0f, 0f, -5f));

        AssertNear(0f, c.Yaw);
        AssertNear(0f, c.Pitch);
        AssertNear(0f, c.Roll);
        Assert.Equal(1f, c.Throttle);   // full forward through the mouth
    }

    [Fact]
    public void Pursue_TurnsOntoAnOffAxisTarget_KeepingFullThrottle()
    {
        // Off-axis target: still turns (finite yaw), still full forward — the
        // pass-through never brakes, it only re-aims.
        var c = AutopilotSteering.Pursue(Vector3.Zero, Identity, new Vector3(400f, 0f, -400f));

        Assert.True(float.IsFinite(c.Yaw), "turn must not be NaN");
        Assert.True(MathF.Abs(c.Yaw) > 0.1f, "off-axis target must turn");
        AssertNear(0f, c.Roll);
        Assert.Equal(1f, c.Throttle);
    }

    [Fact]
    public void DeadCentre_GuardsDivByZero_HoldsKillThrottle()
    {
        // Target at the ship's own position → toTarget is (near) zero. Must not
        // divide by zero (no NaN anywhere) and must hold Throttle = -1 (the
        // "dead centre" kill-throttle from SteerToward's early return).
        var c = AutopilotSteering.SteerToward(
            new Vector3(100f, 200f, 300f), Identity, new Vector3(100f, 200f, 300f), Vector3.Zero);

        Assert.Equal(-1f, c.Throttle);
        Assert.True(float.IsFinite(c.Yaw) && float.IsFinite(c.Pitch) && float.IsFinite(c.Roll),
            "dead-centre must not yield NaN axes");
        AssertNear(0f, c.Yaw);
        AssertNear(0f, c.Pitch);
        AssertNear(0f, c.Roll);
    }

    [Fact]
    public void CustomTunables_OverrideDefaults()
    {
        // Distance 800 with a custom ramp (arrival 700 / brake 900) puts the
        // target MID-ramp → distFactor = (800-700)/(900-700) = 0.5 →
        // throttle = 2·0.5·1 - 1 = 0. The same geometry with the DEFAULT ramp
        // (arrival 60 / brake 420) is far beyond the brake start (800 > 420) →
        // distFactor = 1 → throttle = 1. The differing results prove the
        // custom tunables genuinely take effect, not just the defaults.
        var custom = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(0f, 0f, -800f), Vector3.Zero,
            arrivalRadius: 700f, brakeStartDistance: 900f);
        AssertNear(0f, custom.Throttle);

        var dflt = AutopilotSteering.SteerToward(
            Vector3.Zero, Identity, new Vector3(0f, 0f, -800f), Vector3.Zero);
        AssertNear(1f, dflt.Throttle);
        Assert.True(dflt.Throttle > custom.Throttle,
            "custom ramp should change the throttle");
    }

    private static void AssertNear(float expected, float actual, int places = 3) =>
        Assert.Equal(expected, actual, places);
}
