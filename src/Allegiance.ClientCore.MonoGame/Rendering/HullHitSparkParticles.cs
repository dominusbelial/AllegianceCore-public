using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single live impact-spark bit as the pool enumerates it for the
/// renderer. <see cref="Position"/> is the bit's fully-integrated current world
/// position (<c>origin + velocity·age</c> — native bits fly at constant velocity,
/// BitsGeo Render, efgeo.cpp:836); <see cref="Velocity"/> is the bit's (constant)
/// initial velocity — surfaced so unit tests can assert the native launch speed
/// and spread exactly (the renderer reads only Position / Size / RemainingFrac);
/// <see cref="Size"/> is the native <c>m_size</c> (world units) the renderer
/// draws; <see cref="RemainingFrac"/> is <c>(lifetime − age)/lifetime</c> in
/// (0, 1], driving the bright fade.</summary>
public readonly record struct HullHitSparkView(
    Vector3 Position,
    Vector3 Velocity,
    float Size,
    float RemainingFrac);

/// <summary>
/// The pure, MonoGame-free particle pool for the projectile-impact spark
/// (P364) — a faithful port of native <c>ThingGeo::AddHullHit</c>'s debris-bits
/// half (<c>effect/thinggeo.cpp:1324-1345</c>). It reuses the exhaust-pool
/// conventions of <see cref="DamageDebrisParticles"/> / <see cref="MissileTrailParticles"/>
/// (fixed-capacity ring buffer, an <see cref="ITrailRandom"/> randomness seam, a
/// pure Enumerate view), but reproduces the native bits math exactly: on every
/// qualifying weapon hit, two small debris <b>bits</b> launch from the exact
/// impact position, roughly along the hit-surface normal with a ±0.25 random
/// per-axis spread, sized <c>radius/2</c>, at <c>0.4·size</c> units/s, flying at
/// constant velocity for the native 2&#8209;s bit lifetime.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (thinggeo.cpp:1324-1345).</b> <c>AddHullHit</c>
/// computes <c>size = m_radius / 2</c>, <c>speed = 0.4·size</c>,
/// <c>spread = 0.25</c>, then for <c>index &lt; 2</c> calls
/// <c>m_pbitsGeo-&gt;AddBit(vecWorldPosition, speed·(normal + random(−spread,spread)
/// per axis), size)</c> where <c>vecWorldPosition = m_vecPosition + vecPosition</c>
/// (the target's world position + the local impact offset = the world impact
/// point). Each <c>BitsGeoImpl</c> bit (<c>efgeo.cpp:798-846</c>) flies straight
/// at constant velocity for <c>g_bitsLifeTime = 2</c> s and is drawn scaled to
/// <c>m_size</c>. This port therefore spawns two bits per hit at the world impact
/// point with that exact velocity/size/lifetime.</para>
/// <para><b>Deliberate port substitutions (all documented).</b> (1) The native
/// <c>AddHullHit</c> also drives a <b>second</b> component — the
/// <c>m_phullHitGeo</c> decal (a short-lived white tangent/normal surface decal,
/// <c>HullHitGeoImpl::Render</c>, <c>efgeo.cpp:674-749</c>). That paints on the
/// struck model's mesh surface; this billboard-only particle pipeline has no
/// projected-decal pass, so the ported visual is the bits (the prominent "two
/// small debris bits" the task names), and the decal is documented (not ported)
/// in the P364 writeup. (2) The native bit also spins (rotate about a random
/// axis at <c>random(π/2, 2π)·dtime</c>, efgeo.cpp:813/841); for a sub-meter
/// billboard dot the spin is imperceptible, so it is dropped. (3) Native
/// <c>m_radius</c> is the struck ship's collision radius; the port receives it on
/// the wire (<see cref="Allegiance.Core.Wire.ProjectileHullHit.Radius"/>) since
/// the client has no static ship-type core to resolve it.</para>
/// </remarks>
public sealed class HullHitSparkParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary>Bit lifetime <c>g_bitsLifeTime = 2</c> s (efgeo.cpp:764).</summary>
    public const float LifetimeSeconds = 2f;

    /// <summary>Velocity scale <c>speed = 0.4 · size</c> (thinggeo.cpp:1329).</summary>
    public const float SpeedFactor = 0.4f;

    /// <summary>Per-axis launch spread <c>spread = 0.25</c> (thinggeo.cpp:1330);
    /// each bit's direction is <c>normal + random(−spread, spread)</c> per axis.</summary>
    public const float Spread = 0.25f;

    /// <summary>Number of debris bits per hit — the native <c>index &lt; 2</c>
    /// loop (thinggeo.cpp:1334).</summary>
    public const int BitsPerHit = 2;

    /// <summary>Fallback for the struck target's radius — used only when the
    /// wire's <see cref="Allegiance.Core.Wire.ProjectileHullHit.Radius"/> is
    /// unknown (≤ 0), so the bits still render rather than vanishing at size 0.
    /// Mirrors <see cref="DamageDebrisParticles.ShipRadiusDefault"/>.</summary>
    public const float ShipRadiusDefault = 50f;

    /// <summary>Default fixed pool capacity (2 bits per hit; a sustained fight
    /// holds well under this).</summary>
    public const int DefaultCapacity = 512;

    private struct Slot
    {
        public Vector3 Origin;    // native m_vecPosition (impact point)
        public Vector3 Velocity;  // native m_dvecPosition (constant velocity)
        public float Size;        // native m_size (radius / 2)
        public float AgeSec;
    }

    private readonly Slot[] _buffer;
    private readonly Slot[] _scratch;   // same length; compaction scratch in Update
    private int _start;
    private int _count;

    /// <summary>Construct an empty pool. <paramref name="capacity"/> must be &gt; 0.</summary>
    public HullHitSparkParticles(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        _buffer = new Slot[capacity];
        _scratch = new Slot[capacity];
    }

    /// <summary>Fixed pool capacity (upper bound on <see cref="ActiveCount"/>).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of currently live spark bits in the pool.</summary>
    public int ActiveCount => _count;

    /// <summary>
    /// Emit one native <c>AddHullHit</c> debris pair (thinggeo.cpp:1324-1345):
    /// exactly two bits launched from <paramref name="impact"/> roughly along
    /// <paramref name="normal"/> (per-axis ±<see cref="Spread"/>), sized
    /// <c><paramref name="radius"/> / 2</c>, at <c>0.4·size</c> units/s.
    /// </summary>
    /// <param name="impact">The world impact point (native
    /// <c>vecWorldPosition</c>, thinggeo.cpp:1332).</param>
    /// <param name="normal">The world hit-surface normal (native <c>vecNormal</c>;
    /// from the wire, = normalize(position1 − position2), projectileIGC.cpp:225).</param>
    /// <param name="radius">The struck target's collision radius (native
    /// <c>m_radius</c>; half it to get the bit size).</param>
    /// <param name="rng">The randomness seam (native <c>random(a,b)</c>).</param>
    public void Emit(Vector3 impact, Vector3 normal, float radius, ITrailRandom rng)
    {
        float size = radius * 0.5f;                  // thinggeo.cpp:1328
        float speed = SpeedFactor * size;            // thinggeo.cpp:1329

        for (int index = 0; index < BitsPerHit; index++)
        {
            // speed · (normal + random(−spread,spread) per axis) — thinggeo.cpp:1337-1340
            var velocity = new Vector3(
                normal.X + rng.Range(-Spread, Spread),
                normal.Y + rng.Range(-Spread, Spread),
                normal.Z + rng.Range(-Spread, Spread)) * speed;

            Add(new Slot
            {
                Origin = impact,
                Velocity = velocity,
                Size = size,
                AgeSec = 0f,
            });
        }
    }

    /// <summary>
    /// Age every live bit one frame and expire those past the native bit
    /// lifetime (<c>g_bitsLifeTime = 2</c>). Position is <em>not</em> accumulated —
    /// native renders each frame from the stored initial position/velocity via the
    /// straight-line integral (BitsGeo Render, efgeo.cpp:836), so
    /// <see cref="Enumerate"/> computes the exact current position rather than a
    /// drifting Euler sum.
    /// </summary>
    public void Update(float dtSec)
    {
        if (_count == 0) return;
        int k = 0;
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            s.AgeSec += dtSec;
            if (s.AgeSec < LifetimeSeconds)
                _scratch[k++] = s;
        }
        for (int i = 0; i < k; i++) _buffer[i] = _scratch[i];
        _start = 0;
        _count = k;
    }

    /// <summary>
    /// Enumerate the live bits oldest-first, each at its fully-integrated current
    /// world position: <c>origin + velocity·age</c> (native BitsGeo Render,
    /// efgeo.cpp:836), with the remaining-lifetime fraction for the bright fade.
    /// </summary>
    public IEnumerable<HullHitSparkView> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            float age = s.AgeSec;
            var pos = s.Origin + (s.Velocity * age);
            float rem = (LifetimeSeconds - s.AgeSec) / LifetimeSeconds;
            if (rem < 0f) rem = 0f;
            yield return new HullHitSparkView(pos, s.Velocity, s.Size, rem);
        }
    }

    /// <summary>Drop every live bit (used by tests to start clean).</summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>Append a bit to the ring, evicting the oldest when full.</summary>
    private void Add(Slot s)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = s;
            _count++;
        }
        else
        {
            _buffer[_start] = s;
            _start = (_start + 1) % _buffer.Length;
        }
    }
}
