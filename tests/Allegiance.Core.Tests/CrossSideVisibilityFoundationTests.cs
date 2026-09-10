using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using VisibilityMath = Allegiance.Core.Sim.VisibilityMath;
using SightBody = Allegiance.Core.Sim.VisibilityMath.SightBody;

namespace Allegiance.Core.Tests;

/// <summary>
/// P131 — cross-side visibility foundation, isolated-predicate tests. Proves
/// the three deliverables in isolation against <b>real corpus data</b> (real
/// hull lengths / scanner ranges / signatures and a real asteroid position),
/// with no sim-loop integration and no wiring into any existing pass — exactly
/// the "foundation only" scope P114 used for <see cref="Ship.InGarage"/>.
/// </summary>
/// <remarks>
/// <para>Real bodies (decoded from the .igc corpus via the shared
/// <see cref="PayloadDecodeTests"/> helpers):</para>
/// <list type="bullet">
/// <item>Scanner ship <c>faohshcar</c> — Length 300 (→ ship sphere radius 150
/// via the port's <c>0.5 × Length</c> convention, P113/P114/P116), ScannerRange
/// 3600, Signature 1.5.</item>
/// <item>Target ship <c>dreghpod</c> — Length 4 (→ radius 2), Signature 0.5.</item>
/// <item>Station type 2 (EventCore) — Radius 302.5, ScannerRange 2400.</item>
/// <item>Probe <c>utl23</c> — Radius 6, ScannerRange 500, Signature 0.3.</item>
/// <item>Occluder: real asteroid id 3200 (<c>m2xspiderw.igc</c>), radius 644 at
/// <c>[3758.2, 1133.8, 3165.1]</c>.</item>
/// </list>
/// <para>Detection radius for faohshcar→dreghpod = 150 + 2 + 3600·0.5 = 1952
/// (the global-attribute divisors simplified to 1.0 per the established
/// P117/P119/P121/P124 precedent).</para>
/// </remarks>
public class CrossSideVisibilityFoundationTests
{
    private const short SideA = 5;
    private const short SideB = 9;
    private const short ClusterA = 701;
    private const short ClusterB = 702;

    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    /// <summary>Ship sphere radius via the port's 0.5×Length convention.</summary>
    private static float ShipRadius(DataHullTypeIGC h) => 0.5f * h.Length;

    /// <summary>A real faohshcar scanner body (radius 150) at <paramref name="pos"/>.</summary>
    private static SightBody ScannerShip(Vector3 pos) =>
        new(pos, ShipRadius(Hull("faohshcar")));

    /// <summary>A real dreghpod target body (radius 2) at <paramref name="pos"/>.</summary>
    private static SightBody TargetShip(Vector3 pos) =>
        new(pos, ShipRadius(Hull("dreghpod")));

    // ── SideVisibilityState: state transitions ──────────────────────────────

    [Fact]
    public void SetSideVisibility_true_makes_seen_and_current_eye_true()
    {
        var s = new SideVisibilityState();
        s.SetSideVisibility(SideA, true);
        Assert.True(s.SeenBySide(SideA));
        Assert.True(s.GetCurrentEye(SideA));   // native syncs CurrentEyed with the visible flag (FedSrv.CPP:11168)
    }

    [Fact]
    public void SetSideVisibility_false_clears_both_flags()
    {
        var s = new SideVisibilityState();
        s.SetSideVisibility(SideA, true);
        s.SetSideVisibility(SideA, false);
        Assert.False(s.SeenBySide(SideA));
        Assert.False(s.GetCurrentEye(SideA));
    }

    [Fact]
    public void Untouched_side_reads_false()
    {
        var s = new SideVisibilityState();
        Assert.False(s.SeenBySide(SideA));
        Assert.False(s.GetCurrentEye(SideA));
    }

    [Fact]
    public void Two_sides_track_independently()
    {
        var s = new SideVisibilityState();
        s.SetSideVisibility(SideA, true);
        s.SetSideVisibility(SideB, true);
        s.SetSideVisibility(SideA, false);   // flip one side only
        Assert.False(s.SeenBySide(SideA));
        Assert.False(s.GetCurrentEye(SideA));
        Assert.True(s.SeenBySide(SideB));    // the other side is untouched
        Assert.True(s.GetCurrentEye(SideB));
    }

    // ── InScannerRange: range + cluster, real ship scanner/target ───────────

    [Fact]
    public void InScannerRange_true_well_within_range_and_clear_los()
    {
        var scanner = ScannerShip(Vector3.Zero);
        var target = TargetShip(new Vector3(100f, 0f, 0f));
        var sr = Hull("faohshcar").ScannerRange;
        var tsig = Hull("dreghpod").Signature;

        // 100 ≪ 1952 detection radius, no asteroids → line of sight clear.
        Assert.True(VisibilityMath.InScannerRange(scanner, sr, ClusterA, target, tsig, ClusterA, []));
    }

    [Fact]
    public void InScannerRange_false_beyond_detection_range()
    {
        var scanner = ScannerShip(Vector3.Zero);
        var far = 50_000f;   // far beyond the ~1952 detection radius
        var target = TargetShip(new Vector3(far, 0f, 0f));
        var sr = Hull("faohshcar").ScannerRange;
        var tsig = Hull("dreghpod").Signature;

        Assert.False(VisibilityMath.InScannerRange(scanner, sr, ClusterA, target, tsig, ClusterA, []));
    }

