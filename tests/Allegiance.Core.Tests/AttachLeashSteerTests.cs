using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P419 — the bounded-leash steering law used by the <c>vizfight attach</c>
/// mode's Flight phase (see <see cref="AttachLeashSteer"/>). These prove the
/// two verification requirements from the task:
///   (a) the steer term activates ONLY past the leash radius (inside, the
///       ambient weave passes through bit-for-bit unchanged); and
///   (b) past it, the controller actually REDUCES net distance from the fleet
///       anchor over simulated ticks (integrated through the real
///       <see cref="Orientation.Yaw"/> semantics the sim uses), and a
///       leash-less control demonstrably does NOT — proving the steer term is
///       what pulls the fleet back.
/// </summary>
public class AttachLeashSteerTests
{
    private const float Tol = 1e-4f;

    [Fact]
    public void InsideRadius_ReturnsAmbientWeaveUnchanged()
    {
        var orient = Orientation.Identity;
        var (yaw, throttle) = AttachLeashSteer.Steer(
            pos: new Vector3(600f, 0f, 0f), anchor: Vector3.Zero, leashRadius: 1000f,
            orient: orient, weaveYaw: 0.0367f, weaveThrottle: 0.6179f);

        Assert.True(Math.Abs(yaw - 0.0367f) <= Tol, $"yaw should be the weave, got {yaw}");
        Assert.True(Math.Abs(throttle - 0.6179f) <= Tol, $"throttle should be the weave, got {throttle}");
    }

    [Fact]
    public void AtBoundary_DoesNotEngage_TheSteerOnlyActivatesStrictlyPastThreshold()
    {
        var orient = Orientation.Identity;
        // Exactly ON the radius and just inside: leash inactive (dist <= leash).
        foreach (var pos in new[] { new Vector3(1000f, 0f, 0f), new Vector3(999.9f, 0f, 0f) })
        {
            var (yaw, _) = AttachLeashSteer.Steer(pos, Vector3.Zero, 1000f, orient, 0.025f, 0.6f);
            Assert.True(Math.Abs(yaw - 0.025f) <= Tol, $"expected weave at {pos.X}, got {yaw}");
        }

        // Just past the radius: engage (yaw must differ from the weave).
        var (outerYaw, _) = AttachLeashSteer.Steer(
            new Vector3(1001f, 0f, 0f), Vector3.Zero, 1000f, orient, 0.025f, 0.6f);
        Assert.True(Math.Abs(outerYaw - 0.025f) > Tol, "leash must engage strictly past the threshold");
    }

    [Fact]
    public void FarOutside_FullyEngaged_YawEqualsPureSteerLaw()
    {
        // Point the nose straight at the anchor so the cross-product law has a
        // definite, known signal: forward = -X aims at anchor at origin.
        var orient = Orientation.Identity;
        orient.SetForward(new Vector3(-1f, 0f, 0f));

        // Deeply past (push saturates to 1): the blend must yield the pure
        // steer term, i.e. weave is fully washed out in favour of the steer.
        var (yaw, throttle) = AttachLeashSteer.Steer(
            new Vector3(2000f, 0f, 0f), Vector3.Zero, 1000f, orient, weaveYaw: 0.083f, weaveThrottle: 0.683f);

        // forward = dirToAnchor => cross(forward, dir) ~ 0 => steer yaw ~ 0.
        Assert.True(Math.Abs(yaw - 0f) <= 0.06f, $"pointing at anchor should give ~0 steer yaw, got {yaw}");
        // ...and the throttle firms up to the out-of-bounds cruise value.
        Assert.True(Math.Abs(throttle - AttachLeashSteer.OutOfBoundsThrottle) <= Tol,
            $"out-of-bounds throttle expected, got {throttle}");
    }

    [Fact]
    public void EngagementRamps_WithOvershoot_Proportionally()
    {
        // A fixed geometry where the pure steer yaw is KNOWN non-zero: forward
        // points 45° off the anchor direction (not parallel), so body.Y != 0.
        var orient = Orientation.Identity;
        orient.SetForward(new Vector3(1f, 0f, 1f) / MathF.Sqrt(2f));
        var anchor = new Vector3(0f, 0f, 0f);
        const float leash = 1000f;

        float SteerYawFor(float distX)
        {
            var (y, _) = AttachLeashSteer.Steer(new Vector3(distX, 0f, 0f), anchor, leash, orient, 0f, 0.6f);
            return y;
        }

        // Barely past the threshold: push ~ 0 => yaw ~ weave (0 here).
        float barely = SteerYawFor(leash + 1f);
        // Deep past (>> blend zone of 500): push -> 1 => yaw -> the full steer value.
        float deep = SteerYawFor(leash + 750f);
        float deeper = SteerYawFor(leash + 4000f);

        // The steer term must grow with overshoot and saturate (capped), not blow up.
        Assert.True(deep != 0f, "deep overshoot must engage the steer term");
        Assert.True(Math.Abs(barely) <= Math.Abs(deep) + Tol, "engagement must be proportional to overshoot");
        Assert.True(Math.Abs(deeper) <= Math.Abs(deep) + 0.005f, "engagement must cap, not grow unbounded");
    }

