using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The two particle kinds a missile exhaust emits every frame —
/// native's fire and smoke particle types (thinggeo.cpp, AddThrustParticles'
/// "rocket" frame case: AddFire / AddSmoke).</summary>
public enum TrailParticleKind
{
    /// <summary>The short-lived bright flame at the nozzle (AddFire).</summary>
    Fire,

    /// <summary>The longer-lived expanding smoke (AddSmoke).</summary>
    Smoke,
}

/// <summary>A single live particle's state as the pure pool enumerates it for
/// rendering. <see cref="Position"/> is the particle's current world position;
/// <see cref="Size"/> is its world quad size (the native <c>m_fSize</c>);
/// <see cref="RemainingFrac"/> is <c>(duration − age) / duration</c> in (0, 1],
/// so the renderer can fade/scale by remaining lifetime without owning the
/// pool's clock.</summary>
public readonly record struct TrailParticleView(
    Vector3 Position,
    float Size,
    TrailParticleKind Kind,
    float RemainingFrac);

/// <summary>The randomness seam that keeps <see cref="MissileTrailParticles"/>
/// pure and unit-testable. The live client wires a <see cref="System.Random"/>-
/// backed implementation; tests inject a fixed-value stub.</summary>
public interface ITrailRandom
{
    /// <summary>A uniform random float in [min, max] (native <c>random(a,b)</c>).</summary>
    float Range(float min, float max);

    /// <summary>A uniformly-random unit vector (native <c>Vector::RandomDirection()</c>,
    /// vector.cpp:37-49) — used for the start-position jitter.</summary>
    Vector3 Direction();
}

/// <summary>A live missile the pool should emit a trail from — everything the
/// exhaust logic needs, decoupled from the wire's <see cref="State.MissileEntity"/>.</summary>
public readonly record struct TrailMissile(
    short Id,
    Vector3 Position,
    Vector3 Forward,     // normalised heading; the exhaust ejects along −Forward
    double TimeSec);     // the pool clock at this sample (drives velocity estimation)

/// <summary>
/// The pure, MonoGame-free exhaust-particle pool for the missile trail (P235).
/// A fixed-capacity ring buffer of live particles; each frame it ages out the
/// expired ones and emits one <see cref="TrailParticleKind.Fire"/> + one
/// <see cref="TrailParticleKind.Smoke"/> particle from every live missile's tail,
/// using native's own exhaust formulas as closely as the wire permits.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (thinggeo.cpp / missileigc.cpp).</b> Missiles
/// reuse the ship-afterburner exhaust system (there is no separate "missile
/// trail" effect in the reference): missileigc.cpp:53-55 calls
/// <c>SetAfterburnerSmokeSize(0.25 + radius*0.1)</c>,
/// <c>SetAfterburnerFireDuration(0.33)</c>, <c>SetAfterburnerSmokeDuration(2.0)</c>;
/// then <c>AddThrustParticles</c> (thinggeo.cpp:758+) walks the frame list and,
/// for the <c>"rocket"</c> exhaust frame, emits per frame (with the default
/// <c>s_iShowSmoke=1</c> → one pair):
/// <list type="bullet">
/// <item><b>Fire</b> — <c>ComputeFireParticleParameters</c> (thinggeo.cpp:537-579)
/// with ejection range (9, 12), start variance 0.2, duration 0.33, acceleration
/// zero (thinggeo.cpp:831-836).</item>
/// <item><b>Smoke</b> — <c>ComputeSmokeParticleParameters</c> (thinggeo.cpp:582-616)
/// with ejection range (18, 24), start variance 0.1, duration 2.0, acceleration
/// zero (thinggeo.cpp:855-860).</item>
/// </list>
/// Both scale <c>fEjectionVelocity = random(min,max) * fEffectSize</c> where
/// <c>fEffectSize = smokeSize * random(0.875,1.125)</c> (thinggeo.cpp:809),
/// add the emitter's own velocity, and scatter the start position by
/// <c>startVariance * fEffectSize * randomDirection</c>. This port reproduces
/// those formulas directly (see <see cref="Spawn"/>).</para>
/// <para><b>Deliberate simplifications.</b> (1) The client wire carries only
/// per-tick position/heading — no radius and no velocity — so the smoke size's
/// radius term uses a constant <see cref="MissileRadiusDefault"/> stand-in and
/// the emitter's velocity is ESTIMATED from successive position samples
/// (finite difference), which keeps the trail attached to a fast missile the
/// way native's inherited-frame-velocity does. (2) Native paces emission by
/// travelled distance; with <c>s_iShowSmoke=1</c> this collapses to one pair
/// per frame, reproduced here. (3) Particle acceleration is zero for both kinds
/// (native fire/smoke in the rocket path use a zero acceleration), so the pool
/// integrates position at constant velocity. (4) There is no ported FrameData /
/// nozzle attach point, so the emission origin is the tail of the missile's
/// bounding geometry along −Forward (see <see cref="TailOffset"/>).</para>
/// </remarks>
public sealed class MissileTrailParticles
{
    // ── Native exhaust constants (see class remarks for provenance) ─────────
    /// <summary>Fire ejection velocity lower bound, units/s (thinggeo.cpp:822).</summary>
    public const float FireEjectMin = 9f;
    /// <summary>Fire ejection velocity upper bound, units/s (thinggeo.cpp:823).</summary>
    public const float FireEjectMax = 12f;
    /// <summary>Smoke ejection velocity lower bound, units/s (thinggeo.cpp:847).</summary>
    public const float SmokeEjectMin = 18f;
    /// <summary>Smoke ejection velocity upper bound, units/s (thinggeo.cpp:848).</summary>
    public const float SmokeEjectMax = 24f;
    /// <summary>Fire particle lifetime, seconds (missileigc.cpp:54).</summary>
    public const float FireDuration = 0.33f;
    /// <summary>Smoke particle lifetime, seconds (missileigc.cpp:55).</summary>
    public const float SmokeDuration = 2.0f;
    /// <summary>Fire start-position variance fraction (thinggeo.cpp:825).</summary>
    public const float FireStartVariance = 0.2f;
    /// <summary>Smoke start-position variance fraction (thinggeo.cpp:849).</summary>
    public const float SmokeStartVariance = 0.1f;
    /// <summary>Afterburner smoke-size base term <c>0.25 + radius*0.1</c>
    /// (missileigc.cpp:53) — the <c>fEffectSize</c> scale factor.</summary>
    public const float SmokeSizeScale = 0.25f;
    /// <summary>Port stand-in for the missile's radius in the smoke-size formula
    /// — the wire carries no per-missile radius and the client has no static
    /// core, so a single neutral value is used (see class remarks).</summary>
    public const float MissileRadiusDefault = 2.0f;
    /// <summary>Exhaust emission offset behind the missile's centre along
    /// −Forward, in world units — the port's stand-in for the real (unported)
    /// engine-nozzle attach point: the tail of the bounding geometry.</summary>
    public const float TailOffset = 1.5f;
    /// <summary>The effect-size jitter spread around <c>smokeSize</c>
    /// (thinggeo.cpp:809).</summary>
    public const float SizeJitterMin = 0.875f, SizeJitterMax = 1.125f;

