using Allegiance.Core.Sim;

namespace Allegiance.Core.Collision;

/// <summary>
/// The port of the C++ <c>CshipIGC::Separate</c> (<c>src/Igc/shipIGC.cpp:514-616</c>):
/// derive the separating-plane normal between two colliding bodies from their
/// actual convex-hull extremes (not a radial centre-to-centre line) and push the
/// bodies apart to just-touching-plus-clearance. This is the normal every
/// collision bounce uses — for a sphere pair it degenerates to the radial
/// direction, but for a real <c>.cvh</c> hull it is the face/edge contact normal
/// native produces, which is what makes an elongated or lumpy rock bounce you
/// off its actual surface instead of off its enclosing bounding sphere (the
/// P621 fix).
/// </summary>
/// <remarks>
/// <para><b>Algorithm (faithful to native).</b> Work in body-2's local frame:
/// <c>dP = position1 − position2</c>, <c>o = o1·o2⁻¹</c>, <c>dpLocal =
/// o2⁻¹(dP)</c>. Seed the separation direction with the centre line
/// (<c>shipIGC.cpp:545-559</c>), then hill-climb it by scoring the 27 fixed
/// probe directions against the two hull extremes (<c>:561-601</c>): the score
/// is the signed gap <c>(eA − eB)·direction</c> between body-1's min extreme and
/// body-2's max extreme; the winning direction is the least-penetrating one
/// (a true separating direction, gap ≥ 0, short-circuits at gap = 0). Then
/// <c>*pNormal = directionBest·o2</c> back to world and displace along it by
/// <c>s = distanceBest − 0.5</c> (the native +0.5 clearance fudge,
/// <c>:603-615</c>), split half-and-half when <c>fEqualBounce</c> is set.</para>
/// <para><b>Extremes.</b> <c>eA</c> uses the min extreme of body-1 at position
/// <c>dpLocal</c> with orientation <c>o</c> (both already in body-2's frame);
/// <c>eB</c> uses the max extreme of body-2 with no offset (it sits at the
/// origin of its own frame). The port's <see cref="Collider.GetMinExtreme"/> /
/// <see cref="Collider.GetMaxExtreme"/> reproduce exactly the C++
/// <c>HitTest::GetMinExtreme/GetMaxExtreme</c> the native function calls
/// (<c>HitTest.cpp:865-909</c>), including the hull-subshape center offsets.</para>
/// <para><b>Edge case.</b> When the collision-time positions coincide (centres
/// equal), native perturbs <c>dP</c> to a random direction and shifts
/// <c>position1</c> by it (<c>:529-533</c>); reproduced.</para>
/// </remarks>
public static class Separate
{
    private const float Sqrt1o2 = 0.70710678118654752440084436210485f;
    private const float Sqrt1o3 = 0.57735026918962576450914878050196f;

    /// <summary>The 27 fixed probe directions native iterates
    /// (<c>shipIGC.cpp:479-512</c>, <c>c_maxAttempts</c>).</summary>
    private static readonly Vector3[] Directions =
    [
        new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f), new(0f, 0f, 1f), new(0f, 0f, -1f),

        new(Sqrt1o2, Sqrt1o2, 0f), new(-Sqrt1o2, Sqrt1o2, 0f), new(Sqrt1o2, -Sqrt1o2, 0f), new(-Sqrt1o2, -Sqrt1o2, 0f),

        new(Sqrt1o2, 0f, Sqrt1o2), new(-Sqrt1o2, 0f, Sqrt1o2), new(Sqrt1o2, 0f, -Sqrt1o2), new(-Sqrt1o2, 0f, -Sqrt1o2),

        new(0f, Sqrt1o2, Sqrt1o2), new(0f, -Sqrt1o2, Sqrt1o2), new(0f, Sqrt1o2, -Sqrt1o2), new(0f, -Sqrt1o2, -Sqrt1o2),

