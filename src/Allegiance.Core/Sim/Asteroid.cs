using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// The <b>runtime</b> asteroid — the minimal stateful counterpart to the
/// immutable static <see cref="DataAsteroidIGC"/> wire record. This is the
/// asteroid half of the same static-vs-runtime split P53 established for
/// stations (the static <c>DataStationIGC</c> in <c>Sector.Stations</c> plus
/// the docked state on the ship): it wraps the static data <see cref="Data"/>
/// and carries exactly the <b>two</b> pieces of runtime state mining needs —
/// the <b>depletable ore pool</b> (<see cref="OreRemaining"/>) and the
/// last-reported-ore watermark that throttles the <see cref="IIgcSite.MineAsteroidEvent"/>
/// emission. Nothing else (no hull/shield/name beyond what the record already
/// carries, matching the task's "keep it minimal: position + remaining ore").
/// It doubles as the <see cref="IAsteroidIgc"/> carrier for the seam, mirroring
/// <see cref="DockStation"/> : <c>IStationIgc</c> — the mining pass passes this
/// object as the <c>IAsteroidIgc</c> argument of <c>MineAsteroidEvent</c> and the
/// host casts it back out to reach the concrete record.
/// </summary>
/// <remarks>
/// Behaviour is ported from the reference engine and every simplification is
/// stated openly:
/// <list type="bullet">
/// <item><b>Depletion</b> (<see cref="MineOre"/>, port of
/// <c>CasteroidIGC::MineOre</c>, <c>asteroidigc.h:221-253</c>): the pool grants
/// only the requested <em>or</em> whatever remains when the rock is nearly
/// tapped — the <c>actualOre &lt; requestedOre</c> case the accrual loop reads
/// to detect tapping (<c>shipIGC.cpp:2240-2247</c>). The native
/// <c>DrainAsteroidEvent</c> call in the empty branch
/// (<c>asteroidigc.h:228-230</c>) is NYI-commented out in the reference itself,
/// so it is not emitted here either.</item>
/// <item><b>Regeneration</b> (<see cref="Regenerate"/>, port of
/// <c>CasteroidIGC::Update</c>, <c>asteroidigc.h:47-56</c>): the native engine
/// does <b>not</b> deplete-forever — a He3-capable asteroid continuously
/// regrows toward <c>oreMax</c> at a rate proportional to its deficit. This
/// server reads the loaded mission's c_fcidHe3Regeneration slot through
/// <see cref="Sector.He3Regeneration"/> (asteroidigc.h:52-55).
/// A parked miner is <em>never</em> permanently starved, so "mining
/// stops when the pool hits zero" (the verification wording) manifests as the
/// pool limiting the per-tick yield — <see cref="MineOre"/> returns less than
/// requested — not as a hard stop.</item>
/// <item><b>He3 gate</b> (<see cref="CanMineHe3"/>): regeneration and mining
/// both require the <c>c_aabmMineHe3</c> (0x01) capability bit
/// (<c>igc.h:761</c>, <c>asteroidigc.h:49</c>, <c>shipIGC.cpp:1955</c>). An
/// asteroid without it is inert for this task.</item>
/// <item><b>Report throttling</b> (<see cref="TryReportIfChanged"/>, port of
/// <c>asteroidigc.h:246-251</c>): <c>MineAsteroidEvent</c> fires only when the
/// ore has moved by more than the native 3.0 threshold since the last report,
/// so the host is not spammed every tick.</item>
/// </list>
/// The asteroid is deliberately <b>not</b> a <see cref="Model"/> — it does not
/// join the sector's models loop; mining detection is a sector pass
/// (<see cref="Sector.ResolveMining"/>) like docking/warps, and regeneration
/// runs inside that same pass. Object identity is the record's <see cref="Data"/>
/// <c>AsteroidId</c>, which the mining pass uses to look up the pool.
/// </remarks>
public sealed class Asteroid : IAsteroidIgc, ISideVisible
{
    internal Mission? ThingSiteMission;
    internal ThingSite? ThingSite;
    /// <summary>The He3-minable capability bit <c>c_aabmMineHe3 = 0x01</c> (igc.h:761).</summary>
    public const short CapMineHe3 = 0x01;

