using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single live damage-debris smoke puff as the pool enumerates it for
/// the renderer. <see cref="Position"/> is the particle's fully-integrated
/// current world position (native integrates with acceleration each render
/// frame — SmokeBehavior, particlegeo.cpp:58); <see cref="Velocity"/> /
/// <see cref="Acceleration"/> are the particle's (constant) initial values —
/// the native <c>m_vecVelocity</c> / <c>m_vecAcceleration</c> — surfaced so
/// unit tests can assert the ship-velocity inheritance and the −⅓ drag directly
/// (the renderer reads only Position / Size / RemainingFrac); <see cref="Size"/>
/// is the native <c>m_fSize</c> (world units) the renderer grows;
/// <see cref="RemainingFrac"/> is <c>(duration − age)/duration</c> in (0, 1],
/// driving the grey fade.</summary>
public readonly record struct DamageDebrisView(
    Vector3 Position,
    Vector3 Velocity,
    Vector3 Acceleration,
    float Size,
    float RemainingFrac);

/// <summary>
/// The pure, MonoGame-free particle pool for the continuous damage-debris
/// smoke stream (P297) — a faithful port of native <c>AddDamageParticles</c>
/// (<c>effect/thinggeo.cpp:699-755</c>). It reuses the exhaust-pool conventions
/// of <see cref="WarpTransitParticles"/> / <see cref="MissileTrailParticles"/>
/// (fixed-capacity ring buffer, an <see cref="ITrailRandom"/> randomness seam, a
/// pure Enumerate view), but reproduces the native damage-smoke math exactly:
/// one grey smoke puff per damage vent per frame, sized 1.5–2.5, ejected 4–6
/// units/s along the vent's forward, with a drag acceleration of −⅓·velocity.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (thinggeo.cpp:699-755).</b> Native runs this
/// <em>continuously every frame</em> while the model is damaged (its internal
/// <c>m_iLastDamageIndex &gt;= 0</c> gate; the level condition is ported in
/// <see cref="DamageDebrisTracker"/>). Per frame it emits, for each damage
/// frame (vent) 0…<c>m_iLastDamageIndex</c>, one smoke particle
/// (<c>iParticlesPerFrame = clamp(int(|vel|·dt), 1, s_iShowSmoke)</c> =
/// 1 with the default <c>s_iShowSmoke = 1</c>). Each particle:</para>
/// <list type="bullet">
/// <item>uses <c>ComputeFireParticleParameters</c> (thinggeo.cpp:537-579) with
/// ejection range (4, 6), start variance 0.05 and duration 0.35 (thinggeo.cpp:745),
/// thrusting along the vent's world forward;</item>
/// <item>gets <c>m_fSize = random(1.5, 2.5)</c> and
/// <c>m_fDuration = 0.35</c> (thinggeo.cpp:739, 751);</item>
/// <item>gets <c>m_vecAcceleration = vecStartVelocity · −0.333</c> — a drag
/// proportional to its own initial velocity (thinggeo.cpp:749);</item>
/// <item>is rendered by <c>SmokeBehavior</c> as a grey puff that grows
/// 0.75&#8209;×&#8209;size &#8594; 3.0&#8209;×&#8209;size and fades to zero over its
/// life (particlegeo.cpp:37-62) — see <see cref="DamageDebrisRenderer"/>.</item>
/// </list>
/// <para><b>Deliberate port substitutions (all documented).</b> (1) Native's
/// vent origins are real per-hit damage-frame offsets, but the client wire
/// carries no hit positions — only the ship's current hull fraction — so each
/// vent's local-space placement is a <em>deterministic</em> pseudo-random unit
/// direction (hash of shipId + vent index) scaled to <c>radius · 0.33</c> (the
/// native 0.33 damage-position scale, thinggeo.cpp:1369), with its outward
/// direction used as the ejection forward. The <em>count</em> of vents is real
/// (from <see cref="DamageDebrisTracker.DamageFrameCount"/>), so more-damaged
/// ships genuinely stream from more spots. (2) The client has no ship radius on
/// the wire and no static ship-type core, so <see cref="ShipRadiusDefault"/>
/// stands in for <c>m_radius</c> (the WarpTransitParticles precedent). (3) The
/// client infers no frame acceleration, so the native
/// <c>−fTimeSinceCreation·vecFrameAcceleration</c> velocity term is omitted
/// (velocity inheritance is direct).</para>
/// </remarks>
public sealed class DamageDebrisParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary>Smoke puff lifetime <c>m_fDuration = 0.35f</c> (thinggeo.cpp:751).</summary>
    public const float DurationSeconds = 0.35f;

    /// <summary>Puff size range <c>random(1.5, 2.5)</c> (thinggeo.cpp:739).</summary>
    public const float EffectSizeMin = 1.5f, EffectSizeMax = 2.5f;

    /// <summary>Ejection-velocity range passed to ComputeFireParticleParameters
    /// (thinggeo.cpp:745).</summary>
    public const float EjectMin = 4f, EjectMax = 6f;

    /// <summary>Start-position variance passed to ComputeFireParticleParameters
    /// (thinggeo.cpp:745).</summary>
    public const float StartVariance = 0.05f;

    /// <summary>The default quality multiplier <c>s_iShowSmoke</c> (= 1) that
    /// caps per-vent particles-per-frame at 1 (thinggeo.cpp:714-715).</summary>
    public const int ShowSmoke = 1;

    /// <summary>Port stand-in for the ship's model radius <c>m_radius</c> — the
    /// wire carries no per-ship radius and the client has no static ship-type
    /// core (the WarpTransitParticles precedent). Scales each vent's local
    /// offset.</summary>
    public const float ShipRadiusDefault = 50f;

    /// <summary>The native 0.33 damage-position scale applied to each vent's
    /// local offset (thinggeo.cpp:1369).</summary>
    public const float PositionScale = 0.33f;

    /// <summary>Default fixed pool capacity (shared across all ships; a badly
    /// damaged ship holds ~7 frames × up to 10 vents ≈ 70 live puffs).</summary>
    public const int DefaultCapacity = 4096;

    private struct Slot
    {
        public Vector3 Origin;        // native m_vecPosition (initial position)
        public Vector3 Velocity;      // native m_vecVelocity (start velocity)
        public Vector3 Acceleration;  // native m_vecAcceleration = vel·−0.333
        public float Size;            // native m_fSize
        public float AgeSec;
        public float DurationSec;     // native m_fDuration
    }

    private readonly Slot[] _buffer;
    private readonly Slot[] _scratch;   // same length; compaction scratch in Update
    private int _start;                 // ring index of the oldest live particle
    private int _count;                 // number of live particles

    /// <summary>Construct an empty pool. <paramref name="capacity"/> must be &gt; 0.</summary>
    public DamageDebrisParticles(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        _buffer = new Slot[capacity];
        _scratch = new Slot[capacity];
    }

    /// <summary>Fixed pool capacity (upper bound on <see cref="ActiveCount"/>).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of currently live smoke puffs in the pool.</summary>
    public int ActiveCount => _count;

    /// <summary>
    /// Emit one native <c>AddDamageParticles</c> frame for one ship
    /// (thinggeo.cpp:699-755): one grey smoke puff per damage vent (0 …
    /// <paramref name="damageFrameCount"/>−1), reproducing the native emission
    /// math (see class remarks). Called once per render/sim frame per damaged
    /// ship with that ship's real position/velocity/orientation.
    /// </summary>
    /// <param name="position">The ship's world position (native
    /// <c>m_vecPositionLast</c>).</param>
    /// <param name="velocity">The ship's world velocity (native
    /// <c>vecFrameVelocity</c>), inherited by every puff.</param>
    /// <param name="orientation">The ship's world orientation (rotates each
    /// vent's local offset/forward into world space, native
    /// <c>m_orientation</c>).</param>
    /// <param name="radius">The ship's radius (<see cref="ShipRadiusDefault"/>
    /// stand-in for native <c>m_radius</c>).</param>
    /// <param name="shipId">The ship's object id — stabilises each vent's
    /// pseudo-random hull placement (native's real per-hit offsets are not on
    /// the wire).</param>
    /// <param name="damageFrameCount">Number of active vents
    /// (<see cref="DamageDebrisTracker.DamageFrameCount"/>).</param>
    /// <param name="dtSec">Frame delta used to pace inter-particle start-time
    /// jitter (native <c>fDeltaTime</c>).</param>
    /// <param name="rng">The randomness seam (native <c>random(a,b)</c> /
    /// <c>Vector::RandomDirection()</c>).</param>
    public void Emit(Vector3 position, Vector3 velocity, Orientation orientation,
        float radius, short shipId, int damageFrameCount, float dtSec, ITrailRandom rng)
    {
        for (int vent = 0; vent < damageFrameCount; vent++)
        {
            // Deterministic local-space placement for this vent (stand-in for
            // the missing per-hit frame data — see class remarks).
            Vector3 locDir = VentDirection(shipId, vent);
            Vector3 locPos = locDir * (radius * PositionScale);     // thinggeo.cpp:1369
            Vector3 worldOrigin = position + (locPos * orientation);   // thinggeo.cpp:732
            Vector3 worldForward = locDir * orientation;               // thinggeo.cpp:733

            // Particles per vent this frame: clamp(int(|vel|·dt), 1, ShowSmoke) = 1
            // (thinggeo.cpp:704-716) with the default s_iShowSmoke = 1.
            int perFrame = (int)(velocity.Length() * dtSec);
            if (perFrame < 1) perFrame = 1;
            if (perFrame > ShowSmoke) perFrame = ShowSmoke;
            float uniformDt = dtSec / perFrame;                       // thinggeo.cpp:716

            for (int p = 0; p < perFrame; p++)
            {
                float effectSize = rng.Range(EffectSizeMin, EffectSizeMax);  // thinggeo.cpp:739
                float jitterT = uniformDt * (p + rng.Range(0f, 1f));         // thinggeo.cpp:742

                // ComputeFireParticleParameters (thinggeo.cpp:537-579) with the
                // AddDamageParticles argument list (thinggeo.cpp:745): eject(4,6),
                // start variance 0.05, effect duration 0.35.
                float ejectVel = rng.Range(EjectMin, EjectMax) * effectSize; // thinggeo.cpp:559
                Vector3 thrust = ejectVel * worldForward;                    // thinggeo.cpp:562
                Vector3 theoreticalStart = worldOrigin + (jitterT * thrust); // thinggeo.cpp:565
                Vector3 startPos = theoreticalStart
                    + ((StartVariance * effectSize) * rng.Direction());      // thinggeo.cpp:571
                Vector3 theoreticalEnd = theoreticalStart + (DurationSeconds * thrust); // :568
                Vector3 relVel = (theoreticalEnd - startPos) / DurationSeconds;         // :575
                Vector3 startVel = relVel + velocity;                                 // :578 (no frame accel)

                Add(new Slot
                {
                    Origin = startPos,
                    Velocity = startVel,
                    Acceleration = startVel * (-1f / 3f),   // thinggeo.cpp:749
                    Size = effectSize,                      // thinggeo.cpp:750
                    AgeSec = 0f,
                    DurationSec = DurationSeconds,          // thinggeo.cpp:751
                });
            }
        }
    }

    /// <summary>
    /// Age every live puff one frame and expire those past their duration.
    /// Position is <em>not</em> accumulated — native renders each frame from the
    /// stored initial position/velocity/acceleration via the closed-form
    /// integral (SmokeBehavior, particlegeo.cpp:58), so <see cref="Enumerate"/>
    /// computes the exact current position rather than a drifting Euler sum.
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
    /// Enumerate the live puffs oldest-first, each at its fully-integrated
    /// current world position: <c>origin + velocity·age + ½·acceleration·age²</c>
    /// (native SmokeBehavior, particlegeo.cpp:58), with the remaining-lifetime
    /// fraction for the grey fade/growth.
    /// </summary>
    public IEnumerable<DamageDebrisView> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            float age = s.AgeSec;
            var pos = s.Origin
                + (s.Velocity * age)
                + (s.Acceleration * (0.5f * age * age));
            float rem = (s.DurationSec - s.AgeSec) / s.DurationSec;
            if (rem < 0f) rem = 0f;
            yield return new DamageDebrisView(pos, s.Velocity, s.Acceleration, s.Size, rem);
        }
    }

    /// <summary>Drop every live puff (used by tests to start clean).</summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// A deterministic unit direction for a ship's vent — the port's stand-in
    /// for native's per-hit damage-frame offsets (unavailable on the wire), kept
    /// stable across frames (like native's fixed damage frames) while differing
    /// per ship and per vent. Pure integer hash → point on the unit sphere.
    /// </summary>
    private static Vector3 VentDirection(short shipId, int vent)
    {
        uint h = (uint)(shipId * 2654435761u) ^ (uint)(vent * 974711u);
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15;
        float z = (Frac(h * 0.6180339887f) * 2f) - 1f;
        uint h2 = h ^ 0x9e3779b9u; h2 *= 0x85ebca6bu; h2 ^= h2 >> 13;
        float a = Frac(h2 * 0.4142135624f) * 2f * MathF.PI;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }

    private static float Frac(float x) => x - MathF.Floor(x);

    /// <summary>Append a puff to the ring, evicting the oldest when full.</summary>
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
