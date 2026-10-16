using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>A stationary scanner and optional autonomous weapon. The update
/// follows Igc/probeigc.cpp:333-532: expiry, teleport activation edge, target
/// selection, historical burst emission, and ammo exhaustion.</summary>
public sealed class Probe : Model, IProbeIgc
{
    // Native ExpendableAbilityBitMask bits (igc.h:770-781) read by the probe
    // fire loop to choose targets and the first-shot delay.
    private const short c_eabmQuickReady = 0x08;
    private const short c_eabmShootStations = 0x20;
    private const short c_eabmShootShips = 0x40;
    private const short c_eabmShootMissiles = 0x80;
    private const short c_eabmShootOnlyTarget = 0x1000;

    private readonly Sector _sector;
    private readonly double _timeExpire;

    // P491 armed-probe state: the fire clock, the ammo pool and the persisted
    // target (CprobeIGC::m_nextFire/m_ammo/m_target, probeigc.h:327-335).
    private double _nextFire;
    private short _ammo;
    private bool _seenByAll;
    private bool _terminated;
    private Model? _target;
    private readonly Random _rng = new();

    public Mission? OwnerMission => Mission;

    private Mission? Mission => _sector.Mission ?? (Launcher as Ship)?.Mission;

    /// <summary>Construct a probe in <paramref name="sector"/> at
    /// <paramref name="position"/>, armed at <paramref name="now"/> with the
    /// decoded <paramref name="probeType"/>. Its clock is seeded to
    /// <paramref name="now"/> so the first sector tick is a genuine full chunk,
    /// not one oversized step equal to the mission age (the same ship-clock fix
    /// <see cref="Projectile"/> documents, via <see cref="Model.InitClock"/>).
    /// Expiry is <c>now + probeType.Lifespan</c>
    /// (<c>probeigc.cpp:133</c>: <c>m_timeExpire = m_time0 +
    /// dataProbeType-&gt;lifespan</c>). A probe has no translational velocity;
    /// it sits at <paramref name="position"/> unchanged until it expires (<c>probeigc.cpp:115,142</c>; <c>Update</c> at
    /// <c>probeigc.cpp:333-532</c> never integrates a position), so unlike
    /// <see cref="Missile"/> there are no <c>velocity</c>/<c>forward</c>
    /// parameters to mirror. The <paramref name="sideId"/> is taken explicitly
    /// at construction (from the deploying ship, mirroring
    /// <c>DataProbeExport.sideID</c> / <c>ExportProbe.FromProbe</c>'s
    /// <c>sideID</c>) — the deliberate improvement over <see cref="Mine"/>
    /// (P94), which shipped without one and needed P95's retrofit.</summary>
    public Probe(
        short id,
        Sector sector,
        DataProbeTypeIGC probeType,
        Vector3 position,
        double now,
        short? sideId,
        Model? launcher = null,
        double? createdAt = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        ProbeType = probeType;
        Launcher = launcher;
        SideId = sideId;
        _timeExpire = now + probeType.Lifespan;
        DeployTime = now;
        // probeigc.cpp:123-128: probes receive a random initial attitude.
        float z = NextRandom(-1, 1), azimuth = NextRandom(0, 2 * MathF.PI);
        float radial = MathF.Sqrt(1 - z * z);
        Orientation.Set(new(radial * MathF.Cos(azimuth), radial * MathF.Sin(azimuth), z));
        // P491: seed the armed-probe state exactly as CprobeIGC::Initialize
        // does — m_ammo = probeType ammo (probeigc.cpp:93) and m_nextFire =
        // m_time0 + (c_eabmQuickReady ? 5 : 30) — the pre-shot delay before an
        // armed probe begins firing (probeigc.cpp:137-139).
        _ammo = probeType.Ammo;
        _nextFire = now + (((probeType.EabmCapabilities & c_eabmQuickReady) != 0) ? 5.0 : 30.0);
        // A probe sits at rest until expiry — matching CprobeIGC::Initialize
        // (probeigc.cpp:115,142) and the Mine port's own no-movement premise.
        double creationTime = createdAt ?? now;
        InitClock(creationTime);
        // An imported/backdated probe may already be active when created
        // (probeigc.cpp:151-154); zero delay is the ordinary deployment case.
        if (probeType.DtRipcord >= 0 && creationTime - now >= probeType.DtRipcord)
            Mission?.Site.ActivateTeleportProbe(this);
    }

    /// <summary>The decoded probe type from the static core — the source of
    /// lifespan, scanner, projectile, burst, ammo and activation fields.</summary>
    public DataProbeTypeIGC ProbeType { get; }

    /// <summary>Native model attitude, retained between firing cycles.</summary>
    public Orientation Orientation { get; } = Orientation.Identity;
    public override Orientation CollisionOrientation => Orientation;

