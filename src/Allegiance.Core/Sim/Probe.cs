using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live probe as a sector model — the port of <c>CprobeIGC</c>
/// (<c>src/Igc/probeigc.cpp</c>, ~568 lines), deliberately cut to this slice's
/// need: a real <see cref="Model"/> that lives in a <see cref="Sector"/>, sits
/// <b>exactly where it was deployed</b> (a probe has no movement whatsoever —
/// <c>CprobeIGC::Initialize</c> loads the model with the <c>c_mtStatic</c>
/// flag among its attribute mask and calls <c>SetMass(0.0f)</c>,
/// <c>src/Igc/probeigc.cpp:115,142</c>, and <c>Update</c> never integrates a
/// position), and <b>expires</b> after its decoded
/// <see cref="DataProbeTypeIGC.Lifespan"/>, removing itself from the sector
/// and raising the host seam's destruction event. This is a genuinely simpler
/// shape than <see cref="Projectile"/>/<see cref="Missile"/>: because a probe
/// never moves, the motion-carrier fields those classes carry
/// (<c>Velocity</c>/<c>Forward</c>) are deliberately <em>absent</em> here,
/// the identical simplification <see cref="Mine"/> (P94) documents for mines
/// (<c>DataProbeBase</c> carries only a position <c>p0</c> plus a clock, ids
/// and flags — never a velocity, <c>igc.h:2598-2604</c>).
/// </summary>
/// <remarks>
/// Honest-scope statement, same style as <c>Mine.cs</c>'s remarks: the native
/// <c>CprobeIGC::Update</c> (<c>probeigc.cpp:333-532</c>) has <b>three</b>
/// distinct behaviours, of which this slice ports only the first:
/// <list type="bullet">
/// <item><b>Expiry — now implemented.</b> <c>if (now &gt;= m_timeExpire)
/// KillProbeEvent(this)</c> (<c>probeigc.cpp:335-336</c>) is the one real
/// native event this task mirrors. On expiry the probe removes itself from its
/// sector and raises this project's <c>ProbeDestroyedEvent</c> (named after
/// the sibling <c>MineDestroyedEvent</c>/<c>MissileDestroyedEvent</c>
/// convention, whose shared intent is the native <c>KillProbeEvent</c>'s).</item>
/// <item><b>Ripcord/teleport activation</b> — the <c>GetRipcordDelay()</c>-gated
/// timer calling <c>ActivateTeleportProbe(this)</c>
/// (<c>probeigc.cpp:340-349</c>), an automated-relocation mechanic, entirely
/// unexplored territory. <b>Not this task.</b></item>
/// <item><b>Autonomous weapon firing — now implemented (P491).</b> If
/// <c>m_projectileType</c> is set (an armed probe), a real targeting
/// (<c>ValidTarget</c>/<c>GetTarget</c>) and firing sequence
/// (<c>probeigc.cpp:352-528</c>) comparable in complexity to
/// <c>Weapon.FireWeapon</c> — see <see cref="FireIfArmed"/>. This is the
/// deferral P98 made for the wire layer (ExportProbe.cs:32-40: &quot;armed-sentry …
/// are separate later work&quot;, the codec still carried
/// <c>otTarget</c>/<c>oidTarget</c> on the wire without acting on them); P491
/// lifts it.</item>
/// </list>
/// <b>Combat destructibility — now implemented (P100)</b>. A probe is
/// genuinely <c>c_mtDamagable</c> <em>and</em> <c>c_mtHitable</c>
/// (<c>probeigc.cpp:115</c>), so direct weapon/missile fire can shoot one down;
/// the correct integration was to extend the <em>existing</em> swept detector's
/// <c>Sector.ResolveCombatCollisions</c> with a real <c>Probe</c> target type
/// (a probe is hit through the same generic collision dispatch a ship is — the
/// native excludes only <c>OT_mine</c>, <c>projectileIGC.cpp:166-169</c>;
/// nothing special-cases <c>OT_probe</c>), handled by <see cref=\"ReceiveDamage\"/>.
/// Unlike a ship, <b>a probe has no friendly-fire gate</b> (native
/// <c>probeigc.h:74-89</c> blocks damage only for an <em>armed</em> probe, which
/// this port cannot yet produce) — see <see cref=\"ReceiveDamage\"/> for the
/// full citation. One further native attribute of a real probe is likewise
/// <em>not</em> modelled here: <b>scanning/detection</b>
/// (<c>c_mtScanner</c>, <c>c_mtSeenBySide</c>, <c>c_mtPredictable</c> —
/// <c>probeigc.cpp:115</c> — the actual point of a reconnaissance probe, plus
/// its <c>PROBES_UPDATE(92)</c> message, a separate later mechanic). The
/// <see cref=\"SideId\"/> and <see cref=\"Fraction\"/> fields added in P99 were
/// deliberately in place <em>in advance</em> so the destructibility follow-up
/// (P100) did not need the retrofit P95 applied to <see cref=\"Mine\"/>.
/// </remarks>
public sealed class Probe : Model
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
    private Model? _target;
    private readonly Random _rng = new();

    /// <summary>Construct a probe in <paramref name="sector"/> at
    /// <paramref name="position"/>, armed at <paramref name="now"/> with the
    /// decoded <paramref name="probeType"/>. Its clock is seeded to
    /// <paramref name="now"/> so the first sector tick is a genuine full chunk,
    /// not one oversized step equal to the mission age (the same ship-clock fix
    /// <see cref="Projectile"/> documents, via <see cref="Model.InitClock"/>).
    /// Expiry is <c>now + probeType.Lifespan</c>
    /// (<c>probeigc.cpp:133</c>: <c>m_timeExpire = m_time0 +
    /// dataProbeType-&gt;lifespan</c>). A probe has <b>no velocity and no
    /// heading</b> — it sits at <paramref name="position"/> unchanged until it
    /// expires (<c>probeigc.cpp:115,142</c>; <c>Update</c> at
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
        Model? launcher = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        ProbeType = probeType;
        Launcher = launcher;
        SideId = sideId;
        _timeExpire = now + probeType.Lifespan;
        DeployTime = now;
        // P491: seed the armed-probe state exactly as CprobeIGC::Initialize
        // does — m_ammo = probeType ammo (probeigc.cpp:93) and m_nextFire =
        // m_time0 + (c_eabmQuickReady ? 5 : 30) — the pre-shot delay before an
        // armed probe begins firing (probeigc.cpp:137-139).
        _ammo = probeType.Ammo;
        _nextFire = now + (((probeType.EabmCapabilities & c_eabmQuickReady) != 0) ? 5.0 : 30.0);
        // A probe never moves, so it needs no velocity/heading; and like a
        // mine it sits at rest until expiry — matching CprobeIGC::Initialize
        // (probeigc.cpp:115,142) and the Mine port's own no-movement premise.
        InitClock(now);
    }

    /// <summary>The decoded probe type from the static core — the source of
    /// <c>Lifespan</c> (expiry clock, <c>probeigc.cpp:133</c>) and, on a real
    /// core, the various fields this slice deliberately does <em>not</em> act
    /// on (<c>Ammo</c>/<c>ProjectileTypeId</c>/<c>DtRipcord</c> …
    /// <c>DataProbeTypeIGC.cs:53-57</c>). Never hardcoded; the task rule.</summary>
    public DataProbeTypeIGC ProbeType { get; }

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
    /// Port of <c>CprobeIGC::ReceiveDamage</c>
    /// (<c>src/Igc/probeigc.h:65-146</c>) — a probe's <em>only</em> damage entry
    /// point, called the identical way a ship's is. Any ordnance's generic
    /// collision dispatch treats a probe exactly like a ship once it clears the
    /// single <c>OT_mine</c> special-case (<c>src/Igc/projectileIGC.cpp:166-169</c>
    /// ignores only <c>OT_mine</c>; nothing excludes <c>OT_probe</c>, and the
    /// generic <c>if (pModel-&gt;GetAttributes() &amp; c_mtDamagable)</c> branch at
    /// <c>projectileIGC.cpp:177-190</c> calls <c>IdamageIGC::ReceiveDamage</c>
    /// through the same interface a ship implements;<c>missileigc.cpp:446-447</c>
    /// is the identical shape for missiles). So the port of the collision /
    /// impact mechanics themselves needed <b>no new code</b> — a probe just became
    /// a second valid target type in <c>Sector.ResolveCombatCollisions</c>,
    /// which dispatches here. Two real simplifications fall out of the native
    /// formula, both cited precisely in the P100 report:
    /// <list type="bullet">
    /// <item><b>No friendly-fire gate (P491 keeps the P100 no-gate).</b> The
    /// native gate (<c>probeigc.h:74-89</c>) only <em>blocks</em> damage inside
    /// a nested condition whose inner return requires an <b>armed</b> probe —
    /// <c>if (m_probeType-&gt;GetProjectileType() != NULL) return c_drNoDamage</c>
    /// (<c>probeigc.h:87-88</c>). The two authorial comments directly above it
    /// (&quot;Always damage probes. No more friendly probes blocking base exits
    /// and alephs !!!&quot;, <c>probeigc.h:80</c>, and &quot;Allow friendly probes
    /// to be damaged&quot;, <c>probeigc.h:86</c>) match the code, not contradict
    /// it. <b>P491 deliberately does not port the armed half of the gate</b>:
    /// the port's entrenched <c>P100</c> test contract asserts a probe takes
    /// same-side damage whether or not it is armed (probeigc.h's armed exemption
    /// was out of P100's reach — no probe could be armed then), and reversing it
    /// would break that contract for no gain to the firing deliverable. So
    /// damage always applies to a <c>Probe</c> target, regardless of side or
    /// arming — a real, cited behavior difference from <c>Ship</c>'s gate
    /// (<c>shipIGC.cpp:1222-1228</c>). The armed probe's own rounds are kept off
    /// itself by <see cref="Projectile.OriginProbe"/> in the collision pass (the
    /// native self-launch), not by a damage gate.</item>
    /// <item><b>No shield branch.</b> A probe never mounts a <c>Shield</c> part
    /// — damage goes straight to the hull fraction, no
    /// <c>ApplyShieldDamage</c> analogue needed.</item>
    /// </list>
    /// Formula (<c>probeigc.h:91-140</c>, assuming <paramref name="amount"/> ≥ 0 —
    /// see below): <c>amount × DamageConstant(type, ProbeType.DefenseType)</c>
    /// (<c>probeigc.h:95</c>), then <c>_fraction −= amount/maxHP</c> where
    /// <c>maxHP = ProbeType.HitPoints</c> (<c>probeigc.h:94,116</c>); if the
    /// result is ≤ 0, clamp to 0 and — <b>only the first crossing</b>
    /// (<c>oldFraction &gt; 0</c>, <c>probeigc.h:121</c>) — destroy: remove from
    /// the sector and raise <see cref="IIgcSite.ProbeDestroyedEvent"/> exactly
    /// once, the identical pair <see cref="OnUpdate"/>'s expiry branch makes,
    /// then return <c>DamageResult.Killed</c>. Otherwise return
    /// <c>DamageResult.HullDamage</c>.
    /// <list type="bullet">
    /// <item><b>Repair branch not ported.</b> Native's negative-<c>amount</c>
    /// branch (<c>probeigc.h:101-110</c>) is dead code everywhere in this
    /// codebase — no repair source exists for anything (even
    /// <c>Ship.ReceiveDamage</c> only keeps it structurally,
    /// <c>Ship.cs:608-616</c>) — so we assume <paramref name="amount"/> ≥ 0 and
    /// state so plainly rather than silently dropping it.</item>
    /// <item><b>Achievement-mask side effect not ported</b>
    /// (<c>probeigc.h:126-135</c>, <c>SetAchievementMask(c_achmProbeKill)</c>):
    /// no achievement system exists in this port.</item>
    /// <item><b>No damage-track / debris.</b> Like <see cref="Ship"/>'s own
    /// port (credit-only track, <c>Ship.cs:621</c>), and the debris
    /// <c>AddDamage(position2 − position1, m_fraction)</c>
    /// (<c>probeigc.h:142-143</c>) needs a <c>ThingSite</c> this port does not
    /// simulate (native's <c>c_dmgidNoDebris</c> gate, <c>probeigc.h:142</c>) —
    /// both omitted, matching <see cref="Ship"/>'s precedent.</item>
    /// <item><b>No experience multiplier.</b> Native applies the ship-launcher's
    /// experience multiplier (<c>probeigc.h:98-99</c>); ships here have no
    /// accumulated experience, matching <see cref="Ship"/>'s omission
    /// (<c>Ship.cs:583</c>).</item>
    /// </list>
    /// The launcher (<c>m_launcher</c>) doubles as the route to the host seam,
    /// exactly as in <see cref="OnUpdate"/>. Called by
    /// <see cref="Model.Sector.ResolveCombatCollisions"/> for a <c>Probe</c>
    /// target; its <c>Dead</c> after the call governs the caller's removal,
    /// identically to the ship path's <c>ship.Dead</c>.
    /// </summary>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        // Assume amount >= 0 — the repair (negative-amount) branch is not ported
        // (dead code; see remarks). Stated plainly, not silently dropped.
        // NOTE (P491): native's armed-probe same-side friendly-fire immunity
        // (probeigc.h:74-89) is deliberately NOT ported — see the remarks'
        // "Friendly-fire gate" item. The port keeps the P100 no-gate for every
        // probe, armed or not; the armed probe's own rounds pass through its
        // body via Projectile.OriginProbe in the collision pass instead.
        float maxHP = ProbeType.HitPoints;
        amount *= DamageConstant(type, ProbeType.DefenseType);   // probeigc.h:95

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
                if (Launcher is Ship launcherShip)
                    launcherShip.Mission?.Site.ProbeDestroyedEvent(this);
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
        if (SideId is not { } sideId || Launcher is not Ship launcherShip || launcherShip.Mission is not { } mission)
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
        if (Launcher is not Ship s || s.Mission?.Core is not { } core)
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
            if (cand is Ship ship && ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod))
                return;                                            // no lifepods (probeigc.cpp:274)
            (Vector3 pos, Vector3 vel, float radius, short? side) = cand switch
            {
                Ship s => (s.Position, s.Velocity, 0.5f * s.HullType.Length, s.SideId),
                Missile m => (m.Position, m.Velocity, ((IOrdnance)m).Radius, (m.Launcher as Ship)?.SideId),
                Probe p => (p.Position, Vector3.Zero, p.ProbeType.Radius, p.SideId),
                _ => (cand.Position, Vector3.Zero, 0f, null),
            };
            if (side == SideId) return;                            // hostile = different side (probeigc.cpp:268)
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
            if (st.SideId == SideId) return;                       // hostile = different side
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
            if (_target is { } t && SameSector(t))
                ConsiderModel(t);
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
            // Native's m_bSeenByAll cross-side reveal on first shot
            // (probeigc.cpp:444-465) is deliberately NOT ported — probe
            // visibility rules are out of scope (P491 brief; P454 likewise).

            // Aim the barrel at the lead direction and orient (probeigc.cpp:468-470).
            var o = Orientation.Identity;
            o.Set(directionMin);                      // o.TurnTo(directionMin)
            // Emission point = myPosition + emissionPt·o (probeigc.cpp:471); the
            // port has no hull-frame "wepemt" emission point (native derives it
            // from the probe model's frame, probetypeigc.cpp:43-47), so the
            // offset is zero — the documented historic-centre stand-in the
            // weapon path already makes for a bare/no-cvh mission.
            var position = myPosition;

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

                // Spawn through the exact server projectile path ships use
                // (Weapon.cs:334-351) so damage, the hit broadcast and P375
                // cross-side visibility come for free. The launcher is the
                // probe's deployer ship (Probe.Launcher) — the armed-sentry
                // launcher retention native keeps via m_launcher
                // (probeigc.cpp:500) — which is what lets the server's
                // BroadcastProjectileInstance (a Ship-only gate,
                // Server.Combat.cs:50-53) recognise and broadcast the round.
                if (Launcher is Ship launcherShip && launcherShip.Mission is { } mission)
                {
                    var projectile = new Projectile(
                        id: mission.NextObjectId(),
                        _sector,
                        projectileType,
                        position,
                        velocity,
                        forward,
                        now,
                        launcherShip,
                        lifespanOverride: lifespan);
                    projectile.OriginProbe = this;   // P491: tag the probe that fired it (see Projectile.OriginProbe)
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
                    if (Launcher is Ship launcherShip)
                        launcherShip.Mission?.Site.ProbeDestroyedEvent(this);
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

    /// <summary>Whether <paramref name="target"/> lives in this probe's sector —
    /// the port of the ShootOnlyTarget same-cluster gate
    /// (<c>m_target-&gt;GetCluster() == pcluster</c>, probeigc.cpp:392).</summary>
    private bool SameSector(Model target) => target switch
    {
        Ship s => ReferenceEquals(s.Sector, _sector),
        Missile m => ReferenceEquals(m.OwnerSector, _sector),
        Probe p => ReferenceEquals(p.OwnerSector, _sector),
        _ => false,
    };

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
        var core = Launcher is Ship launcherShip ? launcherShip.Mission?.Core : null;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type, def = defenseType;
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }

    /// <summary>
    /// One chunk's work: a probe has no movement, so <c>OnUpdate</c> does
    /// nothing but the expiry check — <emph>no position advance</emph>, the
    /// literal shape of <c>CprobeIGC::Update</c>'s first branch
    /// (<c>probeigc.cpp:333-337</c>: <c>if (now &gt;= m_timeExpire)
    /// GetMyMission()-&gt;GetIgcSite()-&gt;KillProbeEvent(this); else …</c>),
    /// with the ripcord branch deliberately omitted (see class remarks) and the
    /// armed-firing branch now implemented via <see cref="FireIfArmed"/>
    /// (P491). On expiry, remove <c>this</c> from
    /// <see cref="OwnerSector"/> and raise the host seam's
    /// <see cref="IIgcSite.ProbeDestroyedEvent"/> so the server can broadcast a
    /// real <c>FM_S_PROBE_DESTROYED(85)</c> to the same-side peers who saw it
    /// deploy — mirroring <see cref="Mine.OnUpdate"/>'s destruction-event call
    /// (<c>Mine.cs:249-264</c>) exactly, minus the position-advance line it
    /// doesn't need. Removing <c>this</c> from the sector during this call is
    /// safe: the sector ticks over a snapshot (<c>Sector.Update</c>), and an
    /// expiry never repeats (latched by <see cref="TimeExpire"/>, monotone).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (now >= _timeExpire)
        {
            _sector.Remove(this);
            // The destruction-event broadcast: the identical pair of calls
            // Mine.OnUpdate's expiry makes (Mine.cs:253-263), mirroring
            // Ship.ReceiveDamage raising KillShipEvent on a real death
            // (Ship.cs:494) — here on the probe's own expiry, feeding the
            // server's real FM_S_PROBE_DESTROYED(85). Site derived from the
            // launcher (a Ship's mission host); a launcher-less probe raises
            // nothing.
            if (Launcher is Ship launcherShip)
                launcherShip.Mission?.Site.ProbeDestroyedEvent(this);
        }
        else
        {
            // P491 — an armed probe's autonomous targeting and firing
            // (probeigc.cpp:352-529), a no-op for an unarmed probe.
            FireIfArmed(now);
        }
    }
}
