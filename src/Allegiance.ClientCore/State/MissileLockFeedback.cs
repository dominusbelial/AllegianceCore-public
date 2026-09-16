namespace Allegiance.ClientCore.State;

/// <summary>
/// P230 — pure, side-effect-free classification of a missile-lock value into a
/// display state and the edge-triggered console tip for a state transition.
/// No networking, no MonoGame — just the lock-tip rules, so they are unit-testable
/// in isolation (the same pure-piece discipline as <c>Rendering.ChatLogFormat</c>).
///
/// This is the port of native's lock-tip block (<c>WinTrek/console.cpp:929-946</c>),
/// which reads the local magazine's <c>GetLock()</c> every frame and posts a
/// console tip exactly when the lock <em>crosses</em> a threshold:
/// <code>
///   if (lock in (0,1) && !(prev in (0,1)))            → "Partial missile lock…"
///   else if (lock == 1 && prev != 1)                   → "Missile lock acquired…"
///   else if (lock == 0 && prev != 0 && !targetChanged) → "Missile lock lost."
/// </code>
/// The fire key in the port is <c>Left Ctrl</c> (P226 chose it), so the "acquired"
/// tip says that instead of native's right-mouse-button. The <c>m_fTargetChanged</c>
/// suppression of "lost" is ported: switching to a different target must not also
/// nag "lock lost".
/// </summary>
public static class MissileLockFeedback
{
    /// <summary>The three lock display states.</summary>
    public enum LockState
    {
        /// <summary>No lock (value 0).</summary>
        None,
        /// <summary>Partial lock, building (0 &lt; value &lt; 1).</summary>
        Partial,
        /// <summary>Full lock (value == 1).</summary>
        Full,
    }

    /// <summary>Classify a 0..1 lock value into a display state. Full means
    /// <c>&gt;= 1</c> (the server clamps to exactly 1).</summary>
    public static LockState Classify(float lockValue) =>
        lockValue >= 1f ? LockState.Full : lockValue > 0f ? LockState.Partial : LockState.None;

    /// <summary>
    /// The console tip to post for a transition, or <c>null</c> when the transition
    /// posts nothing (holding a state, or a target-change that suppressed "lost").
    /// Exact port of <c>console.cpp:933-945</c>; the missile type name is interpolated
    /// into the "acquired" line (native <c>GetMissileType()-&gt;GetName()</c>).
    /// </summary>
    /// <param name="prev">The previous frame's lock state.</param>
    /// <param name="curr">The current frame's lock state.</param>
    /// <param name="targetChanged">True when the lock target's identity changed since
    /// the previous frame (the port of native's <c>m_fTargetChanged</c> flag).</param>
    /// <param name="missileName">The mounted missile type's display name.</param>
    public static string? TipFor(LockState prev, LockState curr, bool targetChanged, string missileName) => curr switch
    {
        LockState.Partial when prev != LockState.Partial =>
            "Partial missile lock - center the target on screen to improve the lock.",
        LockState.Full when prev != LockState.Full =>
            $"Missile lock acquired - press Left Ctrl to fire a {missileName} missile at the target.",
        LockState.None when prev != LockState.None && !targetChanged =>
            "Missile lock lost.",
        _ => null,
    };
}