    [Fact]
    public void InScannerRange_false_in_different_cluster_even_when_close()
    {
        var scanner = ScannerShip(Vector3.Zero);
        var target = TargetShip(new Vector3(50f, 0f, 0f));
        var sr = Hull("faohshcar").ScannerRange;
        var tsig = Hull("dreghpod").Signature;

        // Well within range, clear line of sight — but the clusters differ.
        Assert.True(VisibilityMath.InDetectRange(scanner, sr, target, tsig));
        Assert.False(VisibilityMath.InScannerRange(scanner, sr, ClusterA, target, tsig, ClusterB, []));
    }

    // ── InScannerRange: occluded by a real asteroid → false ─────────────────

    /// <summary>
    /// Hand-derived occlusion against the real asteroid id 3200
    /// (<c>m2xspiderw.igc</c>): radius 644 centred at
    /// <c>O = [3758.2, 1133.8, 3165.1]</c>. Scanner and target are placed on the
    /// x-axis through O, one radius (±644) either side, so the asteroid sits
    /// dead-centre on the eye→target ray:
    /// <list type="bullet">
    /// <item>scanner faohshcar at O−(644,0,0) = (3114.2, 1133.8, 3165.1);</item>
    /// <item>target dreghpod at O+(644,0,0) = (4402.2, 1133.8, 3165.1);</item>
    /// <item>eye→target distance = 1288.</item>
    /// </list>
    /// Coverage: eye→obstacle = 644, dense-core half radius = 0.5·644 = 322 →
    /// covered angle = asin(322/644) = asin(0.5) = 30°. Target visible angle =
    /// asin(2/1288) ≈ 0.089°. Separation angle = 0 (collinear), so max separation
    /// ≈ 0.089° ≪ 30° covered → decisively occluded. Detection radius 1952 ≥ 1288,
    /// so the target <b>is</b> in range — the false result is purely occlusion.
    /// </summary>
    [Fact]
    public void InScannerRange_false_when_a_real_asteroid_occludes_the_line()
    {
        var occluder = new SightBody(new Vector3(3758.2f, 1133.8f, 3165.1f), 644f);
        var scanner = ScannerShip(new Vector3(3114.2f, 1133.8f, 3165.1f));
        var target = TargetShip(new Vector3(4402.2f, 1133.8f, 3165.1f));
        var sr = Hull("faohshcar").ScannerRange;
        var tsig = Hull("dreghpod").Signature;

        // In range (1288 ≤ 1952) and line-of-sight is genuinely blocked.
        Assert.True(VisibilityMath.InDetectRange(scanner, sr, target, tsig));
        Assert.False(VisibilityMath.LineOfSightExist(scanner.Position, target, [occluder]));
        Assert.False(VisibilityMath.InScannerRange(scanner, sr, ClusterA, target, tsig, ClusterA, [occluder]));
    }

    // ── Scanner-type coverage: station and probe use the same formula ───────

    [Fact]
    public void InScannerRange_true_for_a_real_station_scanner()
    {
        var st = PayloadDecodeTests.Stations().First().V;   // type 2: Radius 302.5, SRange 2400
        var scanner = new SightBody(Vector3.Zero, st.Radius);
        var target = TargetShip(new Vector3(300f, 0f, 0f));
        var tsig = Hull("dreghpod").Signature;
        // 300 ≤ 302.5 + 2 + 2400·0.5 = 1504.5 → in range, clear LOS.
        Assert.True(VisibilityMath.InScannerRange(scanner, st.ScannerRange, ClusterA, target, tsig, ClusterA, []));
    }

    [Fact]
    public void InScannerRange_true_for_a_real_probe_scanner()
    {
        var pt = PayloadDecodeTests.Probes().First().V;      // utl23: Radius 6, SRange 500
        var scanner = new SightBody(Vector3.Zero, pt.Radius);
        var target = TargetShip(new Vector3(200f, 0f, 0f));
        var tsig = Hull("dreghpod").Signature;
        // 200 ≤ 6 + 2 + 500·0.5 = 258 → in range, clear LOS.
        Assert.True(VisibilityMath.InScannerRange(scanner, pt.ScannerRange, ClusterA, target, tsig, ClusterA, []));
    }

    // ── LineOfSightExist: the cone test in isolation ────────────────────────

    [Fact]
    public void LineOfSightExist_true_with_no_occluders()
        => Assert.True(VisibilityMath.LineOfSightExist(Vector3.Zero, TargetShip(new Vector3(500f, 0f, 0f)), []));

    [Fact]
    public void LineOfSightExist_true_when_obstacle_is_behind_the_eye()
    {
        var obstacle = new SightBody(new Vector3(-500f, 0f, 0f), 200f);
        // Obstacle is behind the eye (dot < 0) → can never block the forward ray.
        Assert.True(VisibilityMath.LineOfSightExist(Vector3.Zero, TargetShip(new Vector3(1000f, 0f, 0f)), [obstacle]));
    }

    [Fact]
    public void LineOfSightExist_true_when_obstacle_is_farther_than_target()
    {
        var obstacle = new SightBody(new Vector3(1000f, 0f, 0f), 200f);
        // Obstacle is beyond the target → the "closer than target" test fails.
        Assert.True(VisibilityMath.LineOfSightExist(Vector3.Zero, TargetShip(new Vector3(500f, 0f, 0f)), [obstacle]));
    }

    [Fact]
    public void LineOfSightExist_true_when_obstacle_is_off_to_the_side()
    {
        // Obstacle near the eye but far off-axis: separation angle ~63° dwarfs the
        // ~0.23° target extent + ~3.2° obstacle cover → not occluded.
        var obstacle = new SightBody(new Vector3(200f, 400f, 0f), 50f);
        Assert.True(VisibilityMath.LineOfSightExist(Vector3.Zero, TargetShip(new Vector3(500f, 0f, 0f)), [obstacle]));
    }
}
