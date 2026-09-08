using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live mine as a sector model — the port of <c>CmineIGC</c>
/// (<c>src/Igc/mineigc.cpp</c>, ~185 lines), kept to this slice's need: a real
/// <see cref="Model"/> that lives in a <see cref="Sector"/>, sits <b>exactly
/// where it was deployed</b> (it has no velocity and no movement physics
/// whatsoever — <c>CmineIGC::Initialize</c> calls <c>SetMass(0.0f)</c> and
/// loads the mine with the <c>c_mtStatic</c> flag,
/// <c>src/Igc/mineigc.cpp:103-104,116</c>, and <c>Update</c> is only an expiry
/// check plus <c>base.Update(now)</c>, <c>mineigc.cpp:150-156</c>, with no
/// position integration at all), and <b>expires</b> after its decoded
/// <see cref="DataMineTypeIGC.Lifespan"/>, removing itself from the sector.
/// This is a genuinely simpler shape than <see cref="Projectile"/> /
/// <see cref="Missile"/>: because a mine never moves, the motion-carrier fields
/// those classes carry (<c>Velocity</c>/<c>Forward</c>) are deliberately
/// <em>absent</em> here, and <c>DataMineBase</c> itself (P93,
/// <c>src/Igc/igc.h:2549-2554</c>) carries only a position (<c>p0</c>), never a
/// velocity — so unlike <see cref="Missile"/> this class mirrors the native
/// exactly rather than adapting its shape.
/// </summary>
/// <remarks>
/// Honest-deferral statement, same style as P90's missile remarks: a mine's
/// real behaviours beyond static existence and timed expiry are <b>not</b>
/// implemented this slice. Specifically <em>not</em> implemented (separate
/// follow-ups):
/// <list type="bullet">
/// <item><b>Proximity detonation.</b> A real mine damages a nearby enemy when
/// one enters its blast radius; that is real, separate work mirroring P91's
/// shape for missiles but needing a <b>point-in-sphere / proximity trigger</b>
/// using <see cref="DataMineTypeIGC.MineRadius"/>, <em>not</em> a swept
/// <c>IOrdnance</c> collision the way projectiles/missiles use —
/// <c>Sector.ResolveDockCollisions</c> and <c>ResolveMining</c> are the two
/// existing precedents for that point-in-shape shape (<c>Model/Sector.cs</c>),
/// not <c>ResolveCombatCollisions</c>.</item>
/// <item><b>Destructibility.</b> <see cref=\"Fraction\"/> is stored but not yet
/// consulted by any damage path — a mine cannot yet be shot down before it
/// detonates.</item>
/// <item><b><c>Endurance</c></b> (<see cref=\"DataMineTypeIGC.Endurance\"/>)
/// is still unconfirmed and unused.</item>
/// <item><b>Cross-side/enemy visibility.</b> A real mine is arguably a hazard
/// any side should see, but that needs Phase 5's still-unbuilt cross-side
/// EXPORT and is not this task's problem.</item>
/// <item><b>A real \"deploy mine\" control input.</b> Like
/// <see cref=\"Ship.FireMissile\"/>, <see cref=\"Ship.DeployMine\"/> is
/// directly-callable only, not wired into live <c>ControlData</c>/<c>StateM</c>
/// dispatch.</item>
/// </list>
/// The one behavioural addition beyond the native's bare expiry is the
/// destruction event: on expiry, in addition to <c>sector.Remove(this)</c>,
/// the mine raises the host seam's <see cref=\"IIgcSite.MineDestroyedEvent\"/>
/// so the server can broadcast a real <c>FM_S_MINE_DESTROYED(84)</c> — the same
/// idea as <see cref=\"Missile\"/>'s expiry-raising-<c>MissileDestroyedEvent</c>
/// (which itself mirrors <c>Ship.ReceiveDamage</c> firing
/// <see cref=\"IIgcSite.KillShipEvent\"/> on a real ship death,
/// <c>Sim/Ship.cs:494</c>). The site is derived from the launcher (a
/// <see cref=\"Ship\"/>'s <see cref=\"Ship.Mission\"/>.<see cref=\"Mission.Site\"/>);
/// a directly-constructed mine with no launcher has no site and its destruction
/// event is a no-op.
/// </remarks>
public sealed class Mine : Model
{
    private readonly Sector _sector;
    private readonly double _timeExpire;

