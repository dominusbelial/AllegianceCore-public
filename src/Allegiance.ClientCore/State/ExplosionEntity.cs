using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live explosion in the client's world model — one per real
/// <c>EXPLOSION_EVENT(207)</c> broadcast (the P263 server-to-client VFX
/// message). It is a <b>transient</b> entity: the server tells us a blast
/// happened and where, the MonoGame client plays the sprite VFX for the
/// category's real duration (<see cref="ExplosionPreset.TotalDurationSeconds"/>),
/// then it is pruned. No persistent id (explosions are fire-and-forget visuals).
/// </summary>
/// <remarks><b>Local receipt time anchoring.</b> Exactly as
/// <see cref="ProjectileEntity"/> does, the explosion anchors its age to the
/// local monotonic instant the message arrived (<see cref="Environment.TickCount64"/>),
/// captured in <see cref="ClientWorld.Apply(ExplosionEvent)"/>. The wire carries
/// no creation timestamp, and the server broadcasts at creation for a same-host
/// client, so receipt ≈ creation. The <see cref="ExplosionSampler"/> turns
/// elapsed seconds into per-frame visuals.</remarks>
public sealed class ExplosionEntity
{
    /// <summary>The blast category — one of
    /// <see cref="Allegiance.Core.Sim.ExplosionType"/>; selects the
    /// <see cref="ExplosionPresets"/> row that fixes size/duration.</summary>
    public int Type;

    /// <summary>The blast centre in world units.</summary>
    public Vector3 Position;

    /// <summary>The sector the blast occurred in — the renderer draws only
    /// explosions in the local ship's own sector (native's cluster gate).</summary>
    public short ClusterId;

    /// <summary>The emitter's world velocity, units/second — fireball drift.</summary>
    public Vector3 Velocity;

    /// <summary>Local monotonic receipt instant
    /// (<see cref="Environment.TickCount64"/>), set by
    /// <see cref="ClientWorld.Apply(ExplosionEvent)"/>.</summary>
    public long ReceivedLocalMs;
}
