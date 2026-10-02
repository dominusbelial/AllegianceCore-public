using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A live ship the pool should emit an engine trail from — everything
/// the afterburner-exhaust logic needs, decoupled from the wire's
/// <see cref="Allegiance.Core.Wire.ServerShipUpdate"/> and from the port's
/// per-ship pose gather.</summary>
public readonly record struct EngineTrailShip(
    short Id,
    Vector3 Position,
    Vector3 Velocity,          // the ship's real world velocity (inherited by every particle)
    Orientation Orientation,   // provides the backward thrust direction + the hull's facing
    float Radius,              // the ship's real rendered radius (0.5 × HullType.Length)
    float ThrustPower);        // 0..1 — native m_fThrustPower (>0 gates emission)

/// <summary>
/// The pure, MonoGame-free particle pool for the ship engine-thrust trail
/// (P358) — a faithful port of native <c>AddThrustParticles</c> (case 1, the
/// "rocket" afterburner fire+smoke path, <c>effect/thinggeo.cpp:758-917</c>),
/// reusing the exhaust-pool conventions of <see cref="MissileTrailParticles"/>
/// / <see cref="DamageDebrisParticles"/> (fixed-capacity ring buffer, an
/// <see cref="ITrailRandom"/> randomness seam, a pure Enumerate view over the
/// shared <see cref="TrailParticleView"/>). Each frame a thrusting ship emits
/// one fire + one smoke particle from its aft (the native "rocket" frame case:
/// AddFire + AddSmoke), which then stream behind it.
/// </summary>
/// <remarks>
/// <para><b>Native relationship (thinggeo.cpp:758-917).</b> Native runs this
/// continuously every frame a ship has a hardpoint frame whose name contains
/// "rocket" (case 1) or "smoke" (case 2), gated on <c>m_fThrustPower &gt; 0</c>
/// (thinggeo.cpp:760) — where <c>m_fThrustPower</c> is fed per frame from the
/// mounted afterburner's ramped power output (WinTrek.cpp:8002-8003 →
/// <c>afterburner-&gt;GetPower()</c>, CafterburnerIGC::Update, afterburnerIGC.cpp:
/// 133-146: ramps to 1.0 while the afterburner button is held, decays to 0 when
/// released). Per frame it emits, for each emitting frame, one fire + one smoke
/// particle (<c>iParticlesPerFrame = clamp(int(2·|vel|·dt/(m_sizeSmoke·power)),
/// 1, s_iShowSmoke)</c> = 1 with the default <c>s_iShowSmoke=1</c>). Each particle:</para>
/// <list type="bullet">
/// <item>uses <c>m_sizeSmoke=4.0</c> (constructor, thinggeo.cpp:235) and
/// <c>fEffectSize = m_sizeSmoke·random(0.875,1.125)</c> (thinggeo.cpp:809);</item>
/// <item><b>fire</b> — <c>ComputeFireParticleParameters</c> (thinggeo.cpp:537-579)
/// with ejection (9,12), start variance 0.2, duration <c>m_fAfterburnerFireDuration
/// = 0.333</c> (thinggeo.cpp:822-836), zero acceleration;</item>
/// <item><b>smoke</b> — <c>ComputeSmokeParticleParameters</c> (thinggeo.cpp:582-616)
/// with ejection (18,24), start variance 0.1, duration <c>m_fAfterburnerSmokeDuration
/// = 2.0</c> (thinggeo.cpp:841-860), zero acceleration;</item>
/// <item>both pass <c>fEffectSize·m_fThrustPower</c> as the effect-size into the
/// compute (thinggeo.cpp:826,850), so a decaying power thins the trail; both
/// eject along the ship's backward direction (<c>m_vecThrustDirection =
/// GetBackward()</c>, WinTrek.cpp:8002) and inherit the ship's own velocity
/// (<c>+vecFrameVelocity</c>).</item>
/// </list>
/// <para><b>Render.</b> <see cref="ShipEngineTrailRenderer"/> draws fire as a
/// bright orange flame and smoke as a grey plume (native FireBehavior
/// particlegeo.cpp:68-129 / SmokeBehavior particlegeo.cpp:24-66).</para>
/// <para><b>Deliberate port substitutions (all documented).</b> (1) This port
/// renders Meshy-regenerated content hulls, not the native <c>.mdl</c> geometry
/// — the native <c>.mdl</c> files DO carry real named "smoke"/"rocket" hardpoint
/// offsets (verified: quizfigv2.mdl <c>smoke</c> pos=(−0.00,0.22,−2.93), fig02.mdl
/// <c>lsmoke</c>/<c>rsmoke</c>), but those are in the native <c>.mdl</c> local
/// frame, which differs from the port's regenerated content-model frame (native
/// ship noses point +Z, this port's content models nose-at-−Z) plus arbitrary
/// per-asset Meshy scale — so mapping them onto the rendered mesh is not cleanly
/// portable across the 70+ content hulls. Instead the emission origin is the
/// hull's aft-centre: <c>position + backward·(radius·<see cref="AftCenterFraction"/>)</c>,
/// the same hull-geometry approximation <see cref="DamageDebrisParticles"/> uses
/// for damage vents when exact per-model data isn't cleanly portable. (2) Native's
/// <c>m_sizeSmoke=4.0</c> is a constant in native model units (~1.3× a native
/// fighter radius); this port normalizes all ships to real world units
/// (radius = 0.5·HullType.Length), so the particle size is scaled by
/// <c>radius·<see cref="SmokeSizeFactor"/></c> to keep the trail proportionally
/// readable on every hull class (a documented scale normalization, not a new
/// feature). (3) The frame-acceleration term of the compute calls is omitted
/// (the client has no per-frame acceleration history; velocity inheritance is
/// direct) — the same omission <see cref="DamageDebrisParticles"/> documents.
/// (4) The trail is triggered by the real <c>StateM.AfterburnerButton</c> bit
/// already on the wire for every visible ship (ServerShipUpdate.cs:43,110 —
/// encoded from the live ship's real StateM), ramped like native's afterburner
/// power by Game1; no new wire data is needed.</para>
/// </remarks>
public sealed class ShipEngineTrailParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary>Afterburner smoke-size base <c>m_sizeSmoke = 4.0</c> (thinggeo.cpp:235).</summary>
    public const float NativeSmokeSize = 4.0f;

    /// <summary>Fire ejection-velocity range passed to ComputeFireParticleParameters
    /// (thinggeo.cpp:822-823).</summary>
    public const float FireEjectMin = 9f, FireEjectMax = 12f;
    /// <summary>Smoke ejection-velocity range passed to ComputeSmokeParticleParameters
    /// (thinggeo.cpp:847-848).</summary>
    public const float SmokeEjectMin = 18f, SmokeEjectMax = 24f;
    /// <summary>Fire particle lifetime <c>m_fAfterburnerFireDuration = 0.333</c>
    /// (thinggeo.cpp:236).</summary>
    public const float FireDuration = 0.333f;
    /// <summary>Smoke particle lifetime <c>m_fAfterburnerSmokeDuration = 2.0</c>
    /// (thinggeo.cpp:237).</summary>
    public const float SmokeDuration = 2.0f;
    /// <summary>Fire start-position variance (thinggeo.cpp:825).</summary>
    public const float FireStartVariance = 0.2f;
    /// <summary>Smoke start-position variance (thinggeo.cpp:849).</summary>
    public const float SmokeStartVariance = 0.1f;
    /// <summary>The effect-size jitter spread around <c>m_sizeSmoke</c> (thinggeo.cpp:809).</summary>
    public const float SizeJitterMin = 0.875f, SizeJitterMax = 1.125f;
    /// <summary>The default quality multiplier <c>s_iShowSmoke</c> (= 1) that caps
    /// per-frame particle count at 1 (thinggeo.cpp:773-774).</summary>
    public const int ShowSmoke = 1;

    /// <summary>How far behind the ship's centre the trail originates, as a
    /// fraction of its radius — the aft-centre nozzle (native smoke hardpoints sit
    /// at the model's aft, e.g. quizfigv2.mdl <c>smoke</c> at z=−2.93 ≈ 1.05×radius).</summary>
    public const float AftCenterFraction = 0.95f;

    /// <summary>The port's normalization of native's constant <c>m_sizeSmoke=4.0</c>
    /// (which in native model units was ~1.3× a fighter's radius) to the port's
    /// real-hull world units: the per-ship smoke size is <c>radius·SmokeSizeFactor</c>,
    /// so the trail keeps the same proportional prominence on every hull class.</summary>
    public const float SmokeSizeFactor = 0.5f;

    /// <summary>Default fixed pool capacity (shared across all ships; a live
    /// ship holds ~1 pair/frame, age-out keeps its smoke ~2s = ~40 live).</summary>
    public const int DefaultCapacity = 2048;

    private struct Slot
    {
        public Vector3 Position;   // native m_vecPosition (initial)
        public Vector3 Velocity;   // native m_vecVelocity (start velocity)
        public float Size;         // native m_fSize
        public TrailParticleKind Kind;
        public float AgeSec;
        public float DurationSec;  // native m_fDuration
    }

    private readonly Slot[] _buffer;
    private readonly Slot[] _scratch;   // same length; compaction scratch in Update
    private int _start;
    private int _count;

    /// <summary>Construct an empty pool. <paramref name="capacity"/> must be &gt; 0.</summary>
    public ShipEngineTrailParticles(int capacity = DefaultCapacity)
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
    /// Emit one native <c>AddThrustParticles</c> "rocket" frame for one ship
    /// (thinggeo.cpp:798-864): one fire + one smoke particle from its aft,
    /// reproducing the native emission math (see class remarks). Emits nothing
    /// while <paramref name="ship"/>.ThrustPower is ≤ 0 (the native
    /// <c>m_fThrustPower &gt; 0</c> gate, thinggeo.cpp:760).
    /// </summary>
    public void Emit(in EngineTrailShip ship, float dtSec, ITrailRandom rng)
    {
        if (ship.ThrustPower <= 0f) return;

        float radius = ship.Radius > 0f ? ship.Radius : 1f;   // guard: unknown hull still shows a trail
        float smokeSize = radius * SmokeSizeFactor;           // normalized m_sizeSmoke
        Vector3 backward = ship.Orientation.GetBackward();    // native m_vecThrustDirection (WinTrek.cpp:8002)
        Vector3 origin = ship.Position + (backward * (radius * AftCenterFraction));

        // Particles per frame: clamp(int(2·|vel|·dt / (m_sizeSmoke·power)), 1, s_iShowSmoke)
        // (thinggeo.cpp:770-774) — with the default s_iShowSmoke=1 this is 1.
        float deltaLength = ship.Velocity.Length() * dtSec;
        int perFrame = (int)(2f * deltaLength / (smokeSize * ship.ThrustPower));
        if (perFrame < 1) perFrame = 1;
        else if (perFrame > ShowSmoke) perFrame = ShowSmoke;
        float uniformDt = dtSec / perFrame;

        for (int p = 0; p < perFrame; p++)
        {
            float effectSize = smokeSize * rng.Range(SizeJitterMin, SizeJitterMax);   // thinggeo.cpp:809
            float jitterT = uniformDt * (p + rng.Range(0f, 1f));                        // thinggeo.cpp:812
            float eff = effectSize * ship.ThrustPower;                                 // :826,:850

            // Fire: ComputeFireParticleParameters(..., 9, 12, FireDuration, 0.2, eff)
            // (thinggeo.cpp:816-829), AddFire (831-836): vel, acc=0, size, dur.
            {
                float ejection = rng.Range(FireEjectMin, FireEjectMax) * eff;          // thinggeo.cpp:559
                Vector3 thrust = ejection * backward;                                  // :562
                Vector3 theoStart = origin + (jitterT * thrust);                       // :565
                Vector3 theoEnd = theoStart + (FireDuration * thrust);                 // :568
                Vector3 startPos = theoStart + ((FireStartVariance * eff) * rng.Direction()); // :571
                Vector3 relVel = (theoEnd - startPos) / FireDuration;                  // :575
                Vector3 startVel = relVel + ship.Velocity;                             // :578 (no frame accel)
                Add(new Slot
                {
                    Position = startPos,
                    Velocity = startVel,
                    Size = effectSize,
                    Kind = TrailParticleKind.Fire,
                    AgeSec = 0f,
                    DurationSec = FireDuration,
                });
            }

            // Smoke: ComputeSmokeParticleParameters(..., 18, 24, 0.1, eff)
            // (thinggeo.cpp:841-853), AddSmoke (855-860): vel, acc=0, size, dur.
            {
                float ejection = rng.Range(SmokeEjectMin, SmokeEjectMax) * eff;        // thinggeo.cpp:603
                Vector3 thrust = ejection * backward;                                  // :606
                Vector3 theoStart = origin + (jitterT * thrust);                       // :609
                Vector3 startPos = theoStart + ((SmokeStartVariance * eff) * rng.Direction()); // :612
                Vector3 startVel = thrust + ship.Velocity;                             // :615 (no frame accel)
                Add(new Slot
                {
                    Position = startPos,
                    Velocity = startVel,
                    Size = effectSize,
                    Kind = TrailParticleKind.Smoke,
                    AgeSec = 0f,
                    DurationSec = SmokeDuration,
                });
            }
        }
    }

    /// <summary>Age every live particle one frame and expire those past their
    /// duration. Motion is constant-velocity (native fire/smoke in the rocket
    /// path use zero acceleration), integrated in <see cref="Enumerate"/>.</summary>
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

    /// <summary>Enumerate the live particles oldest-first, each at its current
    /// world position <c>origin + velocity·age</c> (native integrates at zero
    /// acceleration), with the remaining-lifetime fraction for the fade.</summary>
    public IEnumerable<TrailParticleView> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_start + i) % _buffer.Length;
            var s = _buffer[idx];
            var pos = s.Position + (s.Velocity * s.AgeSec);
            float rem = (s.DurationSec - s.AgeSec) / s.DurationSec;
            if (rem < 0f) rem = 0f;
            yield return new TrailParticleView(pos, s.Size, s.Kind, rem);
        }
    }

    /// <summary>Drop every live particle (used by tests to start clean).</summary>
    public void Reset()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>Append a particle to the ring, evicting the oldest when full.</summary>
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
