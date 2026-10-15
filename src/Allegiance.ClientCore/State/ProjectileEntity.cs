using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live projectile instance in the client's world model — one per real
/// <c>FM_CS_PROJECTILE_INSTANCE(12)</c> broadcast (the P88 wire codec). Unlike
/// ships / stations / warps / asteroids this is a <b>transient</b> entity: the
/// wire message carries no persistent id (native projectiles are fire-and-forget
/// visual objects, not tracked entities), so a projectile is modelled simply as
/// an entry in a short-lived list that expires <see cref="Lifespan"/> seconds
/// after <see cref="ReceivedLocalMs"/> and is then pruned.
/// </summary>
/// <remarks>
/// <para><b>Why local receipt time, not the wire <c>TimeCreate</c>.</b> The
/// codec's <c>TimeCreate</c> is the server tick index (a uint in the server's
/// epoch) which shares no clock with the client, so this port anchors both
/// expiry and motion extrapolation to the local monotonic instant the message
/// was received (<see cref="Environment.TickCount64"/>) — captured in
/// <see cref="ClientWorld.Apply(ProjectileInstance)"/>. The server broadcasts
/// the instance at projectile creation, so for a same-host client receipt ≈
/// creation; anchoring on receipt is the honest client-side equivalent of
/// native's creation-time tracking.</para>
/// </remarks>
public sealed class ProjectileEntity
{
    /// <summary>The decoded projectile type id (<c>ProjectileInstance.ProjectileTypeId</c>).</summary>
    public short ProjectileTypeId;

    /// <summary>P542 — the launching ship's id (<c>ProjectileInstance.LauncherId</c>).
    /// Lets render/target/diagnostic code classify a round as own / teammate /
    /// enemy and, crucially, lets the own-shot-echo de-dup drop only our OWN
    /// server echo rather than any same-type round near us.</summary>
    public short LauncherId;
    public Allegiance.Core.IgcObjectType LauncherType = Allegiance.Core.IgcObjectType.Ship;

    /// <summary>The spawn (muzzle) position carried by the broadcast.</summary>
    public Vector3 Position;

    /// <summary>The normalised launch heading.</summary>
    public Vector3 Forward;

    /// <summary>The inherited launch velocity, units/second.</summary>
    public Vector3 Velocity;

    /// <summary>The projectile type's lifetime, seconds (<c>DataProjectileTypeIGC.Lifespan</c>).</summary>
    public float Lifespan;

    /// <summary>Local monotonic receipt instant (<see cref="Environment.TickCount64"/>),
    /// set by <see cref="ClientWorld.Apply(ProjectileInstance)"/>.</summary>
    public long ReceivedLocalMs;

    /// <summary>P542 — set by <see cref="ClientWorld.DiagnoseDraw"/> the first time
    /// this round survives the radius + camera culls in a draw pass, so the
    /// <c>[proj-diag]</c> "drawn" counters count each UNIQUE round once rather
    /// than inflating with per-frame repeats.</summary>
    public bool EverDrawn;

    /// <summary>
    /// The projectile's extrapolated world position at the given local monotonic
    /// instant — its creation position advanced along its <see cref="Velocity"/>
    /// by the elapsed seconds (clamped so it never travels before it was created).
    /// Native projectiles move linearly in vacuo over their short lifespan, so a
    /// simple <c>Position + Velocity * elapsed</c> is faithful enough for
    /// rendering and avoids a snappy per-broadcast teleport at this POC's frame
    /// rate (the brief's "simple extrapolation, no full prediction system").
    /// </summary>
    public Vector3 PositionAt(long nowLocalMs)
    {
        float elapsed = Math.Max(0f, (nowLocalMs - ReceivedLocalMs) / 1000.0f);
        return Position + Velocity * elapsed;
    }
}
