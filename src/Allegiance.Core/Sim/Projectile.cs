using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>A ballistic round. Native Igc/projectileIGC.cpp:120-143 creates
/// the burnout blast at the exact expiry position, advances through the tick,
/// then terminates on the following update.</summary>
public sealed class Projectile : Model, IOrdnance
{
    private readonly Sector _sector;
    private readonly double _timeExpire;
    private bool _terminated;

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
        Model? launcher = null,
        float? lifespanOverride = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        ProjectileType = projectileType;
        Velocity = velocity;
        Forward = forward.Normalize();
        Launcher = launcher;
        // Expiry defaults to the decoded lifespan; a weapon may override it to
        // intercept a target (native CweaponIGC::FireWeapon rewrites
        // dataProjectile.lifespan with the target-intercept time for blast
        // weapons, weaponIGC.cpp:273-288) — the P109 turret-gunner aim slice.
        _timeExpire = now + (lifespanOverride ?? projectileType.Lifespan);
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
    /// <inheritdoc/>
    float IOrdnance.BlastPower => ProjectileType.BlastPower;
    /// <inheritdoc/>
    float IOrdnance.BlastRadius => ProjectileType.BlastRadius;

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

    /// <summary>
    /// P491 — the armed probe that fired this round, when it came from one
    /// (else <c>null</c> for a ship-fired round). Native launches a probe's
    /// round with the probe itself as <c>m_launcher</c> (probeigc.cpp:500), so
    /// the round is excluded from colliding with its own source by the
    /// self-guard; this port keeps the deployer ship as <see cref="Launcher"/>
    /// so the ship-based broadcast path (and GA/friendly-fire resolution) stays
    /// intact, and tags the originating probe here so
    /// <c>Sector.ResolveCombatCollisions</c>'s probe branch can pass the round
    /// through its own body to the real target — the faithful self-exclusion,
    /// relocated from the launcher field to this tag.</summary>
    public Model? OriginProbe { get; set; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <summary>Native Igc/projectileIGC.cpp:120-143.</summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (_terminated) return;
        if (LastUpdate >= _timeExpire)
        {
            _terminated = true;
            _sector.Remove(this);
            return;
        }
        if (now >= _timeExpire && ProjectileType.BlastPower != 0)
        {
            var expiryPos = Position + Velocity * (float)(_timeExpire - LastUpdate);
            float damageScale = (Launcher as Ship)?.GlobalAttributeFactor(GlobalAttributeSet.DamageGuns) ?? 1;
            _sector.ResolveExplosion(new Explosion(ProjectileType.DamageType,
                ProjectileType.BlastPower * damageScale, ProjectileType.BlastRadius,
                _timeExpire, expiryPos, Launcher), _timeExpire);
        }
        Position += Velocity * (float)dt;
    }
}