    /// <summary>The absolute time (mission-seconds) at which this probe expires
    /// and removes itself (<c>probeigc.cpp:133</c>).</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this probe lives in — the sector that owns it and
    /// that it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>The absolute mission-time this probe was deployed at — its
    /// <c>m_time0</c>, captured from the constructor's <paramref name="now"/>
    /// (mirrors <c>DataProbeExport.time0</c>). Stored so a caller can observe
    /// the construction instant without recomputing it.</summary>
    public double DeployTime { get; }

    /// <summary>
    /// Port of <c>CprobeIGC::GetCanRipcord</c> (<c>probeigc.h:248-254</c>) —
    /// whether this probe is currently a valid ripcord anchor at sim-time
    /// <paramref name="now"/> for a ripcord whose countdown spans
    /// <paramref name="ripcordSpeed"/> seconds. Three conjuncts, copied
    /// verbatim:
    /// <list type="number">
    /// <item><b>It is a teleport probe.</b> <c>dt = m_probeType-&gt;GetRipcordDelay()</c>
    /// (<c>DtRipcord</c>); a non-teleport probe has <c>dt &lt; 0</c>
    /// (<c>probeigc.h:251</c>). <b>No capability-bit lookup is involved</b> —
    /// the teleport-ness is the sign of the delay field alone.</item>
    /// <item><b>Its activation delay has elapsed.</b> <c>(now − m_time0) &gt;= dt</c>
    /// (<c>probeigc.h:252</c>) — <c>m_time0</c> is <see cref="DeployTime"/>,
    /// so this is <c>(now − DeployTime) &gt;= dt</c>. A freshly-deployed probe
    /// is not ripcordable until the delay passes.</item>
    /// <item><b>It will not expire before the ripcord lands.</b>
    /// <c>(now + (ripcordSpeed + 1)) &lt;= m_timeExpire</c> (<c>probeigc.h:253</c>)
    /// — <c>ripcordSpeed</c> is the ripper's hull <c>GetRipcordSpeed()</c>,
    /// and the <c>+1</c> is native's fudge for the teleport's own tail.
    /// An expiring probe stops being a valid anchor.</item>
    /// </list>
    /// <paramref name="now"/> is the current sim time (<c>GetMyLastUpdate()</c>),
    /// supplied by the caller (e.g. <c>Mission.LastUpdate</c>).</summary>
    public bool CanRipcord(double now, float ripcordSpeed)
    {
        float dt = ProbeType.DtRipcord;
        return dt >= 0.0f &&
               (now - DeployTime) >= dt &&
               (now + (ripcordSpeed + 1.0f)) <= TimeExpire;
    }

    /// <summary>
    /// The side that deployed this probe, <c>null</c> if the deploying ship had
    /// none — carried <em>from day one</em> (the deliberate improvement over
    /// <see cref="Mine"/>, which P94 shipped without a side and P95 retrofitted).
    /// Mirrors <c>ExportProbe</c>'s own <c>sideID</c> field, which the wire
    /// broadcast reads from <see cref="ExportProbe.FromProbe"/>. It is the seed
    /// of the destructibility follow-up (where a friendly-fire / ownership gate
    /// analogous to <see cref="Mine.SideId"/>'s will need it). Nullable
    /// <c>short?</c>, matching <see cref="Ship.SideId"/>'s shape and
    /// <see cref="Mine.SideId"/>'s convention.</summary>
    public short? SideId { get; }

    /// <summary>The model that deployed this probe (<c>m_launcher</c>),
    /// mirroring <see cref="Mine.Launcher"/>: it doubles as the route to the
    /// host seam — a <see cref="Ship"/> launcher exposes the authoritative
    /// <see cref="Mission.Site"/> (via <see cref="Ship.Mission"/>), which the
    /// expiry path uses to raise the destruction event. Optional (a directly
    /// constructed probe with no launcher still carries a <see cref="SideId"/>
    /// but raises nothing on expiry — the broadcast is exercised live in
    /// <c>ProbeBroadcastTests</c>). Matches the native retention rule
    /// (<c>probeigc.cpp:102-106</c> retains the launcher when
    /// <c>launcherDef.price == 0</c>; a non-teleprobe with no armed sentry keeps
    /// it too — <c>probeigc.cpp:106</c>: &quot;Xynth would still like to know
    /// who deployed the probe&quot;). This slice does not chase the
    /// price-zero/armed-sentry conditionality — the caller's ship is always kept.</summary>
    public Model? Launcher { get; }

