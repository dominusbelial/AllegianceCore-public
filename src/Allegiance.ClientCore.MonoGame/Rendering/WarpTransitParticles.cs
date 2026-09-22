using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single live teleport spark as the pool enumerates it for the
/// renderer. <see cref="Position"/> is the particle's fully-integrated current
/// world position (native integrates with acceleration each render frame);
/// <see cref="Velocity"/> / <see cref="Acceleration"/> are the particle's
/// (constant) initial values — the native <c>m_vecVelocity</c> /
/// <c>m_vecAcceleration</c> — surfaced so unit tests can assert the faithful
/// ship-velocity inheritance and anti-velocity deceleration directly (the
/// renderer reads only Position / Size / RemainingFrac).
/// <see cref="RemainingFrac"/> is <c>(duration − age) / duration</c> in (0, 1],
/// the native SparkBehavior countdown that drives the hot-blue fade.</summary>
public readonly record struct WarpSparkView(
    Vector3 Position,
    Vector3 Velocity,
    Vector3 Acceleration,
    float Size,
    float RemainingFrac);

/// <summary>
/// The pure, MonoGame-free particle pool for the warp/ripcord transit spark
/// spray (P296) — a faithful port of native <c>AddTeleportParticles</c>
/// (<c>effect/thinggeo.cpp:619-696</c>). It reuses the exhaust-pool conventions
/// of <see cref="MissileTrailParticles"/> (fixed-capacity ring buffer, an
/// <see cref="ITrailRandom"/> randomness seam, a pure Enumerate view for the
/// renderer) but reproduces the native teleport-spark math exactly, including
/// the parts the exhaust path lacks: particle <b>acceleration</b>
/// (<c>-vecFrameVelocity</c>) and the velocity-derived spray axes.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (thinggeo.cpp:619-696).</b> Native runs
/// <c>AddTeleportParticles</c> <b>continuously during the ripcord/aleph lead-up</b>,
/// gated on <c>m_fTimeUntilRipcord</c>/<c>m_fTimeUntilAleph ≥ 0</c>, emitting a
/// per-frame burst whose count ramps linearly up to the maximum as transit
/// approaches. The ramp factor is
/// <c>fTimeToShowSparks = clamp(fTimeUntilTeleport / 3.0f, 0, 1)</c> and the
/// count is
/// <c>floor(TELEPORT_SPARK_COUNT · s_iShowSmoke · (1 − fTimeToShowSparks))</c>.
/// The port observes only the transit <em>instant</em> (the same
/// <c>SET_CLUSTER</c> sector-change edge <see cref="Flight.WarpTransitFlash"/>
/// detects), so <see cref="EmitBurst"/> emits one burst with the peak
/// transit-instant parameters — <c>fTimeToShowSparks = 0</c>,
/// <c>fCountUpToOne = 1</c> — which is exactly what a single native
/// <c>AddTeleportParticles</c> call would emit on the final frame before the
/// countdown goes negative. Every native constant and formula below is kept;
/// the only deliberate substitutions are noted inline.</para>
/// <para><b>Spray axes.</b> Native builds the spray basis from the ship's real
/// velocity at the moment of transit: <c>vecZ = normalize(velocity)</c>,
/// <c>vecX = vecZ.GetOrthogonalVector()</c>, <c>vecY = cross(vecX, vecZ)</c>;
/// it falls back to the hull orientation when stationary (thinggeo.cpp:632-647).
/// Each spark's blast vector is a random unit direction scaled to
/// <c>radius · random(0.75, 1.25)</c>, then (at transit, since
/// <c>fTimeToShowSparks = 0</c>) left as the full 3D blast; the particle's
/// velocity inherits the ship's frame velocity, and its acceleration opposes it
/// (<c>-vecFrameVelocity</c>), so the spray streams outward and then pulls
/// back — native's exact kinematics.</para>
/// <para><b>Particle sizing stand-in.</b> Native scales particle size to
/// <c>(0.45…0.9) · m_radius</c> and blast speed to <c>(0.75…1.25) · m_radius</c>.
/// The client wire carries no ship radius and the client has no static ship-type
/// core (the same gap <see cref="MissileTrailParticles.MissileRadiusDefault"/>
/// bridges), so <see cref="ShipRadiusDefault"/> stands in for <c>m_radius</c> —
/// a documented approximation, exactly the convention of the missile trail.</para>
/// </remarks>
public sealed class WarpTransitParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary><c>#define TELEPORT_SPARK_COUNT 3</c> (thinggeo.cpp:223) — the
    /// per-emission spark ceiling before the quality multiplier.</summary>
    public const int TeleportSparkCount = 3;

    /// <summary>The default quality multiplier <c>s_iShowSmoke</c> (= 1).
    /// Native multiplies the spark count by this (thinggeo.cpp:659); the port
    /// uses the default so one burst emits TeleportSparkCount sparks.</summary>
    public const float ShowSmoke = 1f;

    /// <summary>The lead-up ramp span, seconds — <c>fTimeToShowSparks</c> ramps
    /// 1 → 0 as the transit approaches over the final 3 s (thinggeo.cpp:651).</summary>
    public const float LeadTimeSeconds = 3.0f;

    /// <summary>Each spark's lifetime, seconds — <c>m_fDuration = 0.5f</c>
    /// (thinggeo.cpp:689).</summary>
    public const float SparkDurationSeconds = 0.5f;

    /// <summary>Fallback for the ship's model radius <c>m_radius</c> — used only
    /// when the transiting ship's real hull radius (<c>0.5 × HullType.Length</c>,
    /// now computable post-P333 and threaded in by Game1) is unknown. Everything
    /// native sizes off <c>m_radius</c> — particle size and blast speed — scales
    /// off this documented number.</summary>
    public const float ShipRadiusDefault = 50f;

    /// <summary>Default fixed pool capacity. A burst is 3 sparks that each live
    /// 0.5 s, so the live count is minuscule; the cap is a generous bound.</summary>
    public const int DefaultCapacity = 512;

    private struct Slot
    {
        public Vector3 Origin;        // native m_vecPosition (the initial position)
        public Vector3 Velocity;      // native m_vecVelocity = shipVel + blast
        public Vector3 Acceleration;  // native m_vecAcceleration = -shipVel
        public float Size;            // native m_fSize
        public float AgeSec;
        public float DurationSec;     // native m_fDuration
    }

    private readonly Slot[] _buffer;
    private readonly Slot[] _scratch;   // same length; compaction scratch in Update
    private int _start;                 // ring index of the oldest live particle
    private int _count;                 // number of live particles

    /// <summary>Construct an empty pool. <paramref name="capacity"/> must be &gt; 0.</summary>
    public WarpTransitParticles(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        _buffer = new Slot[capacity];
        _scratch = new Slot[capacity];
    }

    /// <summary>Fixed pool capacity (upper bound on <see cref="ActiveCount"/>).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of currently live sparks in the pool.</summary>
    public int ActiveCount => _count;

    /// <summary>
    /// Emit one native <c>AddTeleportParticles</c> burst at the transit instant
    /// (thinggeo.cpp:619-696), reproducing the native math for a single call
    /// with the peak transit-instant ramp factor (<c>fTimeToShowSparks = 0</c>,
    /// <c>fCountUpToOne = 1</c> — see class remarks). Sparks are spawned around
    /// <paramref name="position"/> using the spray axes derived from
    /// <paramref name="velocity"/> (falling back to <paramref name="forward"/> /
    /// <paramref name="right"/> when stationary), each inheriting the ship's
    /// velocity and an opposing acceleration.
    /// </summary>
    /// <param name="position">The ship's world position at the transit instant
    /// (native <c>m_vecPositionLast</c>).</param>
    /// <param name="velocity">The ship's world velocity at the transit instant
    /// (native <c>vecFrameVelocity</c>).</param>
    /// <param name="forward">Normalised forward axis — used only when
    /// <paramref name="velocity"/> is ~zero (native's orientation fallback).</param>
    /// <param name="right">Normalised right axis — used only when
    /// <paramref name="velocity"/> is ~zero.</param>
    /// <param name="radius">The ship's radius (<see cref="ShipRadiusDefault"/>
    /// stand-in for native <c>m_radius</c>).</param>
    /// <param name="dtSec">Frame delta used to pace the inter-spark start-time
    /// offsets (native <c>fDeltaTime</c>).</param>
    /// <param name="rng">The randomness seam (native <c>random(a,b)</c> /
    /// <c>Vector::RandomDirection()</c>).</param>
    public void EmitBurst(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 right,
        float radius, float dtSec, ITrailRandom rng)
    {
        // Transit instant: fTimeToShowSparks = 0, fCountUpToOne = 1 (peak burst).
        float fTimeToShowSparks = 0f;
        float fCountUpToOne = 1f - fTimeToShowSparks;   // = 1

        // Spray axes from the real velocity; orientation fallback when stationary
        // (thinggeo.cpp:632-647).
        Vector3 vecX, vecY, vecZ;
        float fSpeedSquared = velocity.LengthSquared();
        if (fSpeedSquared > 0f)
        {
            vecZ = velocity / MathF.Sqrt(fSpeedSquared);
            vecX = Orthogonal(vecZ);
            vecY = Vector3.CrossProduct(vecX, vecZ);
        }
        else
        {
            vecZ = forward.Normalize();
            vecX = right.Normalize();
            vecY = Vector3.CrossProduct(vecX, vecZ);
        }

        // Peak spark count at transit (thinggeo.cpp:657-659): fCountUpToOne = 1 →
        // floor(TELEPORT_SPARK_COUNT * s_iShowSmoke * 1) + 0.5 = 3 sparks.
        float fScaledUpCount = ((fCountUpToOne * 2f) + 1f) * radius;   // = 3·radius at transit
        int iSparkCount = (int)(MathF.Floor(TeleportSparkCount * ShowSmoke * fCountUpToOne) + 0.5f);
        if (iSparkCount < 0) iSparkCount = 0;
        float fSparkTimeDelta = iSparkCount > 0 ? dtSec / iSparkCount : 0f;

        for (int i = 0; i < iSparkCount; i++)
        {
            // Blast: random unit direction scaled to radius·random(0.75, 1.25)
            // (thinggeo.cpp:666-667).
            float fRadius = radius * rng.Range(0.75f, 1.25f);
            Vector3 blast = rng.Direction() * fRadius;

            // Project the blast onto the plane in front of the ship and take the
            // offset (thinggeo.cpp:669-672). At transit (fTimeToShowSparks = 0)
            // the interpolation leaves the full 3D blast — the most explosive
            // (least planar) moment of the native ramp.
            Vector3 projected = ((blast * vecX) * vecX) + ((blast * vecY) * vecY);   // dot projections
            Vector3 vecDeltaProjection = projected - blast;
            Vector3 vecParticleVelocity = (fTimeToShowSparks * vecDeltaProjection) + blast;

            // Inter-particle random start time (thinggeo.cpp:680).
            float fRandomStartTime = rng.Range((float)i, (float)i + 1f) * fSparkTimeDelta;

            Vector3 acceleration = -velocity;                                     // m_vecAcceleration (thinggeo.cpp:682)
            Vector3 startPosition =
                  position
                + (fTimeToShowSparks * radius * 1.5f * vecZ)                    // thinggeo.cpp:685
                + (vecParticleVelocity * ((1f - fTimeToShowSparks) + fRandomStartTime))  // thinggeo.cpp:686
                + (0.5f * fRandomStartTime * fRandomStartTime * acceleration);   // thinggeo.cpp:687

            Add(new Slot
            {
                Origin = startPosition,
                Velocity = velocity + vecParticleVelocity,    // m_vecVelocity = vecFrameVelocity + blast (thinggeo.cpp:681)
                Acceleration = acceleration,
                Size = rng.Range(fScaledUpCount * 0.15f, fScaledUpCount * 0.3f),   // thinggeo.cpp:688
                AgeSec = 0f,
                DurationSec = SparkDurationSeconds,           // thinggeo.cpp:689
            });
        }
    }

    /// <summary>
    /// Age every live spark one frame and expire those past their duration.
    /// Position is <em>not</em> accumulated — native renders each frame from the
    /// stored initial position/velocity/acceleration via the closed-form integral
    /// (particlegeo.cpp:239), so <see cref="Enumerate"/> computes the exact
    /// current position rather than a drifting Euler sum.
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
            if (s.AgeSec < s.DurationSec)
                _scratch[k++] = s;
        }
        for (int i = 0; i < k; i++) _buffer[i] = _scratch[i];
        _start = 0;
        _count = k;
    }

    /// <summary>
    /// Enumerate the live sparks oldest-first, each at its fully-integrated
    /// current world position: <c>origin + velocity·age + ½·acceleration·age²</c>
    /// (native particlegeo.cpp:239), with the remaining-lifetime fraction for the
    /// hot-blue fade.
    /// </summary>
    public IEnumerable<WarpSparkView> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            float age = s.AgeSec;
            float posAge = age * age;
            var pos = s.Origin
                + (s.Velocity * age)
                + (s.Acceleration * (0.5f * posAge));
            float rem = (s.DurationSec - s.AgeSec) / s.DurationSec;
            if (rem < 0f) rem = 0f;
            yield return new WarpSparkView(pos, s.Velocity, s.Acceleration, s.Size, rem);
        }
    }

    /// <summary>Drop every live spark (used by tests to start clean).</summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// A unit vector perpendicular to <paramref name="v"/> — the port of native
    /// <c>Vector::GetOrthogonalVector()</c>. Picks the axis of v's smallest
    /// component and crosses it with v (a numerically robust choice).
    /// </summary>
    private static Vector3 Orthogonal(Vector3 v)
    {
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        Vector3 axis;
        if (ax <= ay && ax <= az) axis = new Vector3(1f, 0f, 0f);
        else if (ay <= az) axis = new Vector3(0f, 1f, 0f);
        else axis = new Vector3(0f, 0f, 1f);
        return Vector3.CrossProduct(v, axis).Normalize();
    }

    /// <summary>Append a spark to the ring, evicting the oldest when full.</summary>
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
