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
/// <item><b>Destructibility — now implemented (P96).</b> <see cref="Fraction"/>
/// is written by <see cref="ReduceStrength"/> (the port of
/// <c>ImineIGC::ReduceStrength</c>, <c>mineigc.h:170-181</c>), driven by
/// explosion-blast propagation from <see cref="Sector.ResolveExplosion"/>; below
/// the <c>0.25f</c> threshold the mine is destroyed and raises
/// <see cref="IIgcSite.MineDestroyedEvent"/>. A weapon projectile / standard
/// missile still never touches a mine (that is a separate, deferred mechanic — see
/// the next bullet).</item>
/// <item><b><c>Endurance</c> — now resolved (P96).</b>
/// <see cref="DataMineTypeIGC.Endurance"/> is confirmed against the native
/// <c>DataMineTypeIGC.endurance</c> and is the denominator of
/// <see cref="ReduceStrength"/>'s exponential decay formula (its first real use).</item>
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
    /// <c>mineigc.cpp:70</c>). Since P96 it is <b>consumed</b> by
    /// <see cref="ReduceStrength"/> (explosion-blast destructibility) — written
    /// there and read by the tests. Backed by a private field because it is no
    /// longer immutable.</summary>
    private float _fraction = 1.0f;

    /// <summary>The mine's current health fraction (<c>m_fraction</c>,
    /// <c>mineigc.h:165-168</c> <c>GetStrength</c>). Drives
    /// <see cref="ReduceStrength"/>'s exponential decay and the <c>0.25f</c>
    /// destruction threshold.</summary>
    public float Fraction => _fraction;

    /// <summary>
    /// Port of <c>ImineIGC::ReduceStrength(float amount)</c>
    /// (<c>src/Igc/mineigc.h:170-181</c>) — the explosion-blast destructibility
    /// path, and the endpoint of <see cref="Sector.ResolveExplosion"/>'s blast
    /// propagation for mines. Applies the native exponential decay of the mine's
    /// endurance-scaled fraction:
    /// <c>newEndurance = endurance · m_fraction · exp(−amount/endurance);
    /// m_fraction = newEndurance / endurance</c> — i.e.
    /// <c>m_fraction *= exp(−amount/endurance)</c> — and below the
    /// <c>0.25f</c> fraction threshold (<c>mineigc.h:177-178</c>) destroys the
    /// mine. The decay core is shared with <see cref="TakeAttritionDamage"/>
    /// (the P452 ship-contact attrition path) via <see cref="ApplyDecay"/>; only
    /// the destruction <em>threshold</em> differs (blast uses the fraction
    /// <c>0.25f</c>, attrition uses the native <c>newEndurance &lt; 1</c> test,
    /// <c>mineigc.h:139-142</c>).
    /// </summary>
    /// <remarks>
    /// This is the <b>first real consumer of <see cref="DataMineTypeIGC.Endurance"/></b>
    /// (unconfirmed/unused since P94/P95) — the field maps directly to the
    /// native <c>DataMineTypeIGC.endurance</c>, and the formula it feeds is
    /// ported verbatim. The native also calls
    /// <c>GetThingSite()->SetMineStrength(m_fraction)</c> on a non-destructive
    /// hit (<c>mineigc.h:180</c>) to broadcast the reduced fraction; this port's
    /// mine wire payload (<c>DataMineExport</c>) has no strength byte, so that
    /// broadcast is a deliberate no-op here (the reduced fraction is visible on
    /// the sim object only). The native clamps nothing for a zero
    /// <c>endurance</c> (division by zero); this port guards it defensively — a
    /// mine with no endurance cannot resist anything, so it is destroyed — a
    /// tiny, labelled divergence no real core record hits (<c>Endurance</c> is
    /// ~2000 on <c>PCore001.igc</c>).
    /// </remarks>
    public void ReduceStrength(float amount)
    {
        ApplyDecay(amount);
        if (_fraction < 0.25f)
            DestroyMine();
    }

    /// <summary>
    /// The P452 ship-contact attrition path — the port of the field-wear half of
    /// <c>CmineIGC::HandleCollision</c> (<c>mineigc.h:116-128</c>). Applies the
    /// same native exponential endurance decay as <see cref="ReduceStrength"/>
    /// (shared via <see cref="ApplyDecay"/>), returns the <c>damage</c> the
    /// contacting ship actually takes (<c>oldEndurance − newEndurance</c>,
    /// <c>mineigc.h:126-127</c>), and destroys the mine — removing it and
    /// raising <see cref="IIgcSite.MineDestroyedEvent"/> exactly once — when the
    /// remaining <c>newEndurance</c> drops below <c>1.0f</c>
    /// (<c>mineigc.h:139-142</c>), the native kill test (distinct from
    /// <see cref="ReduceStrength"/>'s <c>0.25f</c> fraction threshold).
    /// <see cref="Sector.ResolveMineDetonations"/> calls this once per
    /// qualifying ship per tick, then applies each ship's worst hit.
    /// </summary>
    public float TakeAttritionDamage(float amount)
    {
        var (damage, newEndurance) = ApplyDecay(amount);
        if (newEndurance < 1.0f)
            DestroyMine();
        return damage;
    }

    /// <summary>
    /// The shared native endurance-decay computation
    /// (<c>mineigc.h:120-128</c>): <c>oldEndurance = endurance · m_fraction;
    /// newEndurance = oldEndurance · exp(−amount/endurance); m_fraction =
    /// newEndurance / endurance</c>. Returns the ship-visible <c>damage</c>
    /// (the endurance consumed) and the new <c>newEndurance</c> (the
    /// <c>&lt; 1</c> kill test). A mine with no <c>Endurance</c> cannot resist
    /// anything: its fraction goes to 0 and it is destroyed (labelled
    /// divergence; no real core record hits a zero endurance).
    /// </summary>
    private (float damage, float newEndurance) ApplyDecay(float amount)
    {
        float endurance = MineType.Endurance;
        if (endurance <= 0f)
        {
            _fraction = 0f;
            return (0f, 0f);
        }
        float oldEndurance = endurance * _fraction;
        float newEndurance = oldEndurance * MathF.Exp(-amount / endurance);
        _fraction = newEndurance / endurance;
        return (oldEndurance - newEndurance, newEndurance);
    }

    /// <summary>
    /// Removes the mine from its sector and raises
    /// <see cref="IIgcSite.MineDestroyedEvent"/> exactly once — the identical
    /// pair of calls every mine-removal path (P94 expiry, P96 blast
    /// destructibility, P452 attrition wear-out) makes, reaching the same
    /// same-side audience that saw it deploy. A <see cref="Sector.Remove"/> on an
    /// already-removed mine is a harmless no-op; callers avoid re-entry (the
    /// attrition pass stops scanning a destroyed mine mid-tick).
    /// </summary>
    private void DestroyMine()
    {
        _sector.Remove(this);
        if (Launcher is Ship launcherShip)
            launcherShip.Mission?.Site.MineDestroyedEvent(this);
    }

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
            // Expiry: remove from the sector + raise MineDestroyedEvent — the
            // identical pair of calls every removal path makes, via
            // <see cref="DestroyMine"/>. Removing during this call is safe: the
            // sector ticks over a snapshot (<see cref="Sector.Update"/>), and an
            // expiry never repeats (latched by <see cref="TimeExpire"/>, monotone).
            DestroyMine();
        }
    }
}
