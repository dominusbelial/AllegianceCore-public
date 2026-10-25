using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// The two cross-side-visibility predicates this arc's foundation stands on —
/// a faithful port of the native <c>IscannerIGC::InScannerRange</c> and the
/// free <c>LineOfSightExist</c>, built against the port's established
/// position+sphere convention and left deliberately unwired (this is the
/// P131 foundation; the per-cluster detection sweep that calls them on a
/// schedule is the separate next task).
/// </summary>
/// <remarks>
/// Bodies are projected to the lightweight <see cref="SightBody"/> rather than
/// the concrete <see cref="Ship"/>/<see cref="Station"/>/<see cref="Probe"/>
/// classes so the predicates stay model-independent and unit-testable in
/// isolation — the native takes <c>ImodelIGC*</c> pointers; here the caller
/// supplies the geometry the native would read off each model. All three of
/// the port's scanner types feed it identically (every ship, station and probe
/// registers as a scanner for its own side unconditionally on joining a
/// cluster — <c>Igc/clusterIGC.h:126,148,219</c>, <c>Igc/shipIGC.cpp:3700</c>,
/// <c>Igc/stationIGC.h:275</c> — and each carries <c>c_mtScanner</c>:
/// <c>stationigc.cpp</c>, <c>probeigc.cpp:115</c>).
/// </remarks>
public static class VisibilityMath
{
    /// <summary>
    /// A body participating in visibility geometry: a centre position and its
    /// collision/occlusion radius. Uses the port's established sphere radius
    /// convention (0.5 × hull length for ships, P113/P114/P116 precedent;
    /// <c>Station.Radius</c> / <c>ProbeType.Radius</c> for the others).
    /// </summary>
    public readonly record struct SightBody(Vector3 Position, float Radius);

    /// <summary>
    /// The effective detection radius of a scanner-target pair — the port of the
    /// native <c>r = GetRadius() + pModel-&gt;GetRadius() + m</c> in
    /// <c>InScannerRange</c> (<c>stationIGC.h:320</c>, <c>shipIGC.h:355</c>,
    /// <c>probeigc.h:175</c>), where the native margin is
    /// <c>m = scannerRange · targetSignature</c> (<c>stationIGC.h:315</c>) then
    /// divided by the target side's global <c>c_gaSignature</c> attribute
    /// (<c>stationIGC.h:317-319</c>). That global-attribute divisor is the
    /// tech-tree-adjacent <c>GlobalAttributeSet</c> infrastructure this port has
    /// already confirmed absent four times (P117, P119, P121, P124); it is
    /// simplified to <c>1.0</c> (no attenuation) exactly as those tasks did —
    /// no new global-attribute infrastructure is built for one divisor. The
    /// probe's additional <c>* GetSide()-&gt;...GetAttribute(c_gaScanRange)</c>
    /// factor (<c>probeigc.h:165</c>) is the same absent infrastructure and is
    /// likewise <c>1.0</c>.
    /// </summary>
    public static float DetectionRadius(float scannerRadius, float scannerRange,
                                        float targetRadius, float targetSignature)
        => scannerRadius + targetRadius + scannerRange * targetSignature;

    /// <summary>
    /// The range half of <c>InScannerRange</c> only (no line-of-sight): is the
    /// target centre within the pair's effective detection radius? Direct port
    /// of <c>(GetPosition() - pModel-&gt;GetPosition()).LengthSquared() &lt;=
    /// r*r</c> (<c>stationIGC.h:321</c>).
    /// </summary>
    public static bool InDetectRange(
        SightBody scanner, float scannerRange,
        SightBody target, float targetSignature)
    {
        float r = DetectionRadius(scanner.Radius, scannerRange, target.Radius, targetSignature);
        return (scanner.Position - target.Position).LengthSquared() <= r * r;
    }

