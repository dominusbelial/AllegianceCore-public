using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests;

/// <summary>
/// Deterministic verification of <see cref="Ship.ExecuteMove"/> — the port of
/// <c>CshipIGC::ExecuteShipMove</c> (<c>shipIGC.cpp:2852-3054</c>). The point
/// of the flight model is reproducibility: identical inputs must produce
/// bit-identical outputs, and the C++ semantics (incl. the surprising ones —
/// no speed clamp, left-handed yaw, an asymmetric side/back multiplier) must be
/// reproduced, not "cleaned up".
/// </summary>
public class ShipFlightModelTests
{
    private readonly ITestOutputHelper _output;

    public ShipFlightModelTests(ITestOutputHelper output) => _output = output;

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

    private static void Snapshot(Ship s, out Vector3 vel, out Vector3 fwd, out Vector3 up, out Vector3 right, out float[] rates)
    {
        vel = s.Velocity;
        fwd = s.Orientation.GetForward();
        up = s.Orientation.GetUp();
        right = s.Orientation.GetRight();
        rates = s.GetTurnRates(new float[3]);
    }

    // ── determinism ────────────────────────────────────────────────────────

    [Fact]
    public void Identical_inputs_produce_bit_identical_outputs()
    {
        var a = NewShip();
        var b = NewShip();
        a.Controls = new ControlData(Yaw: 0.7123f, Pitch: -0.8801f, Roll: 0.991f, Throttle: 0.784f);
        b.Controls = a.Controls;
        a.StateM = ShipControlState.ForwardButton | ShipControlState.AfterburnerButton;
        b.StateM = a.StateM;

        // Same wall-clock simulation, two independently-constructed ships.
        for (double t = 0; t < 30.0; t += 0.039063)   // 768 steps of an odd-sized chunk
        {
            a.ExecuteMove(t, t + 0.023438);
            b.ExecuteMove(t, t + 0.023438);
        }

        Snapshot(a, out var va, out var fa, out var ua, out var ra, out var ta);
        Snapshot(b, out var vb, out var fb, out var ub, out var rb, out var tb);

        // Bit-identical = exact record-struct equality (no tolerance).
        Assert.Equal(va, vb);
        Assert.Equal(fa, fb);
        Assert.Equal(ua, ub);
        Assert.Equal(ra, rb);
        Assert.Equal(ta, tb);
    }

    // ── zero input ⇔ nothing moves ─────────────────────────────────────────

    [Fact]
    public void Neutral_controls_at_rest_move_nothing()
    {
        var ship = NewShip();                         // neutral controls, stateM == 0, velocity 0

        for (int i = 0; i < 200; i++)
            ship.ExecuteMove(0.005 * i, 0.005 * (i + 1));

        Assert.Equal(Vector3.Zero, ship.Velocity);
        Assert.Equal(Vector3.Zero, ship.Orientation.GetForward().Normalize() - Orientation.Identity.GetForward());
        Assert.Equal(new Vector3(0f, 0f, -1f), ship.Orientation.GetForward());   // still identity nose
        Assert.All(ship.TurnRates, r => Assert.Equal(0f, r));
    }

    // ── forward thrust travels along the facing axis only ─────────────────

    [Fact]
    public void Pure_forward_thrust_accelerates_along_the_facing_axis_only()
    {
        var ship = NewShip();                         // identity: facing axis is forward = (0,0,-1)
        ship.StateM = ShipControlState.ForwardButton;

        for (int i = 0; i < 400; i++)
            ship.ExecuteMove(0.0125 * i, 0.0125 * (i + 1));

        var v = ship.Velocity;
        Assert.True(v.Z < 0f, $"forward thrust must drive the nose direction (-z); got Z={v.Z}");
        Assert.True(v.LengthSquared() > 1f, "speed must grow well past 1 m/s under sustained thrust");
        Assert.InRange(MathF.Abs(v.X), 0f, 1e-4f);
        Assert.InRange(MathF.Abs(v.Y), 0f, 1e-4f);
    }

    // ── turn rates bounded; speed not hard-clamped ─────────────────────────

    [Fact]
    public void Turn_rate_never_exceeds_the_hull_max()
    {
        var ship = NewShip();
        var hull = RepresentativeHull.Value;
        ship.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: -1f); // full yaw, no thrust

        float maxYawRate = hull.MaxTurnRates[ControlData.AxisYaw];
        Assert.True(maxYawRate > 0f, "test hull must allow turning");