    /// <summary>Default fixed pool capacity (a generous cap; live count is far
    /// smaller — ~1 pair/frame/missile, age-out keeps smoke at ~40 live).</summary>
    public const int DefaultCapacity = 768;

    private struct Slot
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Size;
        public TrailParticleKind Kind;
        public float AgeSec;
        public float DurationSec;
    }

    private readonly Slot[] _buffer;
    private readonly Slot[] _scratch;   // same length; compaction scratch in Age
    private int _start;                 // ring index of the oldest live particle
    private int _count;                 // number of live particles

    // Per-missile previous sample, for the emitter-velocity finite difference.
    private readonly Dictionary<short, (Vector3 Pos, double Time)> _prev = new();

    /// <summary>Construct an empty pool. <paramref name="capacity"/> must be &gt; 0.</summary>
    public MissileTrailParticles(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        _buffer = new Slot[capacity];
        _scratch = new Slot[capacity];
    }

    /// <summary>Fixed pool capacity (upper bound on <see cref="ActiveCount"/>).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of currently live particles in the pool.</summary>
    public int ActiveCount => _count;

    /// <summary>
    /// Advance the simulation one frame: integrate every live particle's
    /// position by its (constant, zero-acceleration) velocity over
    /// <paramref name="dtSec"/>, expire the aged-out ones, then emit a fire +
    /// smoke pair from every live missile's tail using native's formulas.
    /// Call once per frame with the same wall-clock <paramref name="nowSec"/>
    /// and <paramref name="dtSec"/> as the game loop; <paramref name="nowSec"/>
    /// only feeds the per-missile velocity estimate.
    /// </summary>
    public void Update(double nowSec, float dtSec, IEnumerable<TrailMissile> missiles, ITrailRandom rng)
    {
        Advance(dtSec);
        foreach (var m in missiles)
            Spawn(m, nowSec, rng);
    }

    /// <summary>Enumerate the live particles oldest-first for rendering.</summary>
    public IEnumerable<TrailParticleView> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            float rem = (s.DurationSec - s.AgeSec) / s.DurationSec;
            if (rem < 0f) rem = 0f;
            yield return new TrailParticleView(s.Position, s.Size, s.Kind, rem);
        }
    }

    /// <summary>Drop every particle and forget all per-missile velocity state.</summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
        _prev.Clear();
    }

    // ── Simulation internals ────────────────────────────────────────────────

    /// <summary>Integrate live particles and compact out the expired ones.
    /// Motion is constant-velocity (zero acceleration — native fire/smoke in the
    /// rocket path use a zero acceleration); a particle dies once its age
    /// reaches its duration. Survivors are packed contiguously from slot 0 so
    /// the ring stays compact (this is cheap at ≤ Capacity slots per frame).</summary>
    private void Advance(float dtSec)
    {
        if (_count == 0) return;

        int k = 0;
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            s.Position += s.Velocity * dtSec;
            s.AgeSec += dtSec;
            if (s.AgeSec < s.DurationSec)
                _scratch[k++] = s;
        }
        // Compact back into the buffer.
        for (int i = 0; i < k; i++)
            _buffer[i] = _scratch[i];
        _start = 0;
        _count = k;
    }

    /// <summary>
    /// Emit one fire + one smoke particle from a live missile's tail, following
    /// native's exhaust formulas (see class remarks). The exhaust ejects along
    /// −<paramref name="m"/>.<c>Forward</c> (the backward-facing thrust), scaled
    /// by the randomized effect size; both particles inherit the emitter's
    /// estimated velocity and get a randomised start-position jitter; the fire
    /// additionally integrates the "travel to the theoretical end point over the
    /// fire duration" as its relative velocity (ComputeFireParticleParameters).
    /// </summary>
    private void Spawn(TrailMissile m, double nowSec, ITrailRandom rng)
    {
        var exhaust = -m.Forward.Normalize();          // backward-facing thrust dir
        var tail = m.Position + exhaust * TailOffset;  // nozzle at the tail

        float effectSize = (SmokeSizeScale + MissileRadiusDefault * 0.1f) * rng.Range(SizeJitterMin, SizeJitterMax);

        // Inherit the emitter's own velocity, estimated by finite difference of
        // the missile's per-tick position samples (the wire has no velocity).
        Vector3 emitterVel = EstimateVelocity(m, nowSec);
        _prev[m.Id] = (m.Position, nowSec);

        // Fire: ComputeFireParticleParameters(..., 9, 12, FireDuration, 0.2, effectSize).
        Add(MakeParticle(exhaust, tail, effectSize, emitterVel,
            rng, FireEjectMin, FireEjectMax, FireStartVariance, FireDuration,
            TrailParticleKind.Fire));

        // Smoke: ComputeSmokeParticleParameters(..., 18, 24, 0.1, effectSize).
        Add(MakeParticle(exhaust, tail, effectSize, emitterVel,
            rng, SmokeEjectMin, SmokeEjectMax, SmokeStartVariance, SmokeDuration,
            TrailParticleKind.Smoke));
    }

    /// <summary>Port of the shared ejection core of ComputeFireParticleParameters /
    /// ComputeSmokeParticleParameters: randomised ejection velocity along the
    /// exhaust direction × effect size, a jittered start position, and the
    /// start velocity = ejection + inherited emitter velocity (acceleration
    /// term dropped — both kinds use a zero frame acceleration).</summary>
    private static Slot MakeParticle(
        Vector3 exhaust, Vector3 tail, float effectSize, Vector3 emitterVel,
        ITrailRandom rng,
        float ejectMin, float ejectMax, float startVar, float duration,
        TrailParticleKind kind)
    {
        float ejection = rng.Range(ejectMin, ejectMax) * effectSize;
        var thrust = exhaust * ejection;
        // fJitteredTimeSinceCreation — with one pair per frame the inter-particle
        // time is ~dt; the jitter just nudges the start point slightly.
        float jitter = rng.Range(0f, 1f) * 0.02f;
        var theoStart = tail + thrust * jitter;
        var start = theoStart + (startVar * effectSize) * rng.Direction();

        Vector3 vel;
        if (kind == TrailParticleKind.Fire)
        {
            // ComputeFireParticleParameters: the particle "travels" from the
            // jittered start to the theoretical end over its duration.
            var theoEnd = theoStart + thrust * duration;
            var relVel = (theoEnd - start) / duration;
            vel = relVel + emitterVel;
        }
        else
        {
            // ComputeSmokeParticleParameters: velocity is simply the thrust
            // plus the inherited frame velocity (no end-point travel).
            vel = thrust + emitterVel;
        }

        return new Slot
        {
            Position = start,
            Velocity = vel,
            Size = effectSize,
            Kind = kind,
            AgeSec = 0f,
            DurationSec = duration,
        };
    }

    private Vector3 EstimateVelocity(TrailMissile m, double nowSec)
    {
        if (_prev.TryGetValue(m.Id, out var p))
        {
            double dt = nowSec - p.Time;
            if (dt > 1e-4)
                return (m.Position - p.Pos) / (float)dt;
        }
        return Vector3.Zero;
    }

    /// <summary>Append a particle to the ring, evicting the oldest live one
    /// (advancing the head) when the pool is full — the fixed-size bound.</summary>
    private void Add(Slot s)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = s;
            _count++;
        }
        else
        {
            // Pool full: overwrite the oldest, advancing the ring head.
            _buffer[_start] = s;
            _start = (_start + 1) % _buffer.Length;
        }
    }
}
