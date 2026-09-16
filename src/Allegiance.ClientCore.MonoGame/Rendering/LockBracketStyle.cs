using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P230 — pure per-state styling for the missile-lock target box: the tint and
/// the (possibly animated) half-size and rotation for each of the three lock
/// states. No drawing here — just the numbers the renderer plugs into
/// <see cref="HudRenderer.DrawLockBracket"/> — so the per-state behaviour is
/// unit-testable without a graphics device (same pure-piece discipline as
/// <see cref="RockMeshFactory"/>). The visual contract the brief asks for:
/// <b>no lock</b> = dim, static; <b>partial</b> = amber, gently pulsing scale;
/// <b>full</b> = hot red, stronger pulse + a slow acquisition spin.
/// </summary>
public static class LockBracketStyle
{
    /// <summary>No lock — dim steel grey, static.</summary>
    public static readonly Color NoLock = new(0.45f, 0.485f, 0.515f);
    /// <summary>Partial lock — amber (locking in).</summary>
    public static readonly Color Partial = new(1.00f, 0.72f, 0.10f);
    /// <summary>Full lock — hot red (threat acquired).</summary>
    public static readonly Color Full = new(1.00f, 0.20f, 0.10f);

    /// <summary>The bracket tint for a lock state.</summary>
    public static Color ColorFor(MissileLockFeedback.LockState state) => state switch
    {
        MissileLockFeedback.LockState.None => NoLock,
        MissileLockFeedback.LockState.Partial => Partial,
        _ => Full,
    };

    /// <summary>The bracket's half-size in screen px at a given wall-clock instant.
    /// A sinusoid pulse for partial/full (bigger amplitude + faster for full),
    /// static for no-lock. Evaluated from a caller-supplied monotonic seconds so the
    /// animation is deterministic across frames of an offscreen capture.</summary>
    public static float HalfSizeFor(MissileLockFeedback.LockState state, double nowSeconds)
    {
        float pulse = state switch
        {
            MissileLockFeedback.LockState.None => 0f,
            MissileLockFeedback.LockState.Partial => 5f * (float)Math.Sin(nowSeconds * 3.0),
            _ => 9f * (float)Math.Sin(nowSeconds * 5.0),
        };
        return 34f + pulse;
    }

    /// <summary>The bracket's rotation in radians about its centre at a wall-clock
    /// instant — a slow acquisition spin for full lock only (native's "animated
    /// squares around the target ship"), none otherwise.</summary>
    public static float RotationFor(MissileLockFeedback.LockState state, double nowSeconds) =>
        state == MissileLockFeedback.LockState.Full ? (float)(nowSeconds * 0.7) : 0f;
}
