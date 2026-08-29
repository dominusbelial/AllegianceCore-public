namespace Allegiance.Core.Collision;

/// <summary>
/// A detected collision between two colliders at a step-relative time. The
/// ordering key is <see cref="TCollision"/> — the queue sorts ascending on it
/// (the C++ <c>CollisionEntry::comp</c>, <c>CollisionEntry.cpp:31-39</c>).
/// <see cref="HtsA"/>/<see cref="HtsB"/> are the concrete sub-shapes used
/// (a convex-hull sub-index, or a primitive shape). This is a <b>report only</b>
/// record — no damage, no destruction (out of scope).
/// </summary>
public readonly record struct CollisionEvent(Collider A, Collider B, float TCollision, int HtsA, int HtsB);
