using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A transient projectile-impact spark in the client's world model — one per
/// real <c>PROJECTILE_HULL_HIT(213)</c> broadcast (the P364 server-to-client VFX
/// message). The server tells us a weapon shot connected on a damagable target,
/// where, along which surface normal, and at what target radius; the MonoGame
/// client plays the two-bit impact spark (<c>Rendering/HullHitSparkParticles</c>)
/// for the bits' real ~2&#8209;s lifetime, then it is pruned. No persistent id
/// (impact sparks are fire-and-forget visuals).
/// </summary>
/// <remarks><b>Local receipt time anchoring.</b> Exactly as
/// <see cref="ExplosionEntity"/> does, the spark anchors its age to the local
/// monotonic instant the message arrived (<see cref="Environment.TickCount64"/>),
/// captured in <see cref="ClientWorld.Apply(ProjectileHullHit)"/>. The wire carries
/// no creation timestamp, and the server broadcasts at the hit for a same-host
/// client, so receipt ≈ the hit instant.</remarks>
public sealed class HullHitEntity
{
    /// <summary>The object id of the struck target (ship/probe/station), or -1
    /// (NA) — informational; not required to render the spark.</summary>
    public short TargetId;

    /// <summary>The sector the strike occurred in — the renderer draws only
    /// sparks in the local ship's own sector (the same cluster gate the
    /// explosion renderer applies).</summary>
    public short ClusterId;

    /// <summary>The struck target's collision radius in world units; the spark
    /// sizes each bit <c>radius / 2</c> (native <c>thinggeo.cpp:1328</c>).</summary>
    public float Radius;

    /// <summary>The world-space impact point (native <c>position1</c>,
    /// <c>projectileIGC.cpp:174</c>).</summary>
    public Vector3 Position;

    /// <summary>The world-space hit-surface normal (native
    /// <c>normalize(position1 − position2)</c>, <c>projectileIGC.cpp:225</c>);
    /// the spark bits launch roughly along it.</summary>
    public Vector3 Normal;

    /// <summary>P463 — whether the strike was absorbed by the target's shield
    /// (the wire's <see cref="ProjectileHullHit.AbsorbedByShield"/>). When true
    /// the renderer must NOT spawn the hull-impact spark (native fires
    /// <c>AddHullHit</c> only for hull-or-worse damage, projectileIGC.cpp:193);
    /// instead it is the P463b shield-flare signal. The hit SOUND still plays in
    /// both cases, choosing shield vs hull by this flag.</summary>
    public bool AbsorbedByShield;

    /// <summary>Local monotonic receipt instant
    /// (<see cref="Environment.TickCount64"/>), set by
    /// <see cref="ClientWorld.Apply(ProjectileHullHit)"/>.</summary>
    public long ReceivedLocalMs;
}