    /// <summary>The asteroid's damage-matrix defense-type index — the native
    /// constant <c>c_defidAsteroid = 0</c> (<c>src/Igc/igc.h:567</c>), used by
    /// <see cref="ReceiveDamage"/>'s <c>DamageConstant</c> lookup. The rock has no
    /// record field for it (unlike <c>DataProbeTypeIGC.DefenseType</c>); it is a
    /// hard-coded native constant, so it is declared once here for tests to read.</summary>
    public const byte DefenseType = 0;

    /// <summary>The underlying static wire record.</summary>
    public DataAsteroidIGC Data { get; }

    // Owning mission supplies the matrix even for NULL-launcher warp blasts.
    public Mission? Mission { get; internal set; }
    public Sector? Sector { get; internal set; }
    public BuildingEffect? BuildingEffect { get; internal set; }
    public Station? ConstructedStation { get; internal set; }
    private readonly Dictionary<short, float> _oreSeenBySide = [];
    private long? _inhibitStarted;
    public TimeProvider OreClock { get; set; } = TimeProvider.System;
    public bool InhibitOreUpdate { get; private set; }
    public float GetOreSeenBySide(short sideId) => _oreSeenBySide.GetValueOrDefault(sideId);
    public void SetInhibitUpdate(bool inhibit) => InhibitOreUpdate = inhibit;

    // Igc/asteroidigc.h:58-82: the inhibit window uses wall time, not simulation time.
    internal void UpdateOreKnowledge()
    {
        if (InhibitOreUpdate)
        {
            if (_inhibitStarted is not { } started) _inhibitStarted = OreClock.GetTimestamp();
            else if (OreClock.GetElapsedTime(started).TotalSeconds > 3)
            { _inhibitStarted = null; InhibitOreUpdate = false; }
        }
        if (!CanMineHe3 || InhibitOreUpdate || Mission is null) return;
        foreach (var side in Mission.Sides)
            if (GetCurrentEye(side.Id)) _oreSeenBySide[side.Id] = OreRemaining;
    }

    // Igc/asteroidigc.h:293-304: authoritative client updates bypass the eye/inhibit gate.
    public void SetOreWithFraction(float fraction, bool clientUpdate)
    {
        OreRemaining = fraction * OreMax;
        if (Mission is null) return;
        foreach (var side in Mission.Sides)
            if (clientUpdate || GetCurrentEye(side.Id)) _oreSeenBySide[side.Id] = OreRemaining;
    }

    /// <summary>The asteroid's depletable ore pool, seeded from the record's
    /// <see cref="DataAsteroidIGC.Ore"/>. <see cref="MineOre"/> drains it;
    /// <see cref="Regenerate"/> regrows it toward <see cref="OreMax"/>.</summary>
    public float OreRemaining { get; private set; }

    /// <summary>The record's ore capacity (<see cref="DataAsteroidIGC.OreMax"/>).</summary>
    public float OreMax => Data.OreMax;

    /// <summary>The physical rock radius (the record's <see cref="DataAsteroidIGC.Radius"/>),
    /// used by the mining proximity gate (<see cref="Model.Sector.ResolveMining"/>)
    /// as <c>asteroid.Radius + ship.Radius + 30.0f</c> (<c>shipIGC.cpp:1960</c>).</summary>
    public float Radius => Data.Radius;

    /// <summary>Whether this asteroid holds minable He3 (<c>aabmCapabilities &amp; 0x01</c>,
    /// igc.h:761) — the gate native applies to both mining (shipIGC.cpp:1955)
    /// and regeneration (asteroidigc.h:49).</summary>
    public bool CanMineHe3 => (Data.AabmCapabilities & CapMineHe3) != 0;

    /// <summary>The ore fraction the seam reports — <c>ore/oreMax</c> clamped
    /// to [0,1]; 0 when the record carries no capacity. Native
    /// <c>GetOreFraction()</c> (<c>igc.h:4162</c>) is what
    /// <c>MineAsteroidEvent</c> transmits (<c>asteroidigc.h:249</c>).</summary>
    public float OreFraction => OreMax <= 0f ? 0f : Math.Clamp(OreRemaining / OreMax, 0f, 1f);

