using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live chaff decoy as a sector model — the port of <c>CchaffIGC</c>
/// (<c>src/Igc/chaffigc.cpp</c>, 86 lines, read whole). Chaff is the
/// expendable a ship drops to break a hostile missile's lock (P182). Unlike
/// <see cref="Mine"/>/<see cref="Probe"/> it is <b>not</b> static: it coasts
/// at a constant velocity inherited from the deploying ship
/// (<c>dataChaff.v0 = myVelocity + GetUp()·5</c>,
/// <c>FedSrv/FedSrv.CPP:5684</c>; this slice simplifies to the plain ship
/// velocity — no <c>Up</c> kick, no per-hull <c>ChaffPosition</c> offset —
/// see the <see cref="Ship.DeployChaff"/> remarks), accelerates with no
/// acceleration field (<c>SetMass(0.0f)</c>, <c>chaffigc.cpp:59</c>), grows
/// its rendered <c>Radius</c> linearly from a <c>1.0</c> floor up to
/// <see cref="DataChaffTypeIGC.Radius"/> over its lifetime
/// (<c>chaffigc.cpp:82-83</c>), and <b>expires</b> after
/// <see cref="DataChaffTypeIGC.Lifespan"/> (<c>m_timeExpire = time0 +
/// lifespan</c>, <c>chaffigc.cpp:61</c>), removing itself from the sector.
/// </summary>
/// <remarks>
/// Honest-scope statement, same style as <c>Mine.cs</c>'s remarks: native
/// <c>CchaffIGC</c> is deliberately thin (existence, coast, radius-growth,
/// expiry — <c>chaffigc.cpp:75-86</c>) and this slice ports all of it, so the
/// only deliberate simplifications are the two already named on
/// <see cref="Ship.DeployChaff"/> (drop at the ship's plain <c>Position</c>
/// with the ship's plain <c>Velocity</c>, no <c>ChaffPosition</c> offset and
/// no <c>Up</c> kick) and the absence of a destruction wire message — native
/// chaff <c>Terminate</c> simply removes the model from its cluster
/// (<c>TmodelIGC&lt;IchaffIGC&gt;::Terminate</c>, <c>chaffigc.h:45-47,68-73</c>);
/// there is no <c>FM_S_CHAFF_DESTROYED</c> message in <c>Messages.h</c>, so —
/// unlike <see cref="Mine"/>/<see cref="Probe"/>/<see cref="Missile"/> — chaff
/// raises no destruction event on expiry. It also participates in no combat
/// pass: the swept detector (<c>Sector.ResolveCombatCollisions</c>) builds a
/// collider only for <c>IOrdnance</c>/<c>Ship</c>/<c>Probe</c> and falls
/// through to <c>null</c> for every other model type, so a <c>Chaff</c> in the
/// sector's model list is ignored by combat — it exists purely to be spoofed
/// onto, exactly native's role for it.
/// </remarks>
public sealed class Chaff : Model
{
    private readonly Sector _sector;
    private readonly double _timeExpire;

    // Native seeds the rendered radius to a 1.0 floor at Initialize
    // (chaffigc.cpp:50: SetRadius(1.0f)), then grows it each Update.
    private float _radius = 1.0f;

    /// <summary>Construct a chaff decoy in <paramref name="sector"/> at
    /// <paramref name="position"/> coasting at <paramref name="velocity"/>,
    /// deployed at <paramref name="now"/> with the decoded
    /// <paramref name="chaffType"/>. Its clock is seeded to
    /// <paramref name="now"/> so the first sector tick is a genuine full chunk,
    /// not one oversized step equal to the mission age (the same ship-clock fix
    /// <see cref="Projectile"/> documents, via <see cref="Model.InitClock"/>).
    /// Expiry is <c>now + chaffType.Lifespan</c> (<c>chaffigc.cpp:61</c>).</summary>
    public Chaff(
        short id,
        Sector sector,
        DataChaffTypeIGC chaffType,
        Vector3 position,
        Vector3 velocity,
        double now,
        Model? launcher = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        ChaffType = chaffType;
        Velocity = velocity;
        Launcher = launcher;
        _timeExpire = now + chaffType.Lifespan;
        InitClock(now);
    }

