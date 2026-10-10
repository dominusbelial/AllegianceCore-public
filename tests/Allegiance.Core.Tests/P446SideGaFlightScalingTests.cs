using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit.Abstractions;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P446 — the side's <see cref="GlobalAttributeSet"/> scales a ship's flight
/// physics through the new GA-aware accessors
/// (<see cref="Ship.EffectiveMaxSpeed"/>/<see cref="Ship.EffectiveThrust"/>/
/// <see cref="Ship.EffectiveMaxTurnRate(int)"/>/<see cref="Ship.EffectiveTurnTorque(int)"/>),
/// mirroring native <c>MyHullType</c> (<c>shipIGC.cpp:4528-4546</c>). Each is
/// <c>raw hull value × side's GlobalAttributes[c_ga…]</c>, and every
/// flight-physics read in <see cref="Ship.ExecuteMove"/> and the steering/NPC
/// sites routes through them (the port of <c>m_myHullType</c> /
/// <c>GetHullType()-&gt;…</c>, all GA-scaled — see the P446 report's per-site
/// table). A sideless ship (<see cref="Ship.SideId"/> null) or an unresolved
/// side reads identity (1.0), reproducing the pre-P446 behaviour exactly.
/// </summary>
public class P446SideGaFlightScalingTests
{
    private readonly ITestOutputHelper _o;
    public P446SideGaFlightScalingTests(ITestOutputHelper o) => _o = o;