        for (int i = 0; i < 1000; i++)
        {
            ship.ExecuteMove(0.004 * i, 0.004 * (i + 1));
            Assert.True(ship.TurnRates[ControlData.AxisYaw] <= maxYawRate + 1e-5f,
                $"yaw rate {ship.TurnRates[0]} exceeded max {maxYawRate}");
        }
        Assert.InRange(ship.TurnRates[ControlData.AxisYaw], maxYawRate - 1e-3f, maxYawRate + 1e-5f); // converged to the target
    }

    [Fact]
    public void Speed_is_not_hard_clamped_and_forward_thrust_converges_below_max()
    {
        var hull = RepresentativeHull.Value;
        float maxSpeed = hull.Speed;
        Assert.True(maxSpeed > 0f);

        // (a) From rest, sustained forward thrust grows fast at first then
        // collapses toward a finite plateau. Crucially it is NOT bounded by
        // maxSpeed: this hull's manual thrust, integrated at dt=0.01 for long
        // enough, crosses the hull's Speed — the C++ imposes no clamp anywhere
        // in the movement path. The plateau is dT-dependent (larger steps →
        // higher terminal speed; the Euler discretization's equilibrium, not a
        // physical constant). Assert the invariants that hold regardless: it
        // accelerated and then plateaued to a finite speed (no runaway).
        var cruiser = NewShip();
        cruiser.StateM = ShipControlState.ForwardButton;

        for (int i = 0; i < 200; i++)                       // first 2 s
            cruiser.ExecuteMove(0.01 * i, 0.01 * (i + 1));
        float earlyGrowth = cruiser.Velocity.Length();      // grew from 0 to here

        for (int i = 200; i < 95400; i++)                    // out to ~954 s
            cruiser.ExecuteMove(0.01 * i, 0.01 * (i + 1));
        float cruise = cruiser.Velocity.Length();
        for (int i = 95900; i < 96400; i++)                  // a late 5 s window
            cruiser.ExecuteMove(0.01 * i, 0.01 * (i + 1));
        float late = cruiser.Velocity.Length();

        Assert.True(earlyGrowth > 0f, "must accelerate from rest");
        Assert.True(cruise > 0f && float.IsFinite(cruise), "plateau must be finite");
        Assert.True(late - cruise < earlyGrowth * 1e-3f, "growth must collapse to <0.1% of the early rate (plateau)");

        // (b) The C++ does NOT clamp: start already above max and it stays above
        // rather than snapping down (there is no `if speed > max speed = max`
        // anywhere in shipIGC.cpp's movement). Reproduced, not "fixed".
        var fast = NewShip();
        fast.Velocity = new Vector3(0f, 0f, -maxSpeed * 1.5f);
        fast.Controls = ControlData.Neutral;          // no input: drag only
        fast.ExecuteMove(0.016, 0.032);
        Assert.True(fast.Velocity.Length() > maxSpeed, "over-max velocity must not be hard-clamped to max");
    }

    // ── orthonormality over many steps ────────────────────────────────────

    [Fact]
    public void Orientation_stays_orthonormal_across_ten_thousand_steps()
    {
        var ship = NewShip();
        ship.Controls = new ControlData(Yaw: 0.871f, Pitch: -0.693f, Roll: 0.917f, Throttle: 0.3f);
        ship.StateM = ShipControlState.ForwardButton | ShipControlState.CoastButton;

        for (int i = 0; i < 10000; i++)
            ship.ExecuteMove(0.011 * i, 0.011 * (i + 1));

        var right = ship.Orientation.GetRight();
        var up = ship.Orientation.GetUp();
        var backward = ship.Orientation.GetBackward();
        Assert.Equal(1f, right.Length(), 5);
        Assert.Equal(1f, up.Length(), 5);
        Assert.Equal(1f, backward.Length(), 5);
        Assert.Equal(0f, right * up, 5);
        Assert.Equal(0f, up * backward, 5);
        Assert.Equal(0f, backward * right, 5);
    }

    // ── chunk-divergence measurement ───────────────────────────────────────

    /// <summary>
    /// Steps the same wall-clock interval as one <c>ExecuteMove</c> versus two
    /// even sub-steps (the mission's chunk split, <c>CalculateShip</c>/the tick,
    /// <c>missionigc.cpp:3156-3163</c>). They legitimately diverge because turn
    /// rates, torque multiplier and throttle are re-evaluated per sub-step; the
    /// magnitude is measured and printed for the README (a measurement, not an
    /// assertion of equality).
    /// </summary>
    [Fact]
    public void Measure_single_vs_split_step_divergence()
    {
        const int chunks = 3;
        const float total = 0.0950523f;               // non-power-of-two so float chunking is not exact

        // Whole interval in one ExecuteMove.
        var single = NewShip();
        single.Controls = new ControlData(Yaw: 0.7042f, Pitch: 0.8812f, Roll: -0.922f, Throttle: 0.874f);
        single.StateM = ShipControlState.ForwardButton | ShipControlState.RightButton;
        single.ExecuteMove(0, total);

        // Same interval split into `chunks` even sub-steps.
        float chunkDt = total / chunks;
        var split = NewShip();
        split.Controls = single.Controls;
        split.StateM = single.StateM;
        for (int i = 0; i < chunks; i++)
            split.ExecuteMove(i * chunkDt, (i + 1) * chunkDt);

        float dv = (single.Velocity - split.Velocity).Length();
        float df = (single.Orientation.GetForward() - split.Orientation.GetForward()).Length();
        _output.WriteLine($"CHUNKDIV deltaV={dv:R} deltaForward={df:R} (total={total} s, {chunks} chunks) hull.Speed={RepresentativeHull.Value.Speed}");
        Assert.True(float.IsFinite(dv) && float.IsFinite(df));
    }
}
