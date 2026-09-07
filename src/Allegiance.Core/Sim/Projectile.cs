using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live projectile as a sector model — the port of <c>CprojectileIGC</c>
/// (<c>src/Igc/projectileIGC.cpp</c>, 231 lines), kept to this slice's need: a
/// real <see cref="Model"/> that lives in a <see cref="Sector"/>, moves in a
/// straight line at a constant velocity inherited from its firing ship, ticks
/// with the sector like any other model, and <b>expires</b> after its decoded
/// <see cref="DataProjectileTypeIGC.Lifespan"/>, removing itself from the sector
/// (leaked projectiles would otherwise accumulate invisibly).
/// </summary>
/// <remarks>
/// Being a <see cref="Model"/> in the sector's model list is the point: it is
/// what makes the existing collision detector <i>see</i> it — the next task
/// (damage) hooks that detector up; here the projectile only has to exist and
/// tick. Following the port rules, the expiry semantics mirror
/// <c>CprojectileIGC::Update</c> (<c>projectileIGC.cpp:120-144</c>):
/// the projectile advances <c>position += velocity·dt</c> each chunk, and on the
/// first tick whose <c>now</c> reaches its expiry time it is removed from the
/// sector. (The C++ creates a blast explosion on that tick and integrates one
/// further substep before the <c>GetMyLastUpdate() &gt;= m_timeExpire</c>
/// check kills it; blasts/explosions are explicitly out of this task's scope, so
/// the projectile just expires here — the removal is the observable contract the
/// task asserts.)
/// </remarks>
public sealed class Projectile : Model, IOrdnance
{
    private readonly Sector _sector;
    private readonly double _timeExpire;

    /// <summary>Construct a projectile in <paramref name="sector"/> at
    /// <paramref name="position"/> with <paramref name="velocity"/>, facing
    /// <paramref name="forward"/>, born at <paramref name="now"/>. Its clock is
    /// seeded to <paramref name="now"/> so the first sector tick is a genuine
    /// full chunk, not one oversized step equal to the mission age (same fix as
    /// the ship-clock bug). Expiry is <c>now + projectileType.Lifespan</c>.</summary>
    public Projectile(
        short id,
        Sector sector,
        DataProjectileTypeIGC projectileType,
        Vector3 position,
        Vector3 velocity,
        Vector3 forward,
        double now,
        Model? launcher = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        ProjectileType = projectileType;
        Velocity = velocity;
        Forward = forward.Normalize();
        Launcher = launcher;
        _timeExpire = now + projectileType.Lifespan;
        InitClock(now);
    }

    /// <summary>The decoded projectile type from the static core — the source of
    /// <c>Speed</c>, <c>Lifespan</c>, <c>Power</c>, <c>DamageType</c> &amp;c. (never
    /// hardcoded; the task rule).</summary>
    public DataProjectileTypeIGC ProjectileType { get; }

    /// <summary>The constant linear velocity of this projectile (inherited from the firing ship + weapon speed).</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The normalised heading this projectile was fired along (used to seed its orientation / collider facing).</summary>
    public Vector3 Forward { get; }

    /// <summary>The absolute time (mission-seconds) at which this projectile expires and removes itself.</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this projectile lives in — the sector that owns it and that it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <inheritdoc/>
    float IOrdnance.Radius => ProjectileType.Radius;
    /// <inheritdoc/>
    byte IOrdnance.DamageType => ProjectileType.DamageType;
    /// <inheritdoc/>
    float IOrdnance.Power => ProjectileType.Power;

    /// <summary>
    /// The model that launched this projectile (<c>m_launcher</c>,
    /// <c>projectileIGC.cpp:25</c>). Used by the combat collision pass to
    /// exclude a projectile striking its own launcher — a ship cannot damage
    /// itself — and, when the launcher is a <see cref="Ship"/> with a
    /// <see cref="Ship.SideId"/>, to enforce the friendly-fire gate against
    /// any target on the same side (<c>Sector.ResolveCombatCollisions</c>).
    /// Optional (a projectile constructed directly has no launcher).
    /// </summary>
    public Model? Launcher { get; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <summary>
    /// One chunk's work: advance the straight-line position by
    /// <c>velocity·dt</c>, and expire (remove self from <see cref="OwnerSector"/>)
    /// once <paramref name="now"/> reaches <see cref="TimeExpire"/> —
    /// <c>projectileIGC.cpp:120-144</c> minus the out-of-scope blast.
    /// Removing <c>this</c> from the sector during this call is safe: the
    /// sector ticks over a snapshot (<c>Sector.Update</c>).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        Position += Velocity * (float)dt;
        if (now >= _timeExpire)
            _sector.Remove(this);
    }
}