    private const short Side0 = 0;

    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>The same low-HullId representative the flight-model tests use —
    /// a real turning, accelerating hull (never a degenerate one).</summary>
    private static readonly Lazy<DataHullTypeIGC> RepresentativeHull = new(() =>
    {
        var ordered = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).ToList();
        if (ordered.Count == 0) throw new InvalidOperationException("no hull in core");
        return ordered[0];
    });

    /// <summary>Build a single-side mission whose sole side carries the given
    /// GA factors for the four flight attributes (all others identity 1.0 — a
    /// synthetic <c>Gas</c>, isolating the multiplier under test).</summary>
    private static Mission NewMission(float maxSpeed = 1f, float thrust = 1f, float turnRate = 1f, float turnTorque = 1f)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        IReadOnlyList<SideSpec> specs = [new SideSpec(Side0, "Team 1", civ.CivilizationId)];
        var m = Mission.Assemble(Core.Value, SomeMap.Value, specs, new NullIgcSite());
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        arr[GlobalAttributeSet.MaxSpeed] = maxSpeed;
        arr[GlobalAttributeSet.Thrust] = thrust;
        arr[GlobalAttributeSet.TurnRate] = turnRate;
        arr[GlobalAttributeSet.TurnTorque] = turnTorque;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    private static Ship NewGaShip(Mission m)
        => new(shipId: 1, hull: RepresentativeHull.Value, position: Vector3.Zero,
               velocity: Vector3.Zero, orientation: Orientation.Identity)
        {
            Mission = m,
            SideId = m.Sides[0].Id,
        };

    private static Ship NewSidelessShip()
        => new(shipId: 1, hull: RepresentativeHull.Value, position: Vector3.Zero,
               velocity: Vector3.Zero, orientation: Orientation.Identity);

    // ── accessor-level unit checks ────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accessors_scale_raw_hull_by_the_side_GA_factors()
    {
        Require.Artwork();
        var m = NewMission(maxSpeed: 1.5f, thrust: 2f, turnRate: 1.25f, turnTorque: 1.1f);
        var ship = NewGaShip(m);
        var hull = RepresentativeHull.Value;

        Assert.Equal(hull.Speed * 1.5f, ship.EffectiveMaxSpeed(), 6);
        Assert.Equal(hull.Thrust * 2f, ship.EffectiveThrust(), 6);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(hull.MaxTurnRates[i] * 1.25f, ship.EffectiveMaxTurnRate(i), 6);
            Assert.Equal(hull.TurnTorques[i] * 1.1f, ship.EffectiveTurnTorque(i), 6);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sideless_and_unknown_side_read_identity()
    {
        Require.Artwork();
        var sideless = NewSidelessShip();
        var hull = RepresentativeHull.Value;
        Assert.Equal(hull.Speed, sideless.EffectiveMaxSpeed(), 6);
        Assert.Equal(hull.Thrust, sideless.EffectiveThrust(), 6);
        Assert.Equal(hull.MaxTurnRates[0], sideless.EffectiveMaxTurnRate(0), 6);
        Assert.Equal(hull.TurnTorques[0], sideless.EffectiveTurnTorque(0), 6);

        // An identity-GA side also reproduces raw exactly.
        var m = NewMission();
        var ship = NewGaShip(m);
        Assert.Equal(hull.Speed, ship.EffectiveMaxSpeed(), 6);
        Assert.Equal(hull.Thrust, ship.EffectiveThrust(), 6);
    }

    // ── ExecuteMove-level behavioural tests ──────────────────────────────

    /// <summary>
    /// Main convergence test (the P446 headline): with the side's
    /// <c>c_gaMaxSpeed = 1.5</c>, the throttle-cruise branch (<c>shipIGC.cpp:
    /// 2990-2998</c>) targets <c>-0.5·(1+Throttle)·maxSpeed</c> using the
    /// GA-scaled max speed, so a ship at full throttle converges to
    /// <c>≈ 1.5 × hull.Speed</c>, not <c>hull.Speed</c>. <b>Pre-fix:</b> the
    /// cruise used raw <c>_hull.Speed</c>, so it topped out at
    /// <c>hull.Speed</c> regardless of the side's GA — this test fails on that
    /// code (failure recorded in the P446 report).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxSpeed_GA_scales_full_throttle_cruise_top_speed()
    {
        Require.Artwork();
        var m = NewMission(maxSpeed: 1.5f);
        var ship = NewGaShip(m);
        var hull = RepresentativeHull.Value;
        float rawMax = hull.Speed;
        float expected = 1.5f * rawMax;
        _o.WriteLine($"hull.Speed={rawMax:R} expected(1.5x)={expected:R}");

        ship.Controls = new ControlData(0f, 0f, 0f, Throttle: 1f);   // full throttle cruise
        for (int i = 0; i < 40000; i++)
            ship.ExecuteMove(0.008 * i, 0.008 * (i + 1));            // 320 s — converges
        float topSpeed = ship.Velocity.Length();
        _o.WriteLine($"cruise top speed={topSpeed:R}");

        // About 1.5× the raw hull speed (generous calibration tolerance).
        Assert.InRange(topSpeed, 0.90f * expected, 1.08f * expected);
        // And crucially NOT the raw (unscaled) value:
        Assert.True(topSpeed > 1.3f * rawMax, $"top speed {topSpeed} must reflect the 1.5x GA max speed, not raw {rawMax}");
    }

    /// <summary>Identity GA (all 1.0) reproduces the pre-P446 behaviour exactly:
    /// the cruise plateau equals the raw-hull top speed, and a GA ship with an
    /// identity side flies identically to a sideless ship.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Identity_GA_reproduces_current_Behaviour_exactly()
    {
        Require.Artwork();
        var m = NewMission();
        var ship = NewGaShip(m);
        var sideless = NewSidelessShip();
        var hull = RepresentativeHull.Value;

        // (a) cruise plateau == raw hull.Speed (the pre-fix value).
        ship.Controls = new ControlData(0f, 0f, 0f, Throttle: 1f);
        sideless.Controls = ship.Controls;
        for (int i = 0; i < 40000; i++)
        {
            ship.ExecuteMove(0.008 * i, 0.008 * (i + 1));
            sideless.ExecuteMove(0.008 * i, 0.008 * (i + 1));
        }
        _o.WriteLine($"identity cruise={ship.Velocity.Length():R} raw={hull.Speed:R}");
        Assert.InRange(ship.Velocity.Length(), 0.90f * hull.Speed, 1.06f * hull.Speed);
        // (b) GA identity ship ≡ sideless ship, bit-for-bit (same integration).
        Assert.Equal(sideless.Velocity, ship.Velocity);
        Assert.Equal(sideless.Orientation.GetForward(), ship.Orientation.GetForward());
    }

    /// <summary>Thrust (<c>c_gaThrust</c>) scales the realised acceleration.
    /// From rest under the manual forward branch the first-step Δv is
    /// <c>dT·thrust/Mass</c> (drag negligible at v=0), so doubling the GA
    /// thrust doubles the early acceleration.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Thrust_GA_scales_acceleration()
    {
        Require.Artwork();
        var m = NewMission(thrust: 1.5f);
        var ship = NewGaShip(m);
        var id = NewGaShip(NewMission());           // identity side
        var hull = RepresentativeHull.Value;

        ship.StateM = ShipControlState.ForwardButton;
        id.StateM = ShipControlState.ForwardButton;
        const double dT = 0.025;

        // Integrate both from rest for a short fixed window; drag is small.
        double t = 0;
        for (int i = 0; i < 20; i++) { ship.ExecuteMove(t, t + dT); id.ExecuteMove(t, t + dT); t += dT; }
        float vGa = ship.Velocity.Length();
        float vId = id.Velocity.Length();
        _o.WriteLine($"thrust GA=1.5: v={vGa:R} identity v={vId:R} raw thrust={hull.Thrust:R} mass={hull.Mass:R}");
        Assert.True(vId > 0f);
        // Early Δv is dominated by dT·thrust/Mass → ratio ≈ 1.5 (generous).
        Assert.InRange(vGa / vId, 1.35f, 1.65f);
    }

    /// <summary>Turn rate (<c>c_gaTurnRate</c>) scales the steady-state yaw
    /// rate: full-yaw control eventually converges the turn rate to
    /// <c>EffectiveMaxTurnRate(yaw)</c> = <c>hull.MaxTurnRates[yaw] × 1.5</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TurnRate_GA_scales_steady_state_yaw()
    {
        Require.Artwork();
        var m = NewMission(turnRate: 1.5f);
        var ship = NewGaShip(m);
        var hull = RepresentativeHull.Value;
        float rawMaxYaw = hull.MaxTurnRates[ControlData.AxisYaw];
        Assert.True(rawMaxYaw > 0f, "test hull must turn");
        float expected = 1.5f * rawMaxYaw;

        ship.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: -1f);   // full yaw, no thrust
        for (int i = 0; i < 2000; i++)
            ship.ExecuteMove(0.003 * i, 0.003 * (i + 1));   // plenty of time to converge
        float steadyYaw = ship.TurnRates[ControlData.AxisYaw];
        _o.WriteLine($"steady yaw={steadyYaw:R} raw max={rawMaxYaw:R} expected(1.5x)={expected:R}");
        Assert.InRange(steadyYaw, expected - 1e-3f, expected + 1e-3f);
    }

    /// <summary>Turn torque (<c>c_gaTurnTorque</c>) scales the RAMP (how fast
    /// the turn rate approaches its target), not the steady-state value. With
    /// turn-rate GA identity (same target for both) but torque GA 1.5, the
    /// per-step <c>maxDelta = tm·EffectiveTurnTorque</c> is 1.5×, so while both
    /// ships are still in the linear ramp the GA ship's rate is ~1.5× the
    /// identity ship's.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TurnTorque_GA_scales_ramp_rate()
    {
        Require.Artwork();
        var m = NewMission(turnTorque: 1.5f);            // turn rate GA identity
        var ship = NewGaShip(m);
        var id = NewGaShip(NewMission());                // identity
        var hull = RepresentativeHull.Value;
        float target = hull.MaxTurnRates[ControlData.AxisYaw];   // same for both (turnRate GA=1)
        Assert.True(target > 0f);

        ship.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: -1f);
        id.Controls = ship.Controls;
        // Sample during the LINEAR ramp phase — before either reaches `target`.
        // A modest number of small steps keeps both well below target while the
        // ratio (equal to the torque ratio 1.5) is already distinguishable.
        const double dT = 0.006;
        double t = 0;
        for (int i = 0; i < 60; i++) { ship.ExecuteMove(t, t + dT); id.ExecuteMove(t, t + dT); t += dT; }
        float yawGa = ship.TurnRates[ControlData.AxisYaw];
        float yawId = id.TurnRates[ControlData.AxisYaw];
        _o.WriteLine($"ramp GA torque=1.5 yaw={yawGa:R} identity yaw={yawId:R} target={target:R} (ratio={(yawId > 0 ? yawGa / yawId : float.NaN):R})");
        Assert.True(yawId > 0f && yawGa < target, $"still in the ramp (yawGa={yawGa} < target={target})");
        Assert.InRange(yawGa / yawId, 1.35f, 1.65f);      // torque ratio 1.5
    }
}
