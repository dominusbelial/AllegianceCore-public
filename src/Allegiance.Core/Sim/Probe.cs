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
/// Two further native attributes of a real probe are likewise <em>not</em>
/// modelled here: <b>combat destructibility</b> (a probe is genuinely
/// <c>c_mtDamagable</c> <em>and</em> <c>c_mtHitable</c> —
/// <c>probeigc.cpp:115</c> — so direct weapon/missile fire can shoot one down;
/// unlike a mine's proximity detonation this needs
/// <c>ResolveCombatCollisions</c> extended with a real <c>Probe</c> target
/// type, a separate later task) and <b>scanning/detection</b>
/// (<c>c_mtScanner</c>, <c>c_mtSeenBySide</c>, <c>c_mtPredictable</c> —
/// <c>probeigc.cpp:115</c> — the actual point of a reconnaissance probe, plus
/// its <c>PROBES_UPDATE(92)</c> message, a separate later mechanic). The
/// <see cref="SideId"/> and <see cref="Fraction"/> fields this task adds are
/// deliberately in place <em>now</em> so the destructibility follow-up does
/// not need the retrofit P95 applied to <see cref="Mine"/>.
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
    /// Stored but <b>not consulted by anything</b> this slice: there is no
    /// damage path yet (combat destructibility is deferred — see class remarks),
    /// so unlike <see cref="Mine.Fraction"/> (consumed by
    /// <see cref="Mine.ReduceStrength"/> since P96) this field is a pure
    /// placeholder today, standing exactly where <see cref="Mine"/>'s own
    /// P94-era placeholder stood before P95/P96 gave <see cref="Mine"/>'s real
    /// meaning. Backed by a private field so a future destructibility path can
    /// write it.</summary>
    private float _fraction = 1.0f;

    /// <summary>The probe's current health fraction. Present for the
    /// destructibility follow-up; <b>not</b> consulted by anything this slice.</summary>
    public float Fraction => _fraction;

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