    /// <summary>P134 — the rock's per-side cross-visibility state, the port's
    /// <see cref="SideVisibilityState"/> holder read/written by the sector's
    /// per-tick sweep (<c>Sector.ResolveCrossSideVisibility</c>). An asteroid
    /// is a genuine visibility target natively — its attribute mask carries
    /// <c>c_mtSeenBySide</c> (<c>asteroidigc.cpp:36</c>) and the detection sweep
    /// scores it like any other model — so the port's P132 sweep now includes
    /// <c>_mineable</c> asteroids as targets (it previously covered only
    /// ships/probes/stations). An asteroid has <b>no</b> <c>SideId</c>, so the
    /// sweep's own-side-is-always-visible rule does not apply: it is seen by a
    /// side only via one of that side's scanners in range with line of sight,
    /// exactly like a cross-side ship.</summary>
    public SideVisibilityState SideVisibility { get; } = new() { Predictable = true };

    /// <summary>Whether <paramref name="sideId"/> can currently see this
    /// asteroid — the port of <c>ImodelIGC::SeenBySide</c> (<c>modelIGC.h:110-114</c>),
    /// fed by P132's live sweep (via <see cref="SideVisibility"/>). Consumed by
    /// the per-side <c>ASTEROIDS_UPDATE</c>(179) batch.</summary>
    public bool SeenBySide(short sideId) => SideVisibility.SeenBySide(sideId);

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// asteroid (<c>modelIGC.h:120-124</c>). Part of the
    /// <see cref="ISideVisible"/> surface; not currently consumed by the
    /// asteroid broadcast (native's per-side batch reads only <c>SeenBySide</c>).</summary>
    public bool GetCurrentEye(short sideId) => SideVisibility.GetCurrentEye(sideId);

    private float _lastReportedOre;

    /// <summary>Wrap a static record as the runtime model, seeding the pool from
    /// its <see cref="DataAsteroidIGC.Ore"/> and the P766 sim orientation from
    /// the record's forward/up (native <c>CasteroidIGC::Initialize</c>,
    /// <c>Igc/asteroidigc.cpp:41-44</c>: <c>Orientation o(forward, up);
    /// SetOrientation(o)</c>).</summary>
    public Asteroid(DataAsteroidIGC data)
    {
        Data = data;
        OreRemaining = data.Ore;
        _lastReportedOre = data.Ore;
        if (data.Forward is { Length: 3 } fwd && data.Up is { Length: 3 } up)
            Orientation.Set(new Vector3(fwd[0], fwd[1], fwd[2]), new Vector3(up[0], up[1], up[2]));
    }

    /// <summary>
    /// P766 / SIM-0078 — the rock's sim orientation (the native hit-test
    /// orientation, <c>CmodelIGC::GetOrientation</c>), seeded from the record's
    /// forward/up (<c>asteroidigc.cpp:41-44</c>) and post-rotated about the
    /// record's rotation axis by <c>angle·dt</c> every tick
    /// (<see cref="Update"/>, the port of <c>CmodelIGC::Update</c>'s rotation
    /// block, <c>Igc/modelIGC.cpp:85-99</c>). The port's asteroid hit test is
    /// spherical (<c>Sector.ResolveShipAsteroidCollision</c> builds every rock
    /// collider with <c>HitShape.Sphere</c>), so this orientation does not
    /// change collision outcomes — but the native rule advances it regardless
    /// whenever the rotation angle is non-zero, so it advances here too.
    /// Read-only view: callers mutate through the spin step only.
    /// </summary>
    public Orientation Orientation { get; } = new();