    /// <summary>The decoded chaff type from the static core — the source of
    /// <c>Lifespan</c> (expiry clock, <c>chaffigc.cpp:61</c>), <c>Radius</c>
    /// (the growth ceiling, <c>chaffigc.cpp:82</c>) and <c>ChaffStrength</c>
    /// (the spoof-chance numerator, read by <see cref="Ship.DeployChaff"/>).
    /// Never hardcoded; the task rule.</summary>
    public DataChaffTypeIGC ChaffType { get; }

    /// <summary>The constant coasting velocity of this chaff — set once at
    /// construction (inherited from the deploying ship) and never altered
    /// afterwards. A chaff has <b>no acceleration</b> (<c>SetMass(0.0f)</c>,
    /// <c>chaffigc.cpp:59</c>), matching <see cref="Projectile"/>'s
    /// unaccelerated straight-line flight rather than <see cref="Missile"/>'s
    /// thrust/turning.</summary>
    public Vector3 Velocity { get; }

    /// <summary>The absolute time (mission-seconds) at which this chaff expires
    /// and removes itself (<c>chaffigc.cpp:61</c>).</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this chaff lives in — the sector that owns it and that
    /// it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>The model that deployed this chaff (<c>m_launcher</c>), mirroring
    /// <see cref="Mine.Launcher"/>/<see cref="Missile.Launcher"/>. Kept for parity
    /// with the sibling ordnance/expendable types (the spoof-resolution loop in
    /// <see cref="Ship.DeployChaff"/> keys off the launching ship, not the chaff's
    /// launcher, but a caller may want to know who dropped it). Unlike
    /// <see cref="Mine"/>/<see cref="Probe"/>/<see cref="Missile"/> it is <em>not</em>
    /// used to reach a destruction-event seam — chaff has no destroy broadcast (see
    /// class remarks).</summary>
    public Model? Launcher { get; }

    /// <summary>The current rendered radius of this chaff. Starts at the <c>1.0</c>
    /// floor (<c>chaffigc.cpp:50</c>) and grows linearly toward
    /// <see cref="DataChaffTypeIGC.Radius"/> over the object's lifetime, computed in
    /// <see cref="OnUpdate"/> from the native formula <c>radius·(1 − dtLeft/lifespan)</c>
    /// floored at 1.0 (<c>chaffigc.cpp:82-83</c>).</summary>
    public float Radius => _radius;

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <summary>
    /// One chunk's work: the literal shape of <c>CchaffIGC::Update</c>
    /// (<c>chaffigc.cpp:75-86</c>) — <c>dtLeft = m_timeExpire − now</c>; on
    /// expiry, terminate (remove <c>this</c> from <see cref="OwnerSector"/>, no
    /// destroy event — native chaff has no destroy broadcast); otherwise grow the
    /// rendered <c>Radius</c> per the native formula and advance the position at
    /// the constant coasting <see cref="Velocity"/> (the <c>TmodelIGC::Update</c>
    /// position integration, <c>chaffigc.cpp:84</c>). Removing <c>this</c> from the
    /// sector during this call is safe: the sector ticks over a snapshot
    /// (<c>Sector.Update</c>), and an expiry never repeats (latched by
    /// <see cref="TimeExpire"/>, monotone).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        double dtLeft = _timeExpire - now;
        if (dtLeft <= 0.0)
        {
            _sector.Remove(this);
            return;
        }

        // Radius growth (chaffigc.cpp:82-83): r = radius·(1 − dtLeft/lifespan),
        // floored at 1.0. Guarded against a non-positive Lifespan (degenerate data;
        // a lifespan ≤ 0 would have expired above anyway, so this only protects the
        // div-by-zero for a chaff whose clock latch raced — see TimeExpire).
        float lifespan = ChaffType.Lifespan;
        if (lifespan > 0f)
        {
            float r = ChaffType.Radius * (float)(1.0 - dtLeft / lifespan);
            _radius = r > 1.0f ? r : 1.0f;
        }

        Position += Velocity * (float)dt;   // constant velocity — no acceleration (chaffigc.cpp:52,59)
    }
}