    [Fact]
    public void KinematicIntegration_ReducesNetDistanceFromAnchor()
    {
        var anchor = Vector3.Zero;
        const float leash = 1000f;

        // Start well past the leash with the nose mostly-AWAY but 31° off the
        // exact anti-radial line (never exactly parallel — the continuous
        // cross-product signal needs a non-zero offset to begin curling, the
        // same reason the NPC autopilot has no astern stall).
        var start = new Vector3(1600f, 0f, 0f);
        var initialForward = new Vector3(1f, 0f, 0.614f) / MathF.Sqrt(1f + 0.614f * 0.614f);

        const float dt = 0.034f;    // ~30 Hz server tick
        const int ticks = 960;      // ~33s of sim time

        // The ground truth for the frame conventions is the real sim mover:
        // Ship + DataHullTypeIGC + ExecuteMove (the exact loop a real server
        // ship goes through), not a hand-rolled orientation integrator.
        float EndDistanceUsing(Func<Vector3, Orientation, (float, float)> controller)
        {
            var ship = new Ship(1, SyntheticHull(), start, Vector3.Zero, Orientation.Identity);
            ship.Orientation.SetForward(initialForward);
            for (int i = 0; i < ticks; i++)
            {
                var (yaw, throttle) = controller(ship.Position, ship.Orientation);
                ship.Controls = new ControlData(yaw, 0f, 0f, throttle);
                ship.ExecuteMove(i * dt, (i + 1) * dt);
                ship.Position += ship.Velocity * dt;   // Ship.Update's integrate step (Ship.cs:6471)
            }
            return ship.Position.Length();
        }

        // With the real leash: it must bring the ship back inside the radius.
        float leashedFinal = EndDistanceUsing((pos, o) =>
            AttachLeashSteer.Steer(pos, anchor, leash, o, weaveYaw: 0f, weaveThrottle: 0.754f));
        Assert.True(leashedFinal < leash, $"leash should return the fleet inside the radius, ended at {leashedFinal:0}");

        // Without the leash (weave only, no anchor feedback): it keeps drifting
        // away — the discriminator that proves the steer term is what pulls back.
        float unleashedFinal = EndDistanceUsing((pos, o) => (0f, 0.754f));
        Assert.True(unleashedFinal > start.Length(),
            $"leash-less control should keep drifting away, ended at {unleashedFinal:0} (start {start.Length():0})");
        Assert.True(leashedFinal < unleashedFinal,
            $"leash must end closer than leash-less: {leashedFinal:0} vs {unleashedFinal:0}");
    }

    /// <summary>A fighter-scale synthetic hull with all the fields
    /// <see cref="Ship.ExecuteMove"/> reads, so the integration is the real
    /// sim mover (frame/sign conventions included), not a fake.</summary>
    private static DataHullTypeIGC SyntheticHull() => new(
        Price: 9800, TimeToBuild: 6600, ModelName: "fighter", IconName: "fighter",
        Name: "Fighter", Description: "test hull",
        GroupId: 0, TtbmRequired: Array.Empty<byte>(), TtbmEffects: Array.Empty<byte>(),
        Mass: 916f, Signature: 920f, Speed: 840f,
        MaxTurnRates: new[] { 3.0596f, 2.7276f, 1.9693f },
        TurnTorques: new[] { 3486.958f, 8772.587f, 8860.947f },
        Thrust: 586048f, SideMultiplier: 918.685f / 842.541f, BackMultiplier: 5760f / 624f,
        ScannerRange: 930f, MaxFuel: 272f, Ecm: 577.552f, Length: 812f,
        MaxEnergy: 942f, RechargeRate: 837f, RipcordSpeed: 595.638f, RipcordCost: 654.597f,
        MaxAmmo: 273, HullId: 442, SuccessorHullId: -1,
        MaxWeapons: (byte)2, MaxFixedWeapons: (byte)2, HitPoints: 749f, HardpointOffset: 843,
        DefenseType: (byte)0, CapacityMagazine: 832, CapacityDispenser: 831,
        CapacityChaffLauncher: 869, PreferredPartsTypes: Array.Empty<short>(),
        HabmCapabilities: 883, TextureName: "fighter",
        PmEquipment: new short[8], InteriorSound: 895, ExteriorSound: 896,
        MainThrusterInteriorSound: 897, MainThrusterExteriorSound: 899,
        ManuveringThrusterInteriorSound: 901, ManuveringThrusterExteriorSound: 903,
        Hardpoints: Array.Empty<DataHullTypeIGC.Hardpoint>());
}

