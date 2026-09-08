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
/// Honest-scope statement, same style as P90's missile remarks: a mine's real
/// behaviours beyond static existence, timed expiry and (since P95) proximity
/// detonation are <b>not</b> implemented this slice. Specifically <em>not</em>
/// implemented (separate follow-ups):
/// <list type="bullet">
/// <item><b>Proximity detonation — now implemented (P95).</b> An armed mine
/// triggers on the first qualifying ship within its blast radius
/// (<see cref="DataMineTypeIGC.MineRadius"/> + the ship's own radius) and
/// applies a single <see cref="DataMineTypeIGC.Power"/> hit via
/// <see cref="Ship.ReceiveDamage"/>, then consumes itself and raises
/// <see cref="IIgcSite.MineDestroyedEvent"/> — resolved in
/// <c>Sector.ResolveMineDetonations</c>. Deliberately simplified (stated
/// plainly, per this repo's honesty convention): no native entry/exit
/// dwell-time integration, no "ship must be moving" gate
/// (<c>mineigc.h:80-82</c>), no multi-ship AoE — a ship sitting still next to a
/// mine still sets it off, and only the first qualifying ship in a tick is hit.</item>
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
        DeployTime = now;
        // Carry the deploying ship's side directly on the mine (P95), set once
        // at construction from the launcher rather than re-derived on every
        // trigger. Mirrors ExportMine.FromMine's wire sideID. A launcher-less /
        // side-less mine has no side (fires on everyone, like Ship.SideId's
        // own nullable-short convention).
        SideId = (launcher as Ship)?.SideId;
        InitClock(now);
    }

    /// <summary>The decoded mine type from the static core — the source of
    /// <c>Lifespan</c> (expiry clock, <c>mineigc.cpp:113</c>:
    /// <c>m_timeExpire = m_time0 + dataMineType-&gt;lifespan</c>), and since P95
    /// of <c>MineRadius</c> (blast radius) / <c>Power</c> / <c>DamageType</c>
    /// (the detonation fields, read by <c>Sector.ResolveMineDetonations</c>).
    /// <c>HitPoints</c>/<c>Endurance</c> remain read-but-unused this slice.
    /// Never hardcoded; the task rule.</summary>
    public DataMineTypeIGC MineType { get; }

    /// <summary>The absolute time (mission-seconds) at which this mine expires
    /// and removes itself (<c>mineigc.cpp:113</c>).</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this mine lives in — the sector that owns it and that
    /// it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>
    /// The absolute mission-time this mine was deployed at — its arm-clock
    /// origin (<c>m_time0</c>, <c>mineigc.h:77</c>). Captured from the
    /// constructor's <paramref name="now"/>. The detonation pass gates on
    /// <c>now &gt;= DeployTime + <see cref=\"ArmTimeSeconds\"/></c>: a mine cannot
    /// trigger before the arm delay elapses.
    /// </summary>
    public double DeployTime { get; }

    /// <summary>The mine's arming delay in seconds — the port of the native
    /// <c>c_dtArmTime = 1.0f</c> (<c>mineigc.h:75</c>). A mine cannot trigger
    /// before <c>now &gt;= DeployTime + this</c>.</summary>
    public const double ArmTimeSeconds = 1.0;

    /// <summary>
    /// The side that laid this mine, <c>null</c> if the layering ship had none —
    /// the port of <c>m_launcher-&gt;GetSide()</c> used by the native friendly
    /// gate (<c>mineigc.h:77</c>) and by <c>ExportMine.FromMine</c>'s <c>sideID</c>.
    /// Nullable <c>short?</c>, matching <see cref=\"Ship.SideId\"/>'s own shape.
    /// </summary>
    public short? SideId { get; }

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
