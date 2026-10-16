using System.Runtime.CompilerServices;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Collision;

/// <summary>
/// A single object's collision body — the port of the C++
/// <c>BoundingHull</c>/<c>BoundingSphere</c> (<c>src/_Utility/HitTest.cpp</c>).
/// Carries the state the narrow phase reads: position, velocity, orientation,
/// the (possibly multi-)hull, the effective bounding radius, and the per-step
/// swept axis-aligned bounding box computed by <see cref="SetStep"/> (the C++
/// <c>HitTest::SetBB/UpdateBB</c>, <c>HitTest.cpp:14-110</c>).
/// </summary>
/// <remarks>
/// Faithful scalars: <see cref="OriginalRadius"/> is the file's <c>radius</c>;
/// <see cref="Scale"/> = <c>desiredRadius / originalRadius</c> (<c>HitTest.cpp:571</c>);
/// <see cref="EffectiveRadius"/> is the bounding-sphere radius the swept AABB uses
/// (<c>m_radius</c>). A hull's support vertices are used at file scale times
/// <see cref="Scale"/> — so with <c>desiredRadius == originalRadius</c> the hull
/// is used at file scale, and the (non-enclosing) radius governs the AABB only.
/// <see cref="TrueShape"/> equals the number of sub-hulls when a hull is present
/// (a convex-hull index), otherwise the fallback primitive shape.
/// </summary>
public sealed class Collider
{
    private readonly List<HullSupport> _supports = [];
    private int _shape;

    /// <summary>Create a collider from the given state. A <c>null</c> hull means a primitive (default sphere).</summary>
    public Collider(object? data, Vector3 position, Vector3 velocity, Orientation orientation, CvhFile? hull,
        float desiredRadius, bool isStatic, int fallbackShape = HitShape.Sphere)
    {
        Data = data;
        Position = position;
        Velocity = velocity;
        Orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
        Hull = hull;
        IsStatic = isStatic;

        if (hull is null)
        {
            OriginalRadius = 0f;
            DesiredRadius = desiredRadius;
            Scale = 1f;
            EffectiveRadius = desiredRadius;
            EllipseEquation = Vector3.Zero;
            TrueShape = fallbackShape;
        }
        else
        {
            OriginalRadius = hull.Radius;
            DesiredRadius = desiredRadius;
            Scale = OriginalRadius > 0f ? desiredRadius / OriginalRadius : 1f;
            // Default shape is the sphere primitive; the caller may switch to a
            // convex-hull sub-shape or the ellipse via SetShape/SetRadius.
            EffectiveRadius = desiredRadius;
            EllipseEquation = Scale * hull.EllipseEquation;
            TrueShape = hull.Hulls.Count;
            // Share the per-hull support machinery across every collider built
            // from the same (cached) CvhFile — the support set is a pure function
            // of the parsed hull (vertex array + adjacency + octant extremes) and
            // is immutable after build, so building it once per model (per name,
            // via CollisionHullCache) instead of once per collider per tick keeps
            // the per-step collision cost flat as the field population grows.
            _supports.AddRange(SupportCache.GetValue(hull, h => BuildSupports(h)));
        }

        Shape = TrueShape;
    }

    /// <summary>Shared per-<see cref="CvhFile"/> support geometry, keyed by
    /// reference (a class ⇒ reference equality). Entries are collected when the
    /// <c>CvhFile</c> they key off is; values are immutable, so sharing is safe.</summary>
    private static readonly ConditionalWeakTable<CvhFile, IReadOnlyList<HullSupport>> SupportCache = new();

    private static IReadOnlyList<HullSupport> BuildSupports(CvhFile hull)
    {
        var supports = new List<HullSupport>(hull.Hulls.Count);
        for (var i = 0; i < hull.Hulls.Count; i++)
            supports.Add(HullSupport.Build(hull.Hulls[i]));
        return supports;
    }

    /// <summary>The attached data object (a <c>Model</c>), used for identity in reports.</summary>
    public object? Data { get; }

    /// <summary>Stable ordering ordinal, assigned by the detector in input order; used for deterministic tie-breaking.</summary>
    public int Ordinal { get; set; }

    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public Orientation Orientation { get; set; }
    public CvhFile? Hull { get; }

    /// <summary>Whether the body is static: its swept AABB does not travel this step.</summary>
    public bool IsStatic { get; }

    /// <summary>The file's original bounding-sphere radius (<c>m_originalRadius</c>).</summary>
    public float OriginalRadius { get; }

    /// <summary>The game-set bounding radius (<c>m_desiredRadius</c>); the scale base.</summary>
    public float DesiredRadius { get; }

    /// <summary><c>m_scale = desiredRadius / originalRadius</c> (<c>HitTest.cpp:571</c>); every hull vertex read is scaled by it.</summary>
    public float Scale { get; }

    /// <summary>The bounding-sphere radius the swept AABB uses (<c>m_radius</c>).</summary>
    public float EffectiveRadius { get; private set; }

