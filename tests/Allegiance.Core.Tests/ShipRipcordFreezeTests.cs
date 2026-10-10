using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P405 — ripcord countdown must freeze maneuvering and thrust (native does;
/// the port now does too). Native <c>CshipIGC::ExecuteShipMove</c> guards both
/// the turn-rate ramp and the entire engine-thrust block with
/// <c>if (!m_pmodelRipcord)</c> (<c>shipIGC.cpp:2885,2962</c>); the port's
/// <see cref="Ship.ExecuteMove"/> now mirrors that exactly — during an active
/// ripcord countdown (<see cref="Ship.RipcordModel"/> non-null) the manoeuvre
/// ramp and the engine-thrust block are suppressed, leaving only the
/// orientation integration and drag unconditional (so the ship drifts
/// ballistically with drag only).
///
/// These deterministic tests pin: (1) a ripcording ship commanded full yaw +
/// full forward thrust has its turn rates frozen at their pre-rip values and
/// its velocity changed by drag ONLY (an exact <c>v·fⁿ</c> decay, no thrust
/// contribution), and (2) a ship with no ripcord model is completely
/// unaffected — the same inputs still ramp the turn rate and accelerate.
/// </summary>
public class ShipRipcordFreezeTests
{
    private static readonly Lazy<DataHullTypeIGC> RepresentativeHull = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            var ordered = core.HullTypes.Values.OrderBy(h => h.HullId).ToList();
            if (ordered.Count == 0) continue;
            return ordered[0];
        }
        throw new InvalidOperationException("no static core with a hull found in corpus");
    });

    private static Ship NewShip() => new(
        shipId: 1,
        hull: RepresentativeHull.Value,
        position: Vector3.Zero,
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    /// <summary>The per-step drag factor <c>f</c> native applies to velocity
    /// (<c>shipIGC.cpp:2930</c>). Under ripcord the engine vector is zero, so
    /// each step's <c>Δv = thrustToVelocity·(0 − drag) = −(1−f)·v</c> — the
    /// velocity scales by exactly <c>f</c> each step and its direction is
    /// preserved, which is the "drag only" invariant the tests assert.</summary>
    private static float DragFactor(DataHullTypeIGC hull, float dT)
    {
        float thrustToVelocity = dT / hull.Mass;          // shipIGC.cpp:2868 (Ship.Mass => _hull.Mass)
        return (float)Math.Exp(-(double)hull.Thrust * thrustToVelocity / hull.Speed);
    }

    /// <summary>⭐ With a ripcord model active, a full-yaw + full-forward-thrust
    /// command produces NOTHING: turn rates stay pinned at their pre-rip values
    /// (the ramp never runs) and the velocity scales by exactly the per-step
    /// drag factor <c>f</c> over the stepped interval — no thrust contribution
    /// whatsoever, and a strict DECAY (thrust would have grown it). The whole
    /// thing is asserted analytically so the proof is exact, not approximate.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ripcord_active_freezes_turn_rates_and_thrust_only_drag()
    {
        Require.Artwork();
        var hull = RepresentativeHull.Value;
        Assert.True(hull.MaxTurnRates[ControlData.AxisYaw] > 0f, "test hull must allow turning");

        var ship = NewShip();
        var v0 = new Vector3(60f, -40f, -120f);     // nonzero so drag has something to act on
        ship.Velocity = v0;
        ship.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: +1f);  // full yaw + forward throttle
        ship.StateM = ShipControlState.ForwardButton;                                  // + forward thrust button

        // Arm a ripcord via the SAME state setter the real ripcord arc uses
        // (RequestRipcordCore -> ship.SetRipcordModel, Server.Docking.cs:411).
        ship.SetRipcordModel(new Ship.RipcordTarget((short)IgcObjectType.Station, 9090, 8875));
        Assert.True(ship.IsRipcordActive);

        var preRates = ship.GetTurnRates(new float[3]);
        Assert.Equal(0f, preRates[ControlData.AxisYaw]);

        const float dT = 0.03125f;
        const int n = 8;
        float f = DragFactor(hull, dT);
        Assert.True(f < 1f && f > 0f, $"drag factor must be in (0,1); got {f}");

        var expected = v0;
        for (int i = 0; i < n; i++)
        {
            ship.ExecuteMove(dT * i, dT * (i + 1));
            expected *= f;                               // velocity scales by f each step (drag only)
        }

        // Turn rates frozen at their pre-rip values — the manoeuvre ramp never ran.
        var afterRates = ship.GetTurnRates(new float[3]);
        Assert.Equal(preRates[ControlData.AxisYaw], afterRates[ControlData.AxisYaw]);
        Assert.Equal(0f, afterRates[ControlData.AxisPitch]);
        Assert.Equal(0f, afterRates[ControlData.AxisRoll]);

        // Velocity changed by drag only: equals v0 scaled by f^n, direction
        // preserved. Asserted with a tight absolute tolerance — the per-step
        // float drag accumulation rounds ~1e-5 relative, whereas any thrust
        // contribution over 8 steps of full forward thrust would be many u/s,
        // so 0.08 is simultaneously loose against noise and decisive against
        // a missing guard.
        Assert.True(MathF.Abs(ship.Velocity.X - expected.X) < 0.08f,
            $"drag-only X {ship.Velocity.X} != expected {expected.X}");
        Assert.True(MathF.Abs(ship.Velocity.Y - expected.Y) < 0.08f,
            $"drag-only Y {ship.Velocity.Y} != expected {expected.Y}");
        Assert.True(MathF.Abs(ship.Velocity.Z - expected.Z) < 0.08f,
            $"drag-only Z {ship.Velocity.Z} != expected {expected.Z}");
        Assert.True(ship.Velocity.Length() < v0.Length(),
            $"ripcording speed {ship.Velocity.Length()} must decay below starting {v0.Length()}");
    }

    /// <summary>⭐ A ship with NO ripcord model active is completely unaffected:
    /// the same full-yaw + full-forward-thrust command from rest ramps the turn
    /// rate up from zero and accelerates the velocity along the facing axis —
    /// maneuvering and thrust run exactly as before this change. This is the
    /// must-fail control for
    /// <see cref="Ripcord_active_freezes_turn_rates_and_thrust_only_drag"/>:
    /// identical inputs, only the ripcord state differs.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_ripcord_ship_is_completely_unaffected()
    {
        Require.Artwork();
        var hull = RepresentativeHull.Value;
        var ship = NewShip();                           // at rest, no ripcord
        ship.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: +1f);
        ship.StateM = ShipControlState.ForwardButton;

        Assert.False(ship.IsRipcordActive);
        Assert.Null(ship.RipcordModel);

        const float dT = 0.008f;
        const int n = 400;
        for (int i = 0; i < n; i++)
            ship.ExecuteMove(dT * i, dT * (i + 1));

        // Maneuver ran: the turn rate ramped from 0 toward (but not beyond) the hull max.
        var rates = ship.GetTurnRates(new float[3]);
        Assert.True(rates[ControlData.AxisYaw] > 0f,
            $"turn rate must ramp up with no ripcord; yaw {rates[ControlData.AxisYaw]}");
        Assert.True(rates[ControlData.AxisYaw] <= hull.MaxTurnRates[ControlData.AxisYaw] + 1e-5f,
            "turn rate must not exceed the hull max");

        // Thrust ran: accelerated from rest, nose-facing (-z), well past 1 m/s.
        var v = ship.Velocity;
        Assert.True(v.LengthSquared() > 1f, "must accelerate well past 1 m/s from rest");
        Assert.True(v.Z < 0f, $"forward thrust must drive -z; got Z={v.Z}");
    }
}
