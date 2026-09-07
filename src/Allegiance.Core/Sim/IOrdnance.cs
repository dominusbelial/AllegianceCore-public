using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A piece of flying ordnance that deals single-target damage on impact — the
/// shared shape that lets <see cref="Sector.ResolveCombatCollisions"/> treat a
/// <see cref="Projectile"/> and a <see cref="Missile"/> identically without
/// giving them a common class base. Composition, not inheritance: each is a
/// port of a different, unrelated C++ class (<c>CprojectileIGC</c> vs.
/// <c>CmissileIGC</c>; see <see cref="Missile"/>'s own doc remark), and this
/// interface is deliberately <em>not</em> a way to smuggle them into one
/// hierarchy. It exists solely so the combat pass reads the same few fields
/// through one shape instead of duplicating the whole event-resolution block
/// (including the friendly-fire gate) in two places that could drift apart.
///
/// Everything the pass needs off the ordnance: its launcher (for the self-hit /
/// friendly-fire exclusions), its position/velocity (impact-point math at
/// <c>TCollision</c>), its physical radius (sphere-collider construction,
/// mirrored from the type record), and its damage type + power (passed straight
/// into <see cref="Ship.ReceiveDamage"/>). All four ordnance-specific
/// (<c>Radius</c>, <c>DamageType</c>, <c>Power</c>, plus <c>Velocity</c>) are
/// defined directly off the decoded type records, mirroring how the projectile
/// case already reads <c>ProjectileType.Radius</c> in
/// <c>Sector.ResolveCombatCollisions</c>.
/// </summary>
public interface IOrdnance
{
    /// <summary>The model that launched this ordnance (optional — a directly
    /// constructed ordnance has no launcher). The launcher excludes the
    /// self-hit and friendly-fire cases in the combat pass.</summary>
    Model? Launcher { get; }

    /// <summary>The position of this ordnance (the live <c>Model.Position</c>),
    /// used for the moving collider and for impact-point math.</summary>
    Vector3 Position { get; }

    /// <summary>The constant linear velocity of this ordnance, used for impact
    /// position math and (via the model) for the moving collider.</summary>
    Vector3 Velocity { get; }

    /// <summary>The ordnance's physical bounding radius, from its decoded type
    /// record — the <c>Radius</c> of <see cref="Projectile.ProjectileType"/> /
    /// <see cref="Missile.MissileType"/> that seeds the sphere collider.</summary>
    float Radius { get; }

    /// <summary>The decoded damage type this ordnance lands on impact.</summary>
    byte DamageType { get; }

    /// <summary>The decoded damage magnitude this ordnance lands on impact.</summary>
    float Power { get; }
}
