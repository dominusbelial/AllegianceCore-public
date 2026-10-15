using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live missile instance in the client's world model — one per real
/// in-flight missile, keyed by <see cref="MissileId"/> (missiles carry a real
/// ObjectID on the wire, unlike the id-less fire-and-forget
/// <see cref="ProjectileEntity"/>). Populated from the creation
/// <c>FM_CS_FIRE_MISSILE(70)</c> broadcast (identity + type + cluster + spawn
/// pose) and kept current by the per-tick <c>FM_S_MISSILE_UPDATE(205)</c> (P234),
/// which refreshes <see cref="Position"/> / <see cref="Forward"/> to the
/// server-authoritative values every sim tick — this renderer-only client has no
/// local homing simulation; it trusts
/// the server's per-tick stream rather than extrapolating a straight line.
/// </summary>
/// <remarks>
/// <para><b>Expiry.</b> The server owns expiry; a missile entity is removed by the
/// server's <c>FM_S_MISSILE_DESTROYED(83)</c> (sent on real expiry/destruction)
/// and, as a belt-and-suspenders guard against a missed destroy, by the stale
/// pruner (<see cref="ClientWorld.PruneExpiredMissiles"/>) once no
/// <see cref="MissileUpdate"/> has refreshed it for a generous horizon (longer
/// than the longest real missile lifespan, 19s for the launcher-dumbfires). The
/// per-tick 205 stream stops for an expired missile at the same instant 83 is
/// sent, so both removal paths agree.</para>
/// <para><b>Motion.</b> Unlike a projectile, no velocity extrapolation is needed
/// (and none is available — 205 carries only position/heading, not velocity):
/// the 20&nbsp;Hz per-tick stream re-positions each missile faster than this
/// POC's frame rate, so the renderer simply draws at <see cref="Position"/>.</para>
/// </remarks>
public sealed class MissileEntity
{
    /// <summary>The missile's ObjectID (<c>MissileID</c> = short, the key).</summary>
    public short MissileId;

    // FIRE_MISSILE carries these; MISSILE_UPDATE only refreshes motion.
    public short LauncherId = -1;
    public Allegiance.Core.IgcObjectType TargetType = (Allegiance.Core.IgcObjectType)(-1);
    public short TargetId = -1;

    /// <summary>The decoded missile type's ExpendableTypeId (maps to the model).</summary>
    public short TypeId;

    /// <summary>The sector (cluster) the missile lives in — the draw-loop sector
    /// filter key, exactly like <c>DataWarpIGC.ClusterId</c> / asteroids.</summary>
    public short ClusterId;

    /// <summary>The missile's current world position (server-authoritative, refreshed per tick).</summary>
    public Vector3 Position;

    /// <summary>The missile's current normalised heading — the orientation basis
    /// (nose along this direction; the client has no Up on the wire, so the roll
    /// is chosen deterministically like warps).</summary>
    public Vector3 Forward;

    /// <summary>Local monotonic receipt instant (<see cref="Environment.TickCount64"/>)
    /// of the last update that touched this entity, for the stale pruner.</summary>
    public long LastUpdatedLocalMs;

    /// <summary>P638 — <c>FM_S_MISSILE_SPOOFED(110)</c> marked this missile's
    /// lock as broken: its target was retargeted onto the most recently
    /// deployed chaff decoy (native <c>pmissile->SetTarget(m_pchaffLastCreated)</c>,
    /// appmsg.cpp:1667-1681). Because this renderer-only client has no static
    /// core and no per-missile target id, it reproduces the retarget as the
    /// chaff's world position (<c>null</c> when no spoof has been recorded),
    /// so the renderer can draw the missile aiming at the decoy. Cleared by
    /// the usual <c>FM_S_MISSILE_DESTROYED(83)</c>/stale-pruner removal.</summary>
    public Vector3? SpoofTargetPosition;
}
