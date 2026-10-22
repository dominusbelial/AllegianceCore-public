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
/// P781 — one ship's per-frame lead-up spark emission inputs, gathered in the
/// gate-locked pose pass (the same pass that fills the render poses) into a
/// preallocated array and consumed by the 3D render pass. Pure data — the
/// native <c>ThingGeoImpl</c> per-frame state that feeds
/// <c>AddTeleportParticles</c> (effect/thinggeo.cpp:619-696).
/// </summary>
public readonly record struct TeleportEmitter(
    short ShipId,
    Vector3 Position,
    Vector3 FrameVelocity,   // rendered-position delta / frame dt (thinggeo.cpp:506-512)
    Vector3 Forward,
    Vector3 Right,
    float Radius,
    float TimeUntilTeleport); // selected ripcord/aleph timer (≤ 0 → no sparks)

/// <summary>
/// The pure, MonoGame-free particle pool for the warp/ripcord transit spark
/// spray (P296 + P781) — a faithful port of native <c>AddTeleportParticles</c>
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
/// <see cref="EmitTeleport"/> implements the full ramp for every ripcording or
/// aleph-closing visible ship (P781); <see cref="EmitBurst"/> is the transit
/// <em>instant</em> special case (<c>fTimeUntilTeleport = 0</c>) that fires on
/// the same <c>SET_CLUSTER</c> sector-change edge
/// <see cref="Flight.WarpTransitFlash"/> detects. Every native constant and
/// formula below is kept; the only deliberate substitutions are noted inline.</para>
/// <para><b>Spray axes.</b> Native builds the spray basis from the ship's real
/// velocity at the moment of transit: <c>vecZ = normalize(velocity)</c>,
/// <c>vecX = vecZ.GetOrthogonalVector()</c>, <c>vecY = cross(vecX, vecZ)</c>;
/// it falls back to the hull orientation when stationary (thinggeo.cpp:632-647).
/// Each spark's blast vector is a random unit direction scaled to
/// <c>radius · random(0.75, 1.25)</c>, lerped toward its projection on the X/Y
/// plane by the ramp factor (early sparks form a flat ring 1.5·R in front along
/// Z, thinggeo.cpp:685; at t → 0 the blast is fully 3-D); the particle's
/// velocity inherits the ship's frame velocity, and its acceleration opposes it
/// (<c>-vecFrameVelocity</c>), so the spray streams outward and then pulls
/// back — native's exact kinematics.</para>
/// <para><b>Ship radius stand-in.</b> Native scales particle size to
/// <c>(0.45…0.9) · (2·up+1)·m_radius</c> and blast speed to
/// <c>(0.75…1.25) · m_radius</c>. The caller threads the ship's real rendered
/// radius (<c>0.5 × HullType.Length</c>) when known;
/// <see cref="ShipRadiusDefault"/> stands in when it is not — the documented
/// convention of the missile trail.</para>
/// <para><b>Emission clock (deliberate deviation).</b> Native emits once per
/// RENDERED frame, so the spray density scales with the frame rate (a 144 fps
/// client sprays 2.4× the sparks of a 60 fps one). The port instead emits at a
/// fixed 60 Hz accumulator held by the caller (Game1), so exactly one native
/// call's worth of sparks is emitted per elapsed 1/60 s regardless of frame
/// rate (a 144 Hz client skips frames, a 30 Hz client emits two calls per
/// frame; a hitch catches up at most four calls). The per-emission
/// <c>dtSec</c> passed here is therefore always 1/60 — the native start-time
/// stagger stays proportionally identical. Native's own ripcord timer is NOT
/// pinned at zero for the sparks: it keeps decrementing (thinggeo.cpp:694), so
/// the spray stops right after the countdown even while the frozen model waits
/// for the server's move.</para>
/// </remarks>
public sealed class WarpTransitParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary><c>#define TELEPORT_SPARK_COUNT 3</c> (thinggeo.cpp:223) — the
    /// per-emission spark ceiling before the quality multiplier.</summary>
    public const int TeleportSparkCount = 3;

    /// <summary>The shipped-default quality multiplier <c>s_iShowSmoke</c>.
    /// Native multiplies the spark count by this (thinggeo.cpp:659). The static
    /// initialiser is 1 (thinggeo.cpp:1679), but the client loads
    /// <c>SetSmoke(LoadPreference("SmokeEffects", 3))</c> at start-up
    /// (WinTrek.cpp:3293; SetSmoke maps 2 → 1 and passes anything else through,
    /// WinTrek.cpp:5025-5032), so the SHIPPED default is 3 — one full emission
    /// is 9 sparks. (P781 fix: the port used the static initialiser.)</summary>
    public const float ShowSmoke = 3f;

    /// <summary>The lead-up ramp span, seconds — <c>fTimeToShowSparks</c> ramps
    /// 1 → 0 as the transit approaches over the final 3 s (thinggeo.cpp:651).</summary>
    public const float LeadTimeSeconds = 3.0f;

    /// <summary>Each spark's lifetime, seconds — <c>m_fDuration = 0.5f</c>
    /// (thinggeo.cpp:689).</summary>
    public const float SparkDurationSeconds = 0.5f;

    /// <summary>P781 — the fixed emission clock, seconds (1/60): native emits
    /// once per rendered frame, so its spray density scales with fps; the port
    /// paces the emission with this accumulator quantum instead (a deliberate
    /// deviation — see the class remarks).</summary>
    public const float EmissionClockSeconds = 1f / 60f;

    /// <summary>Fallback for the ship's model radius <c>m_radius</c> — used only
    /// when the transiting ship's real hull radius (<c>0.5 × HullType.Length</c>,
    /// now computable post-P333 and threaded in by Game1) is unknown. Everything
    /// native sizes off <c>m_radius</c> — particle size and blast speed — scales
    /// off this documented number.</summary>
    public const float ShipRadiusDefault = 50f;

    /// <summary>Default fixed pool capacity: the measured worst case — 50
    /// simultaneous emitters × ≤ 9 sparks (TELEPORT_SPARK_COUNT × ShowSmoke,
    /// thinggeo.cpp:659) × the 60 Hz emission clock × 0.5 s lifetime =
    /// 50 · 9 · 60 · 0.5 = 13 500 — so the ring never evicts even when every
    /// ship in a 50-ripper scene sprays at the full transit rate. Measured: the
    /// P781 perf scene (50 staggered rippers, 1920×1080) peaks at ~2 680 live
    /// sparks. (A Slot is a small struct; the two arrays are ~1.3 MB, allocated
    /// once. The pre-P781 capacity of 512 evicted at 50 bursts per frame.)</summary>
    public const int DefaultCapacity = 13_500;

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
    /// — exactly <see cref="EmitTeleport"/> with
    /// <paramref name="timeUntilTeleport"/> = 0 (the peak transit-instant
    /// parameters: fully 3-D blast, maximum count — see class remarks). Sparks
    /// are spawned around <paramref name="position"/> using the spray axes
    /// derived from <paramref name="velocity"/> (falling back to
    /// <paramref name="forward"/> / <paramref name="right"/> when stationary),
    /// each inheriting the ship's velocity and an opposing acceleration.
    /// </summary>
    /// <param name="position">The ship's world position at the transit instant
    /// (native <c>m_vecPositionLast</c>).</param>
    /// <param name="velocity">The ship's world velocity at the transit instant
    /// (native <c>vecFrameVelocity</c>).</param>
    /// <param name="forward">Normalised forward axis — used only when
    /// <paramref name="velocity"/> is ~zero (native's orientation fallback).</param>
    /// <param name="right">Normalised right axis — used only when
    /// <paramref name="velocity"/> is ~zero.</param>
    /// <param name="radius">The ship's radius (the real rendered
    /// <c>0.5 × HullType.Length</c>; <see cref="ShipRadiusDefault"/> stand-in
    /// for native <c>m_radius</c>).</param>
    /// <param name="dtSec">Frame delta used to pace the inter-spark start-time
    /// offsets (native <c>fDeltaTime</c>).</param>
    /// <param name="rng">The randomness seam (native <c>random(a,b)</c> /
    /// <c>Vector::RandomDirection()</c>).</param>
    public void EmitBurst(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 right,
        float radius, float dtSec, ITrailRandom rng)
        => EmitTeleport(position, velocity, forward, right, radius, timeUntilTeleport: 0f, dtSec, rng);

    /// <summary>
    /// P781 — emit one native <c>AddTeleportParticles</c> call
    /// (thinggeo.cpp:619-696) for the ripcord/aleph lead-up, implementing the
    /// full ramp: early sparks (<paramref name="timeUntilTeleport"/> near 3)
    /// form a flat ring 1.5·R in front of the ship along the spray axis; as t → 0
    /// the count ramps to the full <c>3 · ShowSmoke</c> burst and the blast goes
    /// fully 3-D. Nothing is emitted for a negative timer (native: "run the
    /// effect until it ends", thinggeo.cpp:625).
    /// </summary>
    /// <param name="position">The ship's RENDERED world position this frame
    /// (native <c>m_vecPositionLast</c>).</param>
    /// <param name="frameVelocity">The ship's rendered-position delta / frame dt
    /// (native <c>vecFrameVelocity</c>, thinggeo.cpp:506-512).</param>
    /// <param name="forward">Normalised forward axis — used only when
    /// <paramref name="frameVelocity"/> is ~zero (native's orientation fallback).</param>
    /// <param name="right">Normalised right axis — used only when
    /// <paramref name="frameVelocity"/> is ~zero.</param>
    /// <param name="radius">The ship's radius (native <c>m_radius</c>).</param>
    /// <param name="timeUntilTeleport">The selected teleport timer in seconds
    /// (<see cref="Flight.RipcordLeadup.SelectTeleportTime"/>); negative emits
    /// nothing.</param>
    /// <param name="dtSec">Emission delta used to pace the inter-spark start-time
    /// offsets (native <c>fDeltaTime</c>; the 60 Hz emission clock passes 1/60).</param>
    /// <param name="rng">The randomness seam (native <c>random(a,b)</c> /
    /// <c>Vector::RandomDirection()</c>).</param>
    public void EmitTeleport(Vector3 position, Vector3 frameVelocity, Vector3 forward, Vector3 right,
        float radius, float timeUntilTeleport, float dtSec, ITrailRandom rng)
    {
        if (timeUntilTeleport < 0f) return;   // thinggeo.cpp:625 — nothing happens before the final 3 s

        // Ramp: fTimeToShowSparks = clamp(t/3, 0, 1); fCountUpToOne = 1 − ramp
        // (thinggeo.cpp:650-656). up drives the count and the planar ring pull.
        float fTimeToShowSparks = Math.Clamp(timeUntilTeleport / Allegiance.ClientCore.Flight.RipcordLeadup.LeadTimeSeconds, 0f, 1f);
        float fCountUpToOne = 1f - fTimeToShowSparks;

        // Spray axes from the real velocity; orientation fallback when stationary
        // (thinggeo.cpp:632-647).
        Vector3 vecX, vecY, vecZ;
        float fSpeedSquared = frameVelocity.LengthSquared();
        if (fSpeedSquared > 0f)
        {
            vecZ = frameVelocity / MathF.Sqrt(fSpeedSquared);
            vecX = Orthogonal(vecZ);
            vecY = Vector3.CrossProduct(vecX, vecZ);
        }
        else
        {
            vecZ = forward.Normalize();
            vecX = right.Normalize();
            vecY = Vector3.CrossProduct(vecX, vecZ);
        }

        // Spark count and size scale (thinggeo.cpp:656-659):
        //   count = floor(TELEPORT_SPARK_COUNT · s_iShowSmoke · up) + 0.5
        //   scaled = (2·up + 1)·m_radius  (= 3·radius at the transit instant)
        float fScaledUpCount = ((fCountUpToOne * 2f) + 1f) * radius;
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
            // offset (thinggeo.cpp:669-672). Early sparks (ramp near 1) are pulled
            // fully into the plane — the flat ring; at transit (ramp = 0) the
            // interpolation leaves the full 3D blast.
            Vector3 projected = ((blast * vecX) * vecX) + ((blast * vecY) * vecY);   // dot projections
            Vector3 vecDeltaProjection = projected - blast;
            Vector3 vecParticleVelocity = (fTimeToShowSparks * vecDeltaProjection) + blast;

            // Inter-particle random start time (thinggeo.cpp:680).
            float fRandomStartTime = rng.Range((float)i, (float)i + 1f) * fSparkTimeDelta;

            Vector3 acceleration = -frameVelocity;                                     // m_vecAcceleration (thinggeo.cpp:682)
            Vector3 startPosition =
                  position
                + (fTimeToShowSparks * radius * 1.5f * vecZ)                    // 1.5·R in front along Z (thinggeo.cpp:685)
                + (vecParticleVelocity * ((1f - fTimeToShowSparks) + fRandomStartTime))  // thinggeo.cpp:686
                + (0.5f * fRandomStartTime * fRandomStartTime * acceleration);   // thinggeo.cpp:687

            Add(new Slot
            {
                Origin = startPosition,
                Velocity = frameVelocity + vecParticleVelocity,    // m_vecVelocity = vecFrameVelocity + blast (thinggeo.cpp:681)
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

    /// <summary>
    /// Non-allocating gather for the renderer (P781): clear
    /// <paramref name="target"/> and fill it with every live spark, each at its
    /// fully-integrated current world position (the same closed form
    /// <see cref="Enumerate"/> computes). Preferred over
    /// <c>AddRange(Enumerate())</c>, which allocates the iterator + list growth
    /// every frame.
    /// </summary>
    public void CopyTo(List<WarpSparkView> target)
    {
        target.Clear();
        if (target.Capacity < _count) target.Capacity = _count;
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
            target.Add(new WarpSparkView(pos, s.Velocity, s.Acceleration, s.Size, rem));
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
