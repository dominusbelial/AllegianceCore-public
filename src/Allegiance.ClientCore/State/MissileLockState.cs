using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P230 — the client's snapshot of the local ship's missile lock, delivered by the
/// server on the owner-only <c>MISSILE_LOCK_UPDATE(204)</c> message (see
/// <c>Allegiance.Core.Wire.MissileLockUpdate</c>). Plain data only, so it is trivially
/// constructible in tests; <see cref="ClientWorld"/> owns the single current instance
/// and <c>Game1</c> reads it each frame to drive the lock tone (full lock only), the
/// target-box overlay (appearance varies across the three states), and (via the
/// transition tips already posted into the chat log) the partial/full/lost texts.
/// </summary>
/// <param name="LockFraction">The lock quality 0..1 (full == 1).</param>
/// <param name="TargetId">The lock target's object id, or <c>-1</c> (<c>NA</c>) when
/// there is no current lock target.</param>
/// <param name="TargetPosition">The lock target's world position; meaningful only when
/// <see cref="HasTarget"/>.</param>
/// <param name="MissileName">The mounted missile type's display name (empty when no
/// launcher is resolved).</param>
public readonly record struct MissileLockState(
    float LockFraction,
    short TargetId,
    Vector3 TargetPosition,
    string MissileName,
    IgcObjectType TargetType = IgcObjectType.Ship,
    bool IsReady = true)
{
    /// <summary>The canonical "no lock, no target" snapshot.</summary>
    public static readonly MissileLockState None = new(0f, -1, Vector3.Zero, "", IsReady: false);

    /// <summary>True when a lock target is currently resolved.</summary>
    public bool HasTarget => TargetId != -1;

    /// <summary>True when the lock is full (<c>LockFraction &gt;= 1</c>) — the gate for
    /// native's continuous <c>missileTone</c> beep (<c>trekigc.cpp:1289</c>,
    /// <c>HasMissileLock() == (GetLock() == 1.0f)</c>, <c>trekigc.cpp:876-883</c>).</summary>
    public bool IsFullLock => IsReady && LockFraction >= 1f;

    /// <summary>True when a partial lock is in progress (building).</summary>
    public bool IsPartialLock => LockFraction is > 0f and < 1f;
}