    /// <summary>
    /// The full native <c>InScannerRange</c> (<c>stationIGC.h:308-328</c>,
    /// structurally identical at <c>shipIGC.h:341-364</c> and
    /// <c>probeigc.h:163-180</c>): same cluster, in detection range, and with
    /// line of sight. The flag-carrier early-return (<c>GetFlag() == NA</c>,
    /// always-visible) is omitted because this port has <b>no flag concept</b>
    /// — Phase 6 capture-the-flag is not built and no port model exposes a
    /// <c>Flag</c> (native <c>shipIGC.h:289</c>); every model is effectively
    /// <c>NA</c>, so only the normal branch can ever run and a dead branch would
    /// be misleading. <paramref name="scannerCluster"/>/<paramref
    /// name="targetCluster"/> are the cluster ids (the native compares
    /// <c>GetCluster()</c> pointers, <c>stationIGC.h:311-312</c>); the port's
    /// cluster id for a ship/probe is <c>Sector.Id</c> and for a station
    /// <c>ClusterId</c>. <paramref name="asteroids"/> are the cluster's asteroid
    /// occluders (native iterates <c>pcluster-&gt;GetAsteroids()</c>).
    /// </summary>
    public static bool InScannerRange(
        SightBody scanner, float scannerRange, short? scannerCluster,
        SightBody target, float targetSignature, short? targetCluster,
        IReadOnlyList<SightBody> asteroids)
    {
        if (scannerCluster is null || scannerCluster != targetCluster)
            return false;                                     // stationIGC.h:311-313
        if (!InDetectRange(scanner, scannerRange, target, targetSignature))
            return false;
        return LineOfSightExist(scanner.Position, target, asteroids);  // stationIGC.h:322
    }

    /// <summary>
    /// The port of the free function <c>LineOfSightExist(pcluster, pmodel1,
    /// pmodel2)</c> (<c>Igc/common.cpp:2324-2398</c>, read in full): a genuine
    /// asteroid-occlusion cone test. <paramref name="eye"/> is the scanner's
    /// centre (<c>P1</c>), <paramref name="target"/> the target's centre +
    /// radius (<c>P2</c>), and <paramref name="asteroids"/> every asteroid in
    /// the cluster (the only occluders the native considers — it iterates
    /// <c>pcluster-&gt;GetAsteroids()</c> and nothing else). The algorithm:
    /// normalise the eye→target ray; compute the angle the target subtends at
    /// the eye; then for each asteroid nearer than the target and in front of
    /// the eye, compute whether its silhouette (half-radius core, the native's
    /// "dense core" scale) covers the target's maximum separation angle. If any
    /// nearer asteroid's covered angle exceeds the target's farthest extent,
    /// the target is occluded → <c>false</c>. Ported faithfully with no
    /// simplification (it is already self-contained); no raycasting against
    /// arbitrary geometry, exactly the bounded native algorithm.
    /// </summary>
    public static bool LineOfSightExist(Vector3 eye, SightBody target, IReadOnlyList<SightBody> asteroids)
    {
        // V12 = P2 - P1; fOverLengthV12 = 1/|V12|; V12 (normalised) (common.cpp:2336-2341)
        Vector3 v12 = target.Position - eye;
        float lenSq12 = v12.LengthSquared();
        float overLen12 = 1.0f / MathF.Sqrt(lenSq12);
        Vector3 v12n = v12 * overLen12;

        // Angle subtended by the target from the eye (common.cpp:2344)
        float visibleAngle = MathF.Asin(target.Radius * overLen12);

        for (int i = 0; i < asteroids.Count; i++)
        {
            var obstacle = asteroids[i];
            // The native skips the target itself (pmodel2 != pmodel); the target
            // is never an asteroid, so no identity check is needed here.
            Vector3 v13 = obstacle.Position - eye;           // V13 = P3 - P1
            float lenSq13 = v13.LengthSquared();
            if (lenSq13 < lenSq12)                           // obstacle closer than target
            {
                float dot = v12n * v13;                      // V12(norm) . V13 (Vector3 '*' = dot)
                if (dot > 0.0f)                              // obstacle in front of the eye
                {
                    float overLen13 = 1.0f / MathF.Sqrt(lenSq13);
                    float cosSep = dot * overLen13;          // cosine of separation angle (common.cpp:2362-2363)
                    float radius3 = obstacle.Radius * 0.5f;  // dense-core half-radius (common.cpp:2368)
                    float covered = MathF.Asin(radius3 * overLen13);       // obstacle coverage angle
                    float sep = MathF.Acos(cosSep);          // separation angle eye→obstacle vs eye→target
                    float maxSep = sep + visibleAngle;       // farthest extent the target can be seen at
                    if (maxSep < covered)                    // common.cpp:2376-2378 (strict <)
                        return false;
                }
            }
        }
        return true;
    }
}