    /// <summary>
    /// Grant ore up to <paramref name="requested"/> from this asteroid's pool,
    /// draining it correspondingly — the port of <c>CasteroidIGC::MineOre</c>
    /// (<c>asteroidigc.h:221-253</c>). Returns the actual amount granted (less
    /// than requested once the rock is nearly tapped, per
    /// <c>shipIGC.cpp:2240-2241</c>).
    /// </summary>
    public float MineOre(float requested)
    {
        float granted;
        if (OreRemaining < requested)
        {
            granted = OreRemaining;      // tapped: grant everything left (asteroidigc.h:225)
            OreRemaining = 0.0f;         // the DrainAsteroidEvent call is NYI in native; not emitted
        }
        else
        {
            granted = requested;         // ordinary subtraction (asteroidigc.h:233)
            OreRemaining -= requested;
        }

        // Xynth #132 — MineAsteroidEvent only when the ore moved by > 3.0 since
        // the last report (asteroidigc.h:246-251), so a busy rock doesn't spam.
        if (MathF.Abs(OreRemaining - _lastReportedOre) > 3.0f)
        {
            _lastReportedOre = OreRemaining;
            SetChangedFlag();
        }
        return granted;
    }

    /// <summary>The mining pass sets this after emitting <see cref="IIgcSite.MineAsteroidEvent"/>.
    /// Returns the ore fraction to report.</summary>
    public float PendingReport { get; private set; }
    public bool HasPendingReport { get; private set; }
    private void SetChangedFlag()
    {
        PendingReport = OreFraction;
        HasPendingReport = true;
    }

    /// <summary>Consume the pending-report flag (called by the mining pass after
    /// routing it to the site).</summary>
    public void ClearPendingReport() => HasPendingReport = false;

    /// <summary>
    /// Regrow the ore pool toward <see cref="OreMax"/> — the port of the
    /// regeneration half of <c>CasteroidIGC::Update</c>
    /// (<c>asteroidigc.h:47-56</c>): a He3-capable asteroid below capacity gains
    /// <c>dT · regenConst · (oreMax − ore)</c>. <paramref name="dt"/> is the
    /// elapsed sim seconds for this tick. Clamped at <c>oreMax</c> so floating
    /// error never reports a fraction &gt; 1 (native's asymptotic approach makes
    /// overshoot impossible in exact arithmetic; the clamp is defensive).
    /// </summary>
    internal void Update(double now, float dt, float regeneration)
    {
        // P766 / SIM-0078 — CmodelIGC::Update's rotation block
        // (Igc/modelIGC.cpp:85-99): a visible model with a non-zero rotation
        // angle post-rotates its hit-test orientation by angle·dt and
        // renormalizes (PostRotate renormalizes internally). Zero-angle rocks
        // (the common case for authored maps is non-zero, but fixture rocks
        // use 0) skip the step entirely — the native `if (angle != 0)` guard.
        // Allocation-free: stack arithmetic + the in-place matrix edit.
        float[] rot = Data.Rotation;
        if (rot is { Length: >= 4 } r && r[3] != 0f)
            Orientation.PostRotate(new Vector3(r[0], r[1], r[2]), r[3] * dt);
        Regenerate(dt, regeneration);
        UpdateOreKnowledge();
        BuildingEffect?.AsteroidUpdate(now); // Igc/asteroidigc.h:85-91
    }

    public void Regenerate(float dt, float regenConst)
    {
        if (!CanMineHe3) return;               // asteroidigc.h:49
        if (OreRemaining >= OreMax) return;    // already full (asteroidigc.h:50)
        float gained = dt * regenConst * (OreMax - OreRemaining);
        OreRemaining = Math.Min(OreMax, OreRemaining + gained);
    }

    /// <summary>The rock's health/damage fraction, defaulting to <c>1.0f</c>
    /// (the native constructor default, <c>asteroidigc.h:27</c>). Written by
    /// <see cref="ReceiveDamage"/> and clamped to <c>0</c> on death — the
    /// direct-hit health pool this task (P102) introduces, mirroring
    /// <see cref="Probe.Fraction"/>'s own shape exactly (it did not exist before
    /// this task; <c>asteroidigc.h:325</c> declares <c>m_fraction</c> and the
    /// damage path reads/writes it at <c>asteroidigc.h:133-164</c>).</summary>
    private float _fraction = 1.0f;

    /// <summary>The asteroid's current health fraction — the direct-hit
    /// health pool, written by <see cref="ReceiveDamage"/> and clamped to
    /// <c>0</c> on death (<c>CasteroidIGC::GetFraction()</c>,
    /// <c>asteroidigc.h:172-174</c>). The physical damage a rock can absorb
    /// before it is destroyed, independent of its ore pool.</summary>
    public float Fraction => _fraction;

