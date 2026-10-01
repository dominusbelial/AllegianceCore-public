using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P463c — a one-shot <em>collision</em> event: the client-side view of a real
/// <c>COLLISION_EVENT(216)</c> broadcast, carrying exactly what the collision
/// sound and the P449b jiggle feed need. The MonoGame client's audio slice
/// drains these via <see cref="ClientWorld.TakeCollisionEvents"/> each frame,
/// plays the positional collision sound once for the collision (both parties
/// share the single impact point), and — when the local ship IS one of the two
/// parties — feeds that party's jiggle intensity into <c>FlightJiggle.SetJiggle</c>.
/// </summary>
/// <remarks>Kept as its own stream (separate from <see cref="HitSoundEntity"/>
/// and <see cref="HullHitEntity"/>) because collisions are their own wire
/// message and their own audio pass; all three are fed from distinct
/// <c>ClientWorld.Apply(...)</c> handlers.</remarks>
public readonly record struct CollisionEntity(
    short Party1Id,
    short Party2Id,
    short ClusterId,
    float Jiggle1,
    float Jiggle2,
    Vector3 Position);
