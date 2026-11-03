namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P862 — the autopilot's "did the target die, or could we just not steer this
/// frame?" rule, extracted so it is testable without a world.
///
/// <para><b>Why.</b> The client flies its own ship (P508) and engages the shared
/// native-style autopilot for a goto/order. A frame can fail to produce controls
/// for two very different reasons: the own pose is not seeded yet (a goto issued
/// on the undock frame) or the warp graph has not arrived — both TRANSIENT; or the
/// target is genuinely gone (the ship died, docked away, or left the tables).
/// The old caller treated every such frame as "TargetLost" and dropped the order,
/// which is why a scripted <c>--auto-goto</c> at launch logged
/// <c>autopilot ON … autopilot OFF (TargetLost)</c> in the same second and never
/// warped. Native's autopilot keeps the order and simply has nothing to steer
/// until the ship has a pose.</para>
/// </summary>
public static class AutopilotHold
{
    /// <summary>How long an unsteerable frame may be held while the target still
    /// exists. The transient this fixes lasts one or two frames (the undock frame
    /// has no pose yet, the warp graph lands a frame later), so two seconds is
    /// generous — beyond it the order is genuinely unsteerable (e.g. its ship
    /// docked and left the tables) and the pre-P862 disengage is the right answer.</summary>
    public const double HoldLimitMs = 2000;

    /// <summary>Whether this frame ends the engagement. A target that is no longer a
    /// live object in the world, or an order that has been unsteerable for longer
    /// than <see cref="HoldLimitMs"/>, disengages; a merely transient miss (no pose
    /// / no route yet) keeps the order.</summary>
    /// <param name="engaged">The autopilot is engaged.</param>
    /// <param name="steered">This frame produced controls (the autopilot flew).</param>
    /// <param name="targetKnown">The target still exists in the world.</param>
    /// <param name="nowMs">A monotonic millisecond clock.</param>
    /// <param name="holdSinceMs">When the current run of unsteerable frames began,
    /// or 0 when the autopilot steered last frame.</param>
    public static bool ShouldDisengage(bool engaged, bool steered, bool targetKnown,
        double nowMs, double holdSinceMs)
        => engaged && !steered
           && (!targetKnown || (holdSinceMs > 0 && nowMs - holdSinceMs >= HoldLimitMs));
}

/// <summary>
/// The per-frame hold timer behind <see cref="AutopilotHold.ShouldDisengage"/>.
/// A hold window belongs to one engagement: it restarts on every frame that steers,
/// every frame the autopilot is off, and on <see cref="Reset"/> (engage/disengage).
/// Before this, the timestamp was cleared only by a steered frame, so a hold left by
/// the eject-home flight (its last frames before docking cannot steer) survived the
/// dock, and every later goto read as two seconds overdue and logged
/// <c>autopilot ON … autopilot OFF (TargetLost)</c> on its first frame.
/// </summary>
public struct AutopilotHoldClock
{
    /// <summary>When the current run of unsteerable frames began, or 0.</summary>
    public long SinceMs { get; private set; }

    public void Reset() => SinceMs = 0;

    /// <summary>Advance one frame; true when this frame ends the engagement.</summary>
    public bool Frame(bool engaged, bool steered, bool targetKnown, long nowMs)
    {
        if (!engaged || steered)
        {
            SinceMs = 0;
            return false;
        }
        if (SinceMs == 0) SinceMs = nowMs;
        return AutopilotHold.ShouldDisengage(engaged, steered, targetKnown, nowMs, SinceMs);
    }
}