    /// <summary>True once the asteroid is destroyed (<c>_fraction &lt;= 0</c>),
    /// mirroring <see cref="Ship.Dead"/>/<see cref="Probe.Dead"/>. The combat
    /// pass reads it the same way it reads <c>probe.Dead</c> to avoid re-killing
    /// an already-destroyed rock in the same step.</summary>
    public bool Dead => _fraction <= 0.0f;

    /// <summary>
    /// Native asteroidigc.h:126-171: rocks have no friendly-fire gate. Damage
    /// uses the asteroid defense column and ship-launcher experience (135-139).
    /// Repairs cap at one (141-145); positive damage reduces health and the
    /// first lethal crossing returns Killed for the sector to remove/notify
    /// (147-163). The matrix uses the owning mission even with a NULL launcher.
    /// </summary>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        float maxHP = Data.HitPoints;                      // asteroidigc.h:152
        amount *= DamageConstant(type, launcher);          // asteroidigc.h:135 (c_defidAsteroid)
        if (launcher is Ship source) amount *= source.ExperienceMultiplier; // asteroidigc.h:137-139
        if (amount < 0)                                    // asteroidigc.h:141-145
        {
            if (maxHP > 0) _fraction = MathF.Min(1, _fraction - amount / maxHP);
            return DamageResult.HullDamage;
        }
        if (amount == 0) return DamageResult.HullDamage;

        DamageResult dr = DamageResult.HullDamage;         // asteroidigc.h:133
        float oldFraction = _fraction;                     // asteroidigc.h:149

        // maxHP==0 (bad core data, native asserts non-zero at asteroidigc.h:151)
        // is handled the same way Probe guards it: clamp to zero so a 0-hitpoint
        // rock dies on any hit rather than dividing by zero into NaN.
        _fraction = maxHP > 0.0f ? _fraction - amount / maxHP : 0.0f;   // asteroidigc.h:152
        if (_fraction <= 0.0f)
        {
            _fraction = 0.0f;                             // clamp (asteroidigc.h:159)
            if (oldFraction > 0.0f)                       // ONLY the first crossing (asteroidigc.h:160)
            {
                dr = DamageResult.Killed;                 // asteroidigc.h:163
                // Death bookkeeping (remove from _mineable + raise the host's
                // KillAsteroidEvent) is performed by the CALLER —
                // Sector.ResolveCombatCollisions — which has the Sector and the
                // launcher's Mission to reach the site. See the remarks above.
            }
        }
        return dr;
    }

    /// <summary>
    /// <c>GetDamageConstant(type, c_defidAsteroid)</c> — the asteroid's own
    /// private copy of <see cref="Ship"/>'s identically-shaped method
    /// (<c>Ship.cs:668-677</c>): the decoded damage matrix
    /// <c>DamageConstants[dmgid][defid]</c>. The asteroid's defense-type index is
    /// the native constant <c>c_defidAsteroid = 0</c> (<c>igc.h:567</c>) — it has no
    /// record field to read it from (contrast <see cref="Probe.ProbeType"/>.DefenseType).
    /// <c>Asteroid</c> has no shared base with <c>Ship</c>/<c>Probe</c> (deliberately
    /// — composition over inheritance here), so it needs its own copy rather than
    /// inheriting. Derives the <c>Mission</c>/<c>Core</c> from the ordnance launcher
    /// the same way the class's host seam is reached at the call site: the launcher
    /// is a <see cref="Ship"/>, its <see cref="Ship.Mission"/>.core. Returns 1.0
    /// (neutral) when there is no Ship launcher/mission, or the indexes lie outside
    /// the 20×20 matrix, so a stray damage type can never amplify/dilute a hit into
    /// nonsense. Data driven from <see cref="DataConstantsIGC"/>, not hardcoded.
    /// </summary>
    private float DamageConstant(byte type, Model? launcher)
    {
        var core = Mission?.Core ?? (launcher as Ship)?.Mission?.Core;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type & BlastDamage.Mask, def = DefenseType; // missionigc.h:155
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }
}