    /// <summary>The probe's health/damage fraction, defaulting to <c>1.0f</c>
    /// for a freshly-deployed probe (the non-export construction default, the
    /// analogue of <c>CprobeIGC</c>'s mine sibling at <c>mineigc.cpp:88</c>).
    /// Since P100 this is a real, written health pool consumed by
    /// <see cref="ReceiveDamage"/> (the direct-hit destructibility path), like
    /// <see cref="Mine.Fraction"/> consumed by <see cref="Mine.ReduceStrength"/>
    /// since P96. Its presence from P99 onward meant this follow-up needed no
    /// retrofit of the P95 kind applied to <see cref="Mine"/>.
    private float _fraction = 1.0f;

    /// <summary>The probe's current health fraction — the direct-hit health
    /// pool, written by <see cref="ReceiveDamage"/> and clamped to <c>0</c> on
    /// death (<c>CprobeIGC::GetFraction()</c>, <c>probeigc.h:141</c>).</summary>
    public float Fraction => _fraction;

    /// <summary>True once the probe is destroyed (<c>m_fraction &lt;= 0</c>),
    /// mirroring <see cref="Ship.Dead"/> (<c>Ship.cs:97</c>). The combat pass
    /// reads it the same way it reads <c>ship.Dead</c> to avoid re-killing an
    /// already-destroyed probe in the same step.</summary>
    public bool Dead => _fraction <= 0.0f;

    /// <summary>
    /// P491 — whether this probe is armed: it carries a resolvable projectile
    /// type (<c>m_projectileType != NULL</c>, <c>probeigc.cpp:352</c>), i.e.
    /// <see cref="ResolveProjectileType"/> finds one. An unarmed (sensor /
    /// teleport) probe never enters the firing branch of <c>Update</c>.</summary>
    public bool IsArmed => ResolveProjectileType() is not null;

    /// <summary>
    /// P491 — the probe's remaining ammo (<c>CprobeIGC::m_ammo</c>,
    /// seeded from <c>ProbeType.Ammo</c> at construction, <c>probeigc.cpp:93</c>).
    /// Each fired round decrements it; when it exhausts to 0 the probe destroys
    /// itself (<c>KillProbeEvent</c>, <c>probeigc.cpp:513-521</c>). Negative
    /// ammo (<c>&lt;= 0</c>) means an effectively-inexhaustible energy pool —
    /// native only decrements when <c>m_ammo &gt; 0</c> (<c>probeigc.cpp:513</c>).
    /// Exposed so a live test can observe the decrement.</summary>
    public short Ammo => _ammo;

    /// <summary>
    /// P491 — the probe's current, persisted target (<c>CprobeIGC::m_target</c>),
    /// the model an armed <c>c_eabmShootOnlyTarget</c> probe fires at
    /// (<c>probeigc.cpp:390-399</c>). Settable so the wire import path
    /// (<c>otTarget</c>/<c>oidTarget</c>) and a commanding caller can pin the
    /// target the same way native's <c>Initialize</c> does from the export
    /// (<c>probeigc.cpp:70-71,85-86</c>). A free-targeting probe
    /// (<c>ShootShips</c>/<c>ShootStations</c>/<c>ShootMissiles</c>) picks a
    /// fresh per-cycle target and leaves this <c>null</c> — matching native,
    /// whose <c>Export</c> then writes NA/NA for <c>otTarget</c>/<c>oidTarget</c>
    /// (<c>probeigc.cpp:558-562</c>).</summary>
    public Model? Target { get => _target; set => _target = value; }

    /// <summary>
    /// Native probeigc.h:65-146 damage/repair math, preserving the P100/P491
    /// no-friendly-fire-gate contract for all probes. Native sensors and
    /// RescueAny probes allow friendly damage (74-89); its armed-probe immunity
    /// is deliberately omitted here, as before P649. Matrix
    /// and ship-launcher experience scale damage/repair (95-99). Repairs cap
    /// at one (101-109); a first lethal crossing removes the probe and notifies
    /// its mission (116-139). Splash carries NoDebris (142-143).
    /// </summary>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        // P649b: retain the P100/P491 friendly-hit contract for direct hits and
        // splash alike. Native probeigc.h:74-89 exempts sensors/RescueAny but
        // protects armed probes; that armed exception is deliberately not ported.
        float maxHP = ProbeType.HitPoints;
        amount *= DamageConstant(type, ProbeType.DefenseType);   // probeigc.h:95
        if (launcher is Ship source) amount *= source.ExperienceMultiplier; // probeigc.h:97-99
        if (amount < 0)                                        // probeigc.h:101-109
        {
            if (maxHP > 0) _fraction = MathF.Min(1, _fraction - amount / maxHP);
            return DamageResult.HullDamage;
        }
        if (amount == 0) return DamageResult.HullDamage;