    /// <summary>The ellipse-equation vector, scaled by <c>m_scale</c> (<c>HitTest.cpp:572</c>).</summary>
    public Vector3 EllipseEquation { get; private set; }

    /// <summary>The collider's true (most detailed) shape.</summary>
    public int TrueShape { get; }

    /// <summary>The collider's current working shape (may be reduced from <see cref="TrueShape"/>).</summary>
    public int Shape { get => _shape; set => SetShapeInternal(value); }

    /// <summary>Step start; set by <see cref="SetStep"/>.</summary>
    public Vector3 StepStart { get; private set; }

    /// <summary>Step end; set by <see cref="SetStep"/>.</summary>
    public Vector3 StepStop { get; private set; }

    /// <summary>Swept AABB minima for the current step.</summary>
    public Vector3 BoundsMin { get; private set; }

    /// <summary>Swept AABB maxima for the current step.</summary>
    public Vector3 BoundsMax { get; private set; }

    /// <summary>
    /// Compute the swept bounding box over <c>[tStart, tStop]</c> relative to the
    /// current <see cref="Position"/> — the C++ <c>SetBB</c>/<c>UpdateBB</c>
    /// (<c>HitTest.cpp:14-110</c>). A static body stays at its position.
    /// </summary>
    public void SetStep(float tStart, float tStop)
    {
        StepStart = Position;
        StepStop = IsStatic ? Position : Position + Velocity * (tStop - tStart);

        float radius = EffectiveRadius;
        float minX = MathF.Min(StepStart.X, StepStop.X) - radius, maxX = MathF.Max(StepStart.X, StepStop.X) + radius;
        float minY = MathF.Min(StepStart.Y, StepStop.Y) - radius, maxY = MathF.Max(StepStart.Y, StepStop.Y) + radius;
        float minZ = MathF.Min(StepStart.Z, StepStop.Z) - radius, maxZ = MathF.Max(StepStart.Z, StepStop.Z) + radius;
        BoundsMin = new Vector3(minX, minY, minZ);
        BoundsMax = new Vector3(maxX, maxY, maxZ);
    }

    /// <summary>
    /// Cap the working shape at the true shape (the C++ <c>SetShape</c>,
    /// <c>HitTest.cpp:552-566</c>); switching to the ellipse recomputes the
    /// effective radius with the ellipse-radius multiplier.
    /// </summary>
    private void SetShapeInternal(int shape)
    {
        int s = shape <= TrueShape ? shape : TrueShape;
        if (s != _shape)
        {
            _shape = s;
            EffectiveRadius = s == HitShape.Ellipse && Hull is not null
                ? DesiredRadius * Hull.EllipseRadiusMultiplier
                : DesiredRadius;
        }
    }

    /// <summary><c>BoundingHull::GetCenter(hts)</c> (<c>HitTest.cpp:461-465</c>) — sub-hull center times <see cref="Scale"/>.</summary>
    public Vector3 GetCenter(int hts)
    {
        AssertHullShape(hts);
        return Hull!.Hulls[hts].Center * Scale;
    }

    /// <summary>
    /// The support (extreme) point of a convex-hull sub-shape in the collider's
    /// own (hull-local) coordinate space, times <see cref="Scale"/> — the C++
    /// <c>BoundingHull::GetHullExtreme</c> (<c>HitTest.cpp:495-549</c>), a hill
    /// climb over the parsed adjacency from a pre-computed octant extreme.
    /// </summary>
    public Vector3 GetHullExtreme(int hts, Vector3 direction)
    {
        AssertHullShape(hts);
        return _supports[hts].Extreme(direction) * Scale;
    }

    /// <summary><c>HitTest::GetEllipseExtreme</c> (<c>HitTest.cpp:467-492</c>) — the ellipse support in the given direction.</summary>
    public Vector3 GetEllipseExtreme(Vector3 direction)
    {
        double a2 = EllipseEquation.X * EllipseEquation.X;
        double b2 = EllipseEquation.Y * EllipseEquation.Y;
        double c2 = EllipseEquation.Z * EllipseEquation.Z;
        double rk = 1.0 / Math.Sqrt(a2 * direction.X * direction.X +
                                    b2 * direction.Y * direction.Y +
                                    c2 * direction.Z * direction.Z);
        return new Vector3(
            (float)(direction.X * a2 * rk),
            (float)(direction.Y * b2 * rk),
            (float)(direction.Z * c2 * rk));
    }

    /// <summary><c>HitTest::GetMinExtreme</c> (<c>HitTest.cpp:865-887</c>).</summary>
    public Vector3 GetMinExtreme(int hts, Vector3 position, Orientation orientation, Vector3 direction)
    {
        switch (hts)
        {
            case HitShape.Point:
                return position;
            case HitShape.Ellipse:
                return position + GetEllipseExtreme(orientation.TimesInverse(-direction)) * orientation;
            case HitShape.Cone:
                throw new NotSupportedException("cone shapes are not implemented in this slice");
            case HitShape.Sphere:
                return position - direction * EffectiveRadius;
            default:
                return position + GetHullExtreme(hts, orientation.TimesInverse(-direction)) * orientation;
        }
    }

