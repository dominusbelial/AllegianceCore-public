namespace Allegiance.ClientCore.State;

/// <summary>
/// P264 — the client's snapshot of "an enemy missile is tracking YOU",
/// delivered by the server on the owner-only
/// <c>MISSILE_TRACK_WARNING(208)</c> message (see
/// <c>Allegiance.Core.Wire.MissileTrackWarningUpdate</c>). Plain data only, so it
/// is trivially constructible in tests; <see cref="ClientWorld"/> owns the single
/// current instance and <c>Game1</c> reads it each frame to drive the incoming-
/// missile warning loop (<c>missilelockonme</c> / native
/// <c>missileLockSound</c>) — armed while <see cref="IsTracked"/>, with the loop's
/// pitch following <see cref="BestLock"/> (native <c>SetPitch(0.75 + fBestLock/2)</c>,
/// <c>trekigc.cpp:5065</c>), disarmed otherwise.
/// </summary>
/// <param name="IsTracked">True when an in-sector enemy missile's target is the
/// local ship (the warning is armed).</param>
/// <param name="BestLock">The best lock fraction (0..1) among the missiles
/// tracking the local ship — the pitch ramp.</param>
public readonly record struct MissileTrackState(
    bool IsTracked,
    float BestLock)
{
    /// <summary>The canonical "no enemy missile targeting me" snapshot (warning clear).</summary>
    public static readonly MissileTrackState Clear = new(false, 0f);
}