        DamageResult dr = DamageResult.HullDamage;               // probeigc.h:91
        float oldFraction = _fraction;                           // probeigc.h:92

        _fraction = maxHP > 0.0f ? _fraction - amount / maxHP : 0.0f;   // probeigc.h:116
        if (_fraction <= 0.0f)
        {
            _fraction = 0.0f;                                   // clamp (probeigc.h:119)
            if (oldFraction > 0.0f)                             // ONLY the first crossing (probeigc.h:121)
            {
                // Destroy exactly once: the identical pair of calls OnUpdate's
                // expiry branch makes (Probe.cs:200-201) — remove from the
                // sector and raise ProbeDestroyedEvent (the C++'s
                // KillProbeEvent(this), probeigc.h:138).
                _sector.Remove(this);
                (_sector.Mission ?? (Launcher as Ship)?.Mission)?.Site.ProbeDestroyedEvent(this);
                dr = DamageResult.Killed;                       // probeigc.h:139
            }
        }
        return dr;
    }

    /// <summary>
    /// P476 — resolve this probe's owning <see cref="Model.Side"/>'s global
    /// attribute set, or identity when the probe has no resolvable mission/side.
    /// The probe has no <c>Mission</c> field of its own; it derives its host
    /// seam the same way the rest of the class does (<see cref="Probe.DamageConstant"/>,
    /// <see cref="Probe.OnUpdate"/>): the launcher is a <see cref="Ship"/>, its
    /// <see cref="Ship.Mission"/> carries the sides. A probe deployed by a
    /// non-ship launcher, or whose launcher/mission can't resolve the side,
    /// reads identity (all 1.0) — the honest neutral the P446/P467/P475
    /// accessors establish. This is the port of native
    /// <c>GetSide()-&gt;GetGlobalAttributeSet()</c> (<c>probeigc.h:165,177</c>),
    /// which the probe's <c>InScannerRange</c> reads directly off its own side.
    /// </summary>
    private GlobalAttributeSet SideGlobalAttributes()
    {
        if (SideId is not { } sideId || Mission is not { } mission)
            return GlobalAttributeSet.Identity;
        foreach (var side in mission.Sides)
            if (side.Id == sideId)
                return side.GlobalAttributes;
        return GlobalAttributeSet.Identity;
    }

    /// <summary>
    /// P476 — effective scanner range = the decoded <c>ProbeType.ScannerRange</c>
    /// × the probe's owning side's <c>c_gaScanRange</c> factor — the port of the
    /// probe scanner's range boost in native <c>CprobeIGC::InScannerRange</c>
    /// (<c>probeigc.h:165</c>: <c>…GetScannerRange() · targetSignature ·
    /// GetSide()-&gt;…GetAttribute(c_gaScanRange)</c>; <c>c_gaScanRange</c> =
    /// igc.h:601). This is the scan range the probe contributes to the sector's
    /// cross-side visibility sweep as a scanner for its side.
    /// </summary>
    public float EffectiveScannerRange() => ProbeType.ScannerRange * SideGlobalAttributes().Get(GlobalAttributeSet.ScanRange);

    /// <summary>
    /// P479 — the resolved projectile type this armed probe would fire
    /// (<c>m_projectileType</c>, <c>probeigc.cpp:107</c>), from
    /// <see cref="ProbeType"/>.<c>ProjectileTypeId</c> via the launcher ship's
    /// mission core (the same launcher-derived host seam
    /// <see cref="SideGlobalAttributes"/> and <see cref="DamageConstant"/>
    /// use). <c>null</c> when the probe has no projectile type (id ≤ 0), when
    /// the launcher/mission/core can't be resolved, or when the id is absent
    /// from the core. This is the seam P467 deliberately deferred (a probe
    /// fires its OWN projectile with its OWN speed — <c>probeigc.cpp:367</c>
    /// — not through the shared weapon path); a future armed-probe firing
    /// implementation calls it, then <see cref="EffectiveProjectileSpeed"/> /
    /// <see cref="EffectiveProjectileLifespan"/> to build the round.</summary>
    public DataProjectileTypeIGC? ResolveProjectileType()
    {
        short pid = ProbeType.ProjectileTypeId;
        if (pid <= 0)
            return null;
        if (Mission?.Core is not { } core)
            return null;
        return core.ProjectileTypes.TryGetValue(pid, out var pt) ? pt : null;
    }

    /// <summary>
    /// P479 — the effective projectile speed this armed probe fires, the
    /// port of the probe fire loop's <c>speed = m_projectileType-&gt;GetSpeed();
    /// if (m_ammo != 0) speed *= …GetAttribute(c_gaSpeedAmmo)</c>
    /// (<c>probeigc.cpp:366-368</c>; <c>c_gaSpeedAmmo</c> = <c>igc.h:606</c>).
    /// An <b>ammo</b> probe (<c>ProbeType.Ammo != 0</c>) scales the round's
    /// speed by the owning side's <c>c_gaSpeedAmmo</c>; an energy probe
    /// (<c>Ammo == 0</c>) flies at the raw decoded speed — the mirror image
    /// of the weapon path's <c>SpeedAmmo</c> gate (<c>weaponIGC.cpp:220</c>).
    /// </summary>
    public float EffectiveProjectileSpeed(DataProjectileTypeIGC projectileType)
    {
        float speed = projectileType.Speed;
        if (ProbeType.Ammo != 0)
            speed *= SideGlobalAttributes().Get(GlobalAttributeSet.SpeedAmmo);
        return speed;
    }

    /// <summary>
    /// P479 — the effective projectile lifespan this armed probe fires, the
    /// port of <c>CprobeIGC::GetProjectileLifespan</c>
    /// (<c>probeigc.h:223-233</c>): <c>ls = projectileType-&gt;GetLifespan();
    /// if (m_ammo == 0) ls *= …GetAttribute(c_gaLifespanEnergy)</c>
    /// (<c>c_gaLifespanEnergy</c> = <c>igc.h:607</c>). An <b>energy</b> probe
    /// (<c>ProbeType.Ammo == 0</c>) scales the round's lifespan by the owning
    /// side's <c>c_gaLifespanEnergy</c>; an ammo probe keeps the raw decoded
    /// lifespan — the mirror image of the weapon path's <c>LifespanEnergy</c>
    /// gate (<c>weaponIGC.h:167-170</c>).
    /// </summary>
    public float EffectiveProjectileLifespan(DataProjectileTypeIGC projectileType)
    {
        float ls = projectileType.Lifespan;
        if (ProbeType.Ammo == 0)
            ls *= SideGlobalAttributes().Get(GlobalAttributeSet.LifespanEnergy);
        return ls;
    }

    /// <summary>
    /// P491 — the armed-probe autonomous firing sequence: the port of
    /// <c>CprobeIGC::Update</c>'s weapon branch (<c>probeigc.cpp:352-529</c>),
    /// driven each sector tick from <see cref="OnUpdate"/> (native only runs it
    /// when <c>m_projectileType</c> is set — <see cref="IsArmed"/>). Flow:
    /// a fire-rate gate, effective speed/lifespan via the P479 seam, target
    /// selection (<see cref="EvaluateCandidate"/>), lead aiming, dispersion
    /// perturbation, real projectile spawns through the same server path ships
    /// use (<see cref="Projectile"/> + <c>Sector.Add</c> +
    /// <see cref="IIgcSite.ProjectileCreatedEvent"/>), and an ammo decrement
    /// that self-destructs the probe on exhaustion — the native pair
    /// <c>KillProbeEvent</c> and <c>sector.Remove</c> exactly as the expiry
    /// branch raises. Runs on the sim thread (the sector ticks over a
    /// snapshot, <c>Sector.Update</c>), so mutating the sector here is safe.
    /// </summary>
    private void FireIfArmed(double now)
    {
        // m_projectileType == NULL: unarmed probe never fires (probeigc.cpp:352).
        if (!IsArmed) return;
        // Fire-rate gate (probeigc.cpp:354): the first shot is delayed by
        // _nextFire (5s quick-ready / 30s otherwise), seeded at construction.
        if (_nextFire >= now) return;

        if (ResolveProjectileType() is not { } projectileType) return;

        // We'll be able to take a shot (probeigc.cpp:359-360).
        float lifespan = EffectiveProjectileLifespan(projectileType);
        var myPosition = Position;
        float speed = EffectiveProjectileSpeed(projectileType);
        float accuracy = ProbeType.Accuracy;        // GetAccuracy (probeigc.h:243-246)
        float dtimeBurst = ProbeType.DtimeBurst;    // GetDtBurst (probeigc.h:238-241)
        float dispersion = ProbeType.Dispersion;    // GetDispersion (probeigc.cpp:371)

        double lastUpdate = LastUpdate;
        if (_nextFire < lastUpdate) _nextFire = lastUpdate;   // probeigc.cpp:373-374
        double dtUpdate = _nextFire - lastUpdate;             // probeigc.cpp:379

        // ── Target selection (probeigc.cpp:381-437) ──
        short eabm = ProbeType.EabmCapabilities;
        Model? target = null;              // persisted only for ShootOnlyTarget display
        Vector3 directionMin = default;
        bool acquired = false;
        // Initial engagement radius: distance2Min = (speed·lifespan/1.05)² —
        // the farthest point a round can travel (probeigc.cpp:383-384).
        float rangeLimit = speed * lifespan / 1.05f;
        float distance2Min = rangeLimit * rangeLimit;

        // ValidTarget for a Model candidate (probeigc.cpp:255-300): hostile
        // side, no lifepod, within range, and lead-interceptable.
        void ConsiderModel(Model cand)
        {
            // A migrated ship can still occur in an older sector snapshot;
            // native checks its actual cluster, not list membership alone.
            if (!SameSector(cand)) return;
            if (cand is Ship ship && ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod))
                return;                                            // no lifepods (probeigc.cpp:274)
            (Vector3 pos, Vector3 vel, float radius, short? side) = cand switch
            {
                Ship s => (s.Position, s.Velocity, 0.5f * s.HullType.Length, s.SideId),
                Missile m => (m.Position, m.Velocity, ((IOrdnance)m).Radius, BlastDamage.SideOf(m)),
                Probe p => (p.Position, Vector3.Zero, p.ProbeType.Radius, p.SideId),
                Mine m => (m.Position, Vector3.Zero, m.MineType.Radius, m.SideId),
                EquipmentTarget e when e.ObjectType == IgcObjectType.Asteroid &&
                    _sector.Mineable.FirstOrDefault(a => a.Data.AsteroidId == e.Id) is { } rock
                    => (e.Position, Vector3.Zero, rock.Radius, null),
                _ => (cand.Position, Vector3.Zero, 0f, null),
            };
            if (side == SideId) return;                            // hostile = different side (probeigc.cpp:268)
            if (IsFriendlySide(side)) return; // native AlliedSides
            if (cand is Missile missile ? missile.Dead : !EquipmentTarget.IsDamageable(cand)) return;
            bool isStatic = cand is Probe or Mine or EquipmentTarget;
            if (!(isStatic && SideId is { } sid && EquipmentTarget.CanSee(cand, sid, _sector))
                && !InScannerRange(cand, pos, radius)) return;
            var dp = pos - myPosition + vel * (float)dtUpdate;     // probeigc.cpp:277-280
            float d2 = dp.LengthSquared();
            if (d2 >= distance2Min) return;                        // probeigc.cpp:283
            float t = ShipDodgeLaw.SolveForImpact(dp, accuracy * vel, speed, radius, out var dir);  // :285-289
            if (t > lifespan) return;                              // probeigc.cpp:291
            acquired = true; target = cand; directionMin = dir; distance2Min = d2;
        }

        // ValidTarget for a Station candidate (stations are a separate type in
        // this port — IStationIgc, not a Model — so a dedicated arm).
        void ConsiderStation(Station st)
        {
            if (st.Dead || IsFriendlySide(st.SideId) || st.SideId == SideId) return;
            if (!(SideId is { } sid && st.SeenBySide(sid)) && !InScannerRange(st, st.Center, st.Radius)) return;
            Vector3 sp = st.Data.Position is { Length: 3 } a
                ? new Vector3(a[0], a[1], a[2])
                : default;
            var dp = sp - myPosition;                              // station never moves
            float d2 = dp.LengthSquared();
            if (d2 >= distance2Min) return;
            float t = ShipDodgeLaw.SolveForImpact(dp, Vector3.Zero, speed, st.Radius, out var dir);
            if (t > lifespan) return;
            acquired = true; directionMin = dir; distance2Min = d2; // target stays null (no Model for a station)
        }

        if ((eabm & c_eabmShootOnlyTarget) != 0)
        {
            // Fire only at the persisted target, and only while it stays in this
            // sector (probeigc.cpp:390-399).
            if (_target is { } t)
            {
                // Native probeigc.cpp:395 resolves a boarded target to its
                // source carrier. Never acquire another enemy if it is gone.
                if (t is Ship passenger && passenger.ParentShip is { } carrier) t = carrier;
                if (EquipmentTarget.IsPresent(t, _sector))
                {
                    if (t is EquipmentTarget { ObjectType: IgcObjectType.Station } e &&
                        _sector.StationRuntimes.FirstOrDefault(st => st.StationId == e.Id) is { } station)
                        ConsiderStation(station);
                    else ConsiderModel(t);
                }
            }
        }
        else
        {
            if ((eabm & c_eabmShootStations) != 0)
                foreach (var st in _sector.StationRuntimes) ConsiderStation(st);
            bool hasAOE = projectileType.BlastRadius > 1.0f;      // probeigc.cpp:412
            if (hasAOE && !acquired && (eabm & c_eabmShootMissiles) != 0)
                foreach (var mm in _sector.Models.OfType<Missile>()) ConsiderModel(mm);
            if (!acquired && (eabm & c_eabmShootShips) != 0)
                foreach (var s in _sector.Models.OfType<Ship>()) ConsiderModel(s);
            if (!hasAOE && !acquired && (eabm & c_eabmShootMissiles) != 0)
                foreach (var mm in _sector.Models.OfType<Missile>()) ConsiderModel(mm);
        }

        if (acquired)
        {
            RevealOnFiring();

            // Aim the barrel at the lead direction and orient (probeigc.cpp:468-470).
            var o = Orientation;
            // Native zlib/orientation.cpp:220-230 preserves the current up
            // vector except when nearly parallel/antiparallel to the target.
            var up = o.GetUp();
            float length2 = directionMin.LengthSquared();
            float dot = up * directionMin;
            float cosUp2 = length2 > 0 ? dot * MathF.Abs(dot) / length2 : 0;
            if (cosUp2 < -.99f) up = o.GetForward();
            else if (cosUp2 > .99f) up = -o.GetForward();
            o.Set(directionMin, up);
            // probetypeigc.cpp:43-47 loads and scales the wepemt frame;
            // probeigc.cpp:471 rotates it into the firing orientation.
            var model = Hull ?? _sector.CollisionHulls?.Resolve(ProbeType.ModelName);
            Vector3 emission = Vector3.Zero;
            if (model is { Radius: > 0 })
                foreach (var frame in model.Frames)
                    if (frame.Name == "wepemt")
                    {
                        emission = HullLandingBay.Transform(frame).Position * (ProbeType.Radius / model.Radius);
                        break;
                    }
            var position = myPosition + emission * o;

            // Fire the burst, dispensing one real projectile per round
            // (probeigc.cpp:473-511).
            int nShots = 0;
            do
            {
                Vector3 forward = directionMin;
                if (dispersion != 0.0f)
                {
                    float r = NextRandom(0.0f, dispersion);
                    float a = NextRandom(0.0f, 2.0f * MathF.PI);
                    forward += (r * MathF.Cos(a)) * o.GetRight()
                             + (r * MathF.Sin(a)) * o.GetUp();
                    forward = forward.Normalize();    // Vector::SetNormalize (probeigc.cpp:488)
                }

                // The probe never moves, so the round has no inherited velocity
                // (probeigc.cpp:491-492).
                Vector3 velocity = speed * forward;

                // Native creates each round at m_nextFire, retaining its
                // historical clock and expiry (probeigc.cpp:496-505).
                if (Mission is { } mission)
                {
                    Model source = Launcher is Ship ship && ReferenceEquals(ship.Mission, mission) ? ship : this;
                    var projectile = new Projectile(mission.NextObjectId(), _sector,
                        projectileType, position, velocity, forward, _nextFire, source,
                        lifespanOverride: lifespan) { OriginProbe = this };
                    _sector.Add(projectile);
                    mission.Site.ProjectileCreatedEvent(projectile);
                }

                nShots++;
                _nextFire += dtimeBurst;
            }
            while (_nextFire < now);

            // Ammo decrement — self-destruct on exhaustion (probeigc.cpp:513-521).
            if (_ammo > 0)
            {
                _ammo -= (short)nShots;
                if (_ammo <= 0)
                {
                    _ammo = 0;
                    _sector.Remove(this);
                    Mission?.Site.ProbeDestroyedEvent(this);
                }
            }
        }
        else
        {
            // No shots this cycle — wait for the next target opportunity
            // (probeigc.cpp:525-526).
            _nextFire = now;
        }
    }

    // Igc/probeigc.cpp:444-465: retry on subsequent shots until every side
    // already knows the probe or has a scanner in this cluster. Range and LOS
    // do not participate in this firing reveal.
    private void RevealOnFiring()
    {
        if (_seenByAll || Mission is not { } mission) return;
        _seenByAll = true;
        foreach (var side in mission.Sides)
        {
            if (SeenBySide(side.Id))
            {
                SideVisibility.RetainPredictableToSide(side.Id);
                continue;
            }
            // Native clusterIGC.h:145-148 excludes boarded ships; docked
            // ships have no cluster (shipIGC.h:448-470). Managed docked ships
            // can remain in Models for bookkeeping, so test that state here.
            bool hasScanner = _sector.Models.Any(m => m is Ship { StationId: null, ParentShip: null } s && s.SideId == side.Id ||
                m is Probe p && p.SideId == side.Id) || _sector.StationRuntimes.Any(s => s.SideId == side.Id);
            if (hasScanner || (mission.AllowAlliedViz && IsFriendlySide(side.Id)))
                SideVisibility.RevealPredictableToSide(side.Id);
            else _seenByAll = false;
        }
    }

    // Called by the owning sector's removal operation, including manual
    // removal, expiry, damage and ammo exhaustion (probeigc.cpp:222-245).
    internal void OnRemoved()
    {
        if (_terminated) return;
        _terminated = true;
        if (ProbeType.DtRipcord >= 0) Mission?.Site.DestroyTeleportProbe(this);
    }

    private bool SameSector(Model target) => target switch
    {
        Ship ship => ReferenceEquals(ship.Sector, _sector),
        Missile missile => ReferenceEquals(missile.OwnerSector, _sector),
        Probe probe => ReferenceEquals(probe.OwnerSector, _sector),
        Mine mine => ReferenceEquals(mine.OwnerSector, _sector),
        EquipmentTarget adapter => EquipmentTarget.IsPresent(adapter, _sector),
        _ => false
    };

    // Native Igc/igc.h:4297-4305: live alliance groups, in addition to
    // the port's explicit side-pair relationships used by standalone hosts.
    private bool IsFriendlySide(short? other)
    {
        if (Mission?.AreSidesFriendly(SideId, other) == true) return true;
        if (SideId is not { } side || other is null) return false;
        var group = Mission?.Sides.FirstOrDefault(s => s.Id == side)?.AllyGroup;
        return group is not null && Mission!.Sides.Any(s => s.Id == other && s.AllyGroup == group);
    }

    // Native probeigc.h:163-191: signature/side-GA range and asteroid LOS.
    private bool InScannerRange(object target, Vector3 position, float radius)
    {
        if (target is Ship { Flag: not null }) return true;
        float signature = target switch
        {
            Ship ship => ship.EffectiveSignature,
            Probe probe => probe.ProbeType.Signature,
            Missile missile => missile.MissileType.Signature,
            Mine mine => mine.MineType.Signature,
            Station station => station.Type.Signature,
            EquipmentTarget e when e.ObjectType == IgcObjectType.Asteroid =>
                _sector.Mineable.First(a => a.Data.AsteroidId == e.Id).Data.Signature,
            _ => 1
        };
        short? side = target is Station stationTarget ? stationTarget.SideId : BlastDamage.SideOf(target as Model);
        float signatureScale = Mission?.Sides.FirstOrDefault(s => s.Id == side)?.GlobalAttributes.Get(GlobalAttributeSet.Signature) ?? 1;
        if (signatureScale > 0) signature /= signatureScale;
        var rocks = _sector.Mineable.Where(a => a.Data.Position is { Length: 3 })
            .Select(a => new VisibilityMath.SightBody(new(a.Data.Position[0], a.Data.Position[1], a.Data.Position[2]), a.Radius)).ToArray();
        return VisibilityMath.InScannerRange(new(Position, ProbeType.Radius), EffectiveScannerRange(), _sector.Id,
            new(position, radius), signature, _sector.Id, rocks);
    }

    /// <summary>A uniform draw in [min, max) — the C++ <c>random(min,max)</c>
    /// (zlib) used for dispersion, identical to <see cref="Weapon"/>'s.</summary>
    private float NextRandom(float min, float max) => min + (float)(_rng.NextDouble() * (max - min));

    /// <summary>
    /// <c>GetDamageConstant(type, defenseType)</c> — the probe's own private
    /// copy of <see cref="Ship"/>'s identically-shaped method
    /// (<c>Ship.cs:668-677</c>): the decoded damage matrix
    /// <c>DamageConstants[dmgid][defid]</c>. <c>Probe</c> has no shared base
    /// with <c>Ship</c> (deliberately — this project prefers composition), so
    /// it needs its own copy rather than inheriting. Derives the
    /// <c>Mission</c>/<c>Core</c> the same way the class derives its host seam
    /// elsewhere (<see cref="OnUpdate"/>): the launcher is a <see cref="Ship"/>,
    /// its <see cref="Ship.Mission"/>.core. Returns 1.0 (neutral) when there is
    /// no Ship launcher/mission, or the indexes lie outside the 20×20 matrix, so
    /// a stray damage type can never amplify/dilute a hit into nonsense. Data
    /// driven from <see cref="DataConstantsIGC"/>, not hardcoded.</summary>
    private float DamageConstant(byte type, byte defenseType)
    {
        var core = _sector.Mission?.Core ?? (Launcher as Ship)?.Mission?.Core;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type & BlastDamage.Mask, def = defenseType; // missionigc.h:155
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }

    /// <summary>Native Igc/probeigc.cpp:333-352.</summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (_terminated) return;
        if (now >= _timeExpire)
        {
            _sector.Remove(this);
            Mission?.Site.ProbeDestroyedEvent(this);
        }
        else
        {
            double activation = DeployTime + ProbeType.DtRipcord;
            if (ProbeType.DtRipcord >= 0 && LastUpdate < activation && activation <= now)
                Mission?.Site.ActivateTeleportProbe(this);
            FireIfArmed(now);
        }
    }
}
