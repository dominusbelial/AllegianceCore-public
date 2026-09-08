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
/// <item><b>Autonomous weapon firing</b> — if <c>m_projectileType</c> is set
/// (an armed probe), a real targeting (<c>ValidTarget</c>/<c>GetTarget</c>)
/// and firing sequence (<c>probeigc.cpp:352-528</c>) comparable in complexity
/// to <c>Weapon.FireWeapon</c>. <b>Not this task</b> — same deferral P98 made
/// for the wire layer (ExportProbe.cs:32-40: &quot;armed-sentry … are separate
/// later work&quot;, the codec still carries <c>otTarget</c>/<c>oidTarget</c>
/// on the wire without acting on them).</item>
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
    private readonly Sector _sector;
    private readonly double _timeExpire;

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
    /// <item><b>No friendly-fire gate.</b> The native gate
    /// (<c>probeigc.h:74-89</c>) only <em>blocks</em> damage inside a nested
    /// condition whose inner return requires an <b>armed</b> probe —
    /// <c>if (m_probeType-&gt;GetProjectileType() != NULL) return c_drNoDamage</c>
    /// (<c>probeigc.h:87-88</c>). The two authorial comments directly above it
    /// (&quot;Always damage probes. No more friendly probes blocking base exits
    /// and alephs !!!&quot;, <c>probeigc.h:80</c>, and &quot;Allow friendly probes
    /// to be damaged&quot;, <c>probeigc.h:86</c>) match the code, not contradict
    /// it. <c>Ship.DeployProbe</c> (P99) never sets a projectile type — every
    /// probe this port can currently deploy is unarmed — so the inner branch
    /// never fires and <b>damage always applies, regardless of side</b>. We
    /// implement accordingly: <b>no side/friendly-fire check at all</b> for a
    /// <c>Probe</c> target — a real, cited behavior difference from
    /// <c>Ship</c>'s gate (<c>shipIGC.cpp:1222-1228</c>). Boundary condition for
    /// the report: if a future task ever arms a probe (sets
    /// <c>DataProbeTypeIGC.ProjectileTypeId</c>), this simplification needs
    /// revisiting. No <c>EabmCapabilities</c>/<c>HasCapability(c_eabmRescueAny)</c>
    /// parsing — dead code for an unarmed probe.</item>
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
    /// with the ripcord and armed-firing branches deliberately omitted (see
    /// class remarks). On expiry, remove <c>this</c> from
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
    }
}