    /// <summary>Construct a mine in <paramref name="sector"/> at
    /// <paramref name="position"/>, armed at <paramref name="now"/> with the
    /// decoded <paramref name="mineType"/>. Its clock is seeded to
    /// <paramref name="now"/> so the first sector tick is a genuine full chunk,
    /// not one oversized step equal to the mission age (the same ship-clock fix
    /// <see cref="Projectile"/> documents, via <see cref="Model.InitClock"/>).
    /// Expiry is <c>now + mineType.Lifespan</c>. A mine has <b>no velocity and
    /// no heading</b> — it sits at <paramref name="position"/> unchanged until
    /// it expires (<c>mineigc.cpp:103-116,150-156</c>), so unlike
    /// <see cref="Missile"/> there are no <c>velocity</c>/<c>forward</c>
    /// parameters to mirror (<c>DataMineBase.p0</c> is a lone position,
    /// <c>igc.h:2549-2554</c>).</summary>
    public Mine(
        short id,
        Sector sector,
        DataMineTypeIGC mineType,
        Vector3 position,
        double now,
        Model? launcher = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        MineType = mineType;
        Launcher = launcher;
        _timeExpire = now + mineType.Lifespan;
        InitClock(now);
    }

    /// <summary>The decoded mine type from the static core — the source of
    /// <c>Lifespan</c> (and, read-but-unused this slice, <c>MineRadius</c>/
    /// <c>Power</c>/<c>HitPoints</c>/<c>Endurance</c>). Never hardcoded; the
    /// task rule. <c>Lifespan</c> is the expiry clock (<c>mineigc.cpp:113</c>:
    /// <c>m_timeExpire = m_time0 + dataMineType-&gt;lifespan</c>).</summary>
    public DataMineTypeIGC MineType { get; }

    /// <summary>The absolute time (mission-seconds) at which this mine expires
    /// and removes itself (<c>mineigc.cpp:113</c>).</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this mine lives in — the sector that owns it and that
    /// it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>The model that laid this mine (<c>m_launcher</c>), mirroring
    /// <see cref="Missile.Launcher"/>. Here it doubles as the route to the host
    /// seam: a <see cref="Ship"/> launcher exposes the authoritative
    /// <see cref="Mission.Site"/> (via <see cref="Ship.Mission"/>), which the
    /// expiry path uses to raise the destruction event. Optional (a directly
    /// constructed mine has no launcher and raises nothing). Matches the native
    /// <c>m_launcher</c>, retained only when the launcher-def price is 0
    /// (<c>mineigc.cpp:91-95</c>) — a simplification this slice does not chase
    /// (the caller's ship is always kept).</summary>
    public Model? Launcher { get; }

    /// <summary>The mine's health/damage fraction, defaulting to <c>1.0f</c> for
    /// a freshly-armed mine (<c>mineigc.cpp:88</c>, the non-export/IGC-construction
    /// path; the re-sync case reads the export's <c>fraction</c> byte instead,
    /// <c>mineigc.cpp:70</c>). Stored but <b>not consulted by anything</b> this
    /// slice — a mine has no damage path yet (destructibility is deferred), so
    /// this field exists to be carried, not yet acted on.</summary>
    public float Fraction { get; } = 1.0f;

    /// <summary>
    /// One chunk's work: a mine has no movement, so <c>OnUpdate</c> does nothing
    /// but the expiry check — <emph>no position advance</emph>, the literal shape
    /// of <c>CmineIGC::Update</c> (<c>mineigc.cpp:150-156</c>, <c>if (now >
    /// m_timeExpire) Terminate(); else base.Update(now)</c>). On expiry, remove
    /// <c>this</c> from <see cref="OwnerSector"/> and raise the host seam's
    /// <see cref="IIgcSite.MineDestroyedEvent"/> so the server can broadcast a
    /// real <c>FM_S_MINE_DESTROYED(84)</c> to the same-side peers who saw it
    /// deploy — mirroring <see cref="Missile.OnUpdate"/>'s destruction-event
    /// call exactly, minus the position-advance line it doesn't need. Removing
    /// <c>this</c> from the sector during this call is safe: the sector ticks
    /// over a snapshot (<c>Sector.Update</c>), and an expiry never repeats
    /// (latched by <see cref="TimeExpire"/>, monotone).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (now >= _timeExpire)
        {
            _sector.Remove(this);
            // The destruction-event broadcast: same shape as
            // Ship.ReceiveDamage raising KillShipEvent on a real death
            // (Ship.cs:494), applied to the mine's own expiry — the identical
            // line Missile.OnUpdate uses for its expiry (Missile.cs:152-153),
            // minus any position advance. Site derived from the launcher (a
            // Ship's mission host); a launcher-less mine has no site and
            // raises nothing.
            if (Launcher is Ship launcherShip)
                launcherShip.Mission?.Site.MineDestroyedEvent(this);
        }
    }
}
