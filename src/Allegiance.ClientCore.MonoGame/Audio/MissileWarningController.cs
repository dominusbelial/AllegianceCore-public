namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="MissileWarningController"/> wants taken on the
/// incoming-missile warning loop this frame.</summary>
public enum MissileWarningAction
{
    /// <summary>Nothing changed; no directive.</summary>
    None,

    /// <summary>An enemy missile just began targeting the local ship → start the
    /// warning loop at the current best-lock pitch (edge-triggered, once).</summary>
    Arm,

    /// <summary>The warning is still armed → refresh the loop's pitch to the
    /// current best lock (native updates pitch every frame while active,
    /// <c>trekigc.cpp:5065</c>).</summary>
    Pitch,

    /// <summary>No enemy missile targets the local ship anymore (destroyed /
    /// expired / lost lock / player left the sector) → stop the warning loop.</summary>
    Disarm,
}

/// <summary>A per-frame directive for the incoming-missile warning loop, emitted
/// by <see cref="MissileWarningController.Plan"/>.</summary>
/// <param name="Action"><see cref="MissileWarningAction"/>.</param>
/// <param name="BestLock">The best lock fraction (0..1) among the missiles
/// tracking the local ship — the pitch ramp (<c>0.75 + bestLock/2</c>).
/// Meaningful for <see cref="MissileWarningAction.Arm"/> and
/// <see cref="MissileWarningAction.Pitch"/>.</param>
public readonly record struct MissileWarningCommand(
    MissileWarningAction Action,
    float BestLock);

/// <summary>
/// P264 — pure, MonoGame-free transition tracker for the incoming-missile
/// warning loop (<c>missilelockonme</c> / native <c>missileLockSound</c>). Fed
/// each frame the server-streamed armed state + best lock (from the owner-only
/// <c>MISSILE_TRACK_WARNING(208)</c>), it emits exactly one
/// <see cref="MissileWarningAction.Arm"/> on the not-tracked→tracked edge, a
/// <see cref="MissileWarningAction.Pitch"/> each subsequent armed frame, and one
/// <see cref="MissileWarningAction.Disarm"/> on the tracked→not-tracked edge —
/// the state-based, never-re-fired-per-frame semantics native's own client uses
/// (<c>if (!m_psoundMissileWarning) start; SetPitch(...)</c>,
/// <c>trekigc.cpp:5056-5065</c>, disarm at <c>5067-5071</c>).
/// </summary>
public sealed class MissileWarningController
{
    private bool _prevTracked;

    /// <summary>Whether the warning is currently considered armed (internal
    /// edge tracker).</summary>
    public bool IsArmed => _prevTracked;

    /// <summary>
    /// Compute this frame's warning-loop directive from the server-streamed
    /// state. Returns at most one command; an un-changed clear or armed state
    /// emits <see cref="MissileWarningAction.None"/> except the per-frame pitch
    /// refresh while armed.
    /// </summary>
    public MissileWarningCommand Plan(bool isTracked, float bestLock)
    {
        if (isTracked && !_prevTracked)
        {
            _prevTracked = true;
            return new MissileWarningCommand(MissileWarningAction.Arm, bestLock);
        }
        if (isTracked) // && _prevTracked
        {
            // Native sets the pitch every frame while the warning is active
            // (trekigc.cpp:5065) so the ramp follows the improving lock.
            return new MissileWarningCommand(MissileWarningAction.Pitch, bestLock);
        }
        if (_prevTracked)
        {
            _prevTracked = false;
            return new MissileWarningCommand(MissileWarningAction.Disarm, 0f);
        }
        return new MissileWarningCommand(MissileWarningAction.None, 0f);
    }

    /// <summary>The warning loop's pitch ramp: native's
    /// <c>SetPitch(0.75 + fBestLock/2)</c> (<c>trekigc.cpp:5065</c>), with
    /// <paramref name="bestLock"/> clamped to 0..1 → pitch 0.75..1.25.</summary>
    public static float PitchFor(float bestLock) =>
        0.75f + Math.Clamp(bestLock, 0f, 1f) * 0.5f;
}