    /// <summary><c>HitTest::GetMaxExtreme</c> (<c>HitTest.cpp:889-909</c>).</summary>
    public Vector3 GetMaxExtreme(int hts, Vector3 direction)
    {
        switch (hts)
        {
            case HitShape.Point:
                return Vector3.Zero;
            case HitShape.Ellipse:
                return GetEllipseExtreme(direction);
            case HitShape.Cone:
                throw new NotSupportedException("cone shapes are not implemented in this slice");
            case HitShape.Sphere:
                return direction * EffectiveRadius;
            default:
                return GetHullExtreme(hts, direction);
        }
    }

    private void AssertHullShape(int hts)
    {
        if (Hull is null || hts < HitShape.ConvexHullMin || hts >= Hull.Hulls.Count)
            throw new ArgumentOutOfRangeException(nameof(hts), $"shape {hts} is not a valid convex-hull sub-index (hulls={Hull?.Hulls.Count.ToString() ?? "none"})");
    }

    /// <summary>
    /// One sub-hull's support machinery: the pre-computed 8 octant extreme
    /// vertices (the C++ <c>SingleHull::CalculateExtremeVertices</c>,
    /// <c>HitTest.cpp:311-357</c>) plus the hill-climb from an octant warm start
    /// (the body of <c>BoundingHull::GetHullExtreme</c>, <c>HitTest.cpp:505-548</c>).
    /// Deliberately deterministic: no temporal-coherence cache, pure function of
    /// the parsed vertices and adjacency.
    /// </summary>
    private sealed class HullSupport
    {
        private readonly Vector3[] _vertices;
        private readonly float[] _x, _y, _z;
        private readonly int[][] _adjacency;
        private readonly int[] _octantExtremes = new int[8];

        private HullSupport(CvhHull hull)
        {
            _vertices = hull.Vertices.Select(v => v.Position).ToArray();
            _x = _vertices.Select(v => v.X).ToArray();
            _y = _vertices.Select(v => v.Y).ToArray();
            _z = _vertices.Select(v => v.Z).ToArray();
            _adjacency = hull.Vertices.Select(v => v.AdjacentIndices).ToArray();
            ComputeOctantExtremes();
        }

        public static HullSupport Build(CvhHull hull) => new(hull);

        private void ComputeOctantExtremes()
        {
            // Eight axis-aligned octant directions (+/− on each axis), in the
            // same bit layout Extreme() later packs from the direction's signs.
            for (int o = 0; o < 8; o++)
            {
                var dir = new Vector3((o & 1) != 0 ? 1f : -1f, (o & 2) != 0 ? 1f : -1f, (o & 4) != 0 ? 1f : -1f);
                _octantExtremes[o] = Climb(ArgMax(dir), dir);
            }
        }

        private int ArgMax(Vector3 dir)
        {
            int best = 0;
            float dotMax = dir * _vertices[0];
            for (int i = 1; i < _vertices.Length; i++)
            {
                float d = dir * _vertices[i];
                if (d > dotMax) { dotMax = d; best = i; }
            }
            return best;
        }

        /// <summary>Hill-climb to the support vertex: start at <paramref name="start"/> and move while a neighbour dots higher — the C++ GetHullExtreme loop (<c>HitTest.cpp:517-548</c>). Terminates at the global support on a convex hull.</summary>
        private int Climb(int start, Vector3 dir)
        {
            // Reuse direction components throughout the support walk. Keep
            // the native float multiply/add order and strict improvement/tie
            // rule; no vector temporaries or accessor calls in the inner loop.
            float dx = dir.X, dy = dir.Y, dz = dir.Z;
            var pcv = start;
            while (true)
            {
                float dotMax = dx * _x[pcv] + dy * _y[pcv] + dz * _z[pcv];
                int pcvNext = -1;
                foreach (var adj in _adjacency[pcv])
                {
                    float dot = dx * _x[adj] + dy * _y[adj] + dz * _z[adj];
                    if (dot > dotMax) { dotMax = dot; pcvNext = adj; }
                }
                if (pcvNext < 0) return pcv;
                pcv = pcvNext;
            }
        }

        /// <summary>The support vertex in <paramref name="direction"/> (direction length irrelevant — only sign/ratios matter).</summary>
        public Vector3 Extreme(Vector3 direction)
        {
            // Octant index: bit 0 = x>=0, bit 1 = y>=0, bit 2 = z>=0 — the C++
            // GetExtremeVertex sign packing (HitTest.cpp:304-309).
            int idx = (direction.X < 0f ? 0 : 1)
                    + (direction.Y < 0f ? 0 : 2)
                    + (direction.Z < 0f ? 0 : 4);
            return _vertices[Climb(_octantExtremes[idx], direction)];
        }
    }
}