        new(Sqrt1o3, Sqrt1o3, Sqrt1o3), new(-Sqrt1o3, Sqrt1o3, Sqrt1o3), new(Sqrt1o3, -Sqrt1o3, Sqrt1o3), new(-Sqrt1o3, -Sqrt1o3, Sqrt1o3),
        new(Sqrt1o3, Sqrt1o3, -Sqrt1o3), new(-Sqrt1o3, Sqrt1o3, -Sqrt1o3), new(Sqrt1o3, -Sqrt1o3, -Sqrt1o3), new(-Sqrt1o3, -Sqrt1o3, -Sqrt1o3),
    ];

    /// <summary>
    /// Per-pass immutable support offsets for the 27 fixed separation probes.
    /// Identical hull/scale/relative-orientation pairs share offsets, but every
    /// pair still evaluates its own positions and the native probe order. Seed
    /// normals and coincident-centre random perturbations are never cached.
    /// </summary>
    public sealed class ProbeCache
    {
        private readonly Dictionary<Key, (Vector3[] A, Vector3[] B)> _offsets = [];

        // P706 — per-body cache of the 27 body-2 (GetMaxExtreme) support
        // vertices. GetMaxExtreme(hts, d) is a pure function of the collider's
        // hull/shape/scale/radius and the FIXED probe direction d — it never
        // involves the body's orientation (Collider.GetMaxExtreme →
        // GetHullExtreme is hull-local × scale). The pair-keyed _offsets cache
        // recomputes these 27 supports for every DISTINCT pair, but in a dense
        // fleet of few hull types (e.g. one fighter class relaunching in a
        // mass) the same body's 27 supports repeat across thousands of pairs.
        // Keying them by the body alone computes them once per unique body and
        // is byte-for-byte the same support values — a pure memoisation, no
        // gameplay change. (The body-1 `aa` terms depend on the relative
        // orientation o, so they stay pair-keyed.)
        private readonly Dictionary<BKey, Vector3[]> _bb = [];
        private readonly record struct BKey(CvhFile? Hull, int Hts, int Scale, int Radius);

        internal Vector3[] GetB(Collider b, int htsB)
        {
            var key = new BKey(b.Hull, htsB,
                BitConverter.SingleToInt32Bits(b.Scale),
                BitConverter.SingleToInt32Bits(b.EffectiveRadius));
            if (!_bb.TryGetValue(key, out var arr))
            {
                arr = new Vector3[Directions.Length];
                for (int i = 0; i < Directions.Length; i++)
                    arr[i] = b.GetMaxExtreme(htsB, Directions[i]);
                _bb.Add(key, arr);
            }
            return arr;
        }

        private readonly record struct Bits(int X, int Y, int Z)
        {
            public static Bits Of(Vector3 v) => new(BitConverter.SingleToInt32Bits(v.X),
                BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
        }
        private readonly record struct Key(CvhFile? HullA, CvhFile? HullB, int ShapeA, int ShapeB,
            int ScaleA, int ScaleB, int RadiusA, int RadiusB, Bits Right, Bits Up, Bits Backward);

        internal (Vector3[] A, Vector3[] B)? Get(Collider a, int htsA, Collider b, int htsB, Orientation o)
        {
            var key = new Key(a.Hull, b.Hull, htsA, htsB,
                BitConverter.SingleToInt32Bits(a.Scale), BitConverter.SingleToInt32Bits(b.Scale),
                BitConverter.SingleToInt32Bits(a.EffectiveRadius), BitConverter.SingleToInt32Bits(b.EffectiveRadius),
                Bits.Of(o.GetRight()), Bits.Of(o.GetUp()), Bits.Of(o.GetBackward()));
            if (_offsets.TryGetValue(key, out var offsets)) return offsets;
            // A fleet with many distinct attitudes must not create an unbounded
            // per-pair support cache. Fall back to the original solver once full.
            if (_offsets.Count >= 256) return null;
            var aa = new Vector3[Directions.Length]; var bb = new Vector3[Directions.Length];
            for (int i = 0; i < Directions.Length; i++)
            {
                var d = Directions[i];
                aa[i] = htsA switch
                {
                    HitShape.Point => Vector3.Zero,
                    HitShape.Sphere => d * a.EffectiveRadius,
                    HitShape.Ellipse => a.GetEllipseExtreme(o.TimesInverse(-d)) * o,
                    _ => a.GetHullExtreme(htsA, o.TimesInverse(-d)) * o
                };
                bb[i] = b.GetMaxExtreme(htsB, d);
            }
            _offsets.Add(key, offsets = (aa, bb));
            return offsets;
        }
    }

    /// <summary>
    /// The separated collision-time positions of the two bodies and the world-space
    /// separating normal, computed from their convex hulls. Body-1 moves (or is
    /// pushed full-distance when <c>fEqualBounce</c> is false); body-2 is static
    /// unless <c>fEqualBounce</c> (in which case both are displaced half the
    /// distance — native <c>fEqualBounce</c>, <c>shipIGC.cpp:524-526,610-615</c>).
    /// </summary>
    public readonly record struct Result(Vector3 Position1, Vector3 Position2, Vector3 Normal);

    /// <param name="a">Body 1 (the mover; the ship in ship↔static pairs).</param>
    /// <param name="b">Body 2 (the stationary rock/station, or the second ship when <c>fEqualBounce</c>).</param>
    /// <param name="tCollision">Step-relative collision time (<c>entry.m_tCollision</c>).</param>
    /// <param name="fEqualBounce">Both bodies bounce (ship↔ship); else only body-1 moves.</param>
    /// <param name="htsA"><paramref name="a"/>'s concrete sub-shape from detection (<c>entry.m_hts1</c>).</param>
    /// <param name="htsB"><paramref name="b"/>'s concrete sub-shape (<c>entry.m_hts2</c>).</param>
    public static Result SeparateColliders(Collider a, Collider b, float tCollision, bool fEqualBounce, int htsA, int htsB, ProbeCache? probeCache = null)
    {
        // Collision-time positions (shipIGC.cpp:523-526): body-2 only moves when
        // equal-bounce; a static body stays at its position.
        Vector3 position1 = a.Position + a.Velocity * tCollision;
        Vector3 position2 = fEqualBounce ? b.Position + b.Velocity * tCollision : b.Position;

        Vector3 dP = position1 - position2;
        if (dP.X == 0f && dP.Y == 0f && dP.Z == 0f)
        {
            dP = RandomDirection();
            position1 += dP;
        }

        var oA = a.Orientation;
        var oB = b.Orientation;
        var o = ComposeInverse(oA, oB);         // o = oA · oB⁻¹  (A's attitude in B's frame)
        Vector3 dpLocal = oB.TimesInverse(dP);

        // Seed the best direction with the centre line, hull-center-adjusted
        // (shipIGC.cpp:545-551).
        Vector3 directionBest = dpLocal;
        if (htsA >= HitShape.ConvexHullMin) directionBest += a.GetCenter(htsA) * o;
        if (htsB >= HitShape.ConvexHullMin) directionBest -= b.GetCenter(htsB);
        directionBest = directionBest.Normalize();

        Vector3 eA = a.GetMinExtreme(htsA, dpLocal, o, directionBest);
        Vector3 eB = b.GetMaxExtreme(htsB, directionBest);
        Vector3 delta = eA - eB;
        double distanceBest = delta * directionBest;

        var offsets = probeCache?.Get(a, htsA, b, htsB, o);
        int attempt = 0;
        do
        {
            var direction = Directions[attempt];

            Vector3 eAa = offsets is { } cached ? htsA switch
            {
                HitShape.Point => dpLocal,
                HitShape.Sphere => dpLocal - cached.A[attempt],
                _ => dpLocal + cached.A[attempt]
            } : a.GetMinExtreme(htsA, dpLocal, o, direction);
            Vector3 eBb = offsets is { } cachedB
                ? cachedB.B[attempt]
                : probeCache is { } pcB ? pcB.GetB(b, htsB)[attempt] : b.GetMaxExtreme(htsB, direction);
            Vector3 deltaA = eAa - eBb;
            double distance = deltaA * direction;

            if (distance >= 0.0)
            {
                // A true separating plane: use it and minimise the displacement.
                directionBest = direction;
                distanceBest = 0.0;
                break;
            }

            if (distance > distanceBest)
            {
                directionBest = direction;
                distanceBest = distance;
            }
        }
        while (++attempt < Directions.Length);

        Vector3 normal = directionBest * oB;    // *pNormal = directionBest · o2  (shipIGC.cpp:603)

        // Displace so the extreme points no longer intersect, plus native's 0.5
        // fudge (s = distanceBest − 0.5, shipIGC.cpp:608).
        float s = (float)(distanceBest - 0.5);
        position1 -= (s * (fEqualBounce ? 0.5f : 1.0f)) * normal;

        if (fEqualBounce)
            position2 += (s * 0.5f) * normal;

        return new Result(position1, position2, normal);
    }

    /// <summary><c>o1.TimesInverse(o2)</c> — the orientation of body-1 in body-2's
    /// frame; the same composition the narrow phase's <c>HullCollide</c> builds
    /// (<c>NarrowPhase.CompileInverse</c>).</summary>
    private static Orientation ComposeInverse(Orientation a, Orientation b)
    {
        var o = new Orientation();
        o.SetRight(b.TimesInverse(a.GetRight()));
        o.SetUp(b.TimesInverse(a.GetUp()));
        o.SetForward(-b.TimesInverse(a.GetBackward()));
        return o;
    }

    /// <summary>A uniform random unit vector — native <c>Vector::RandomDirection()</c>
    /// (<c>zlib/vector.cpp:37-46</c>), used when the centres coincide.</summary>
    private static Vector3 RandomDirection()
    {
        double u = Random.Shared.NextDouble() * 2.0 - 1.0;
        double phi = Random.Shared.NextDouble() * 2.0 * Math.PI;
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
        return new Vector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)u);
    }
}
