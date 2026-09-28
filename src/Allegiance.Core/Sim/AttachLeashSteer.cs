using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// P419 — the bounded-"leash" steering law for the <c>vizfight attach</c> mode's
/// <c>Flight</c> phase. An <c>attach</c> bot otherwise cruises forward
/// indefinitely under a pure sinusoidal weave (positive throttle every tick),
/// so the fleet "goes to infinity" with nothing pulling it back. This controller
/// wraps that ambient weave in a hard-but-soft boundary: below
/// <paramref name="leashRadius"/> from the fleet anchor it hands the weave
/// through unchanged; past it, it blends in a real "steer back toward the
/// anchor" yaw term proportional to how far the ship has overshot the threshold
/// (capped), so ships visibly arc back rather than snap or teleport.
///
/// The steering signal is the exact same continuous cross-product turn law the
/// sim's own NPC autopilot uses (<c>Ship.SteerToward</c>, Ship.cs:4911-4952):
/// <c>body = Orientation.TimesInverse(cross(GetForward(), dirToAnchor))</c>,
/// whose up-component drives yaw — continuous through the target-astern case,
/// so it never stalls pointing the wrong way. Kept as a pure static function
/// (no server, no wire) so the activation-threshold and distance-reduction
/// properties are directly unit-testable (P419 verification requirement).
/// </summary>
public static class AttachLeashSteer
{
    /// <summary>Turn gain — the same 2.0 used by <c>Ship.SteerToward</c>
    /// (<c>AutopilotTurnGain</c>, Ship.cs:4131) so the leash turns at the
    /// autopilot's rate, not a new tuned constant.</summary>
    public const float SteerGain = 2.0f;

    /// <summary>
    /// The normalized-signed length of the blend zone (fraction of
    /// <paramref name="leashRadius"/>) over which the steer term ramps from 0
    /// (exactly at the threshold) to full (well past it). Bigger = gentler
    /// onset; smaller = snappier but still not instant.
    /// </summary>
    public const float BlendZoneFraction = 0.5f;

    /// <summary>Absolute floor (sim units) for the blend zone, so a tiny leash
    /// radius still gets a numerically sane ramp.</summary>
    public const float MinBlendZone = 50f;

    /// <summary>Throttle commanded once firmly outside the leash, while the ship
    /// arcs back (always positive — it must keep flying, never stall at full
    /// deflection). Between the weave's ambient throttle and this as it ramps.</summary>
    public const float OutOfBoundsThrottle = 0.75f;

    /// <summary>
    /// Produce the (yaw, throttle) controls for one bot for this tick.
    /// <paramref name="pos"/> is the bot's live position, <paramref name="anchor"/>
    /// the fleet's fixed home point, <paramref name="leashRadius"/> the stay-inside
    /// radius, <paramref name="orient"/> the bot's current attitude, and
    /// <paramref name="weaveYaw"/>/<paramref name="weaveThrottle"/> the ambient
    /// cruise signal that applies freely inside the radius.
    /// </summary>
    public static (float Yaw, float Throttle) Steer(
        Vector3 pos, Vector3 anchor, float leashRadius,
        Orientation orient, float weaveYaw, float weaveThrottle)
    {
        Vector3 toAnchor = anchor - pos;
        float dist = toAnchor.Length();

        // Inside the boundary (or coincident with the anchor): the weave passes
        // through untouched — the leash is INACTIVE. This is the "below the
        // threshold radius, keep the existing weave/cruise feel unchanged" rule.
        if (dist <= leashRadius || dist <= 1e-3f)
            return (weaveYaw, weaveThrottle);

        // Overshoot fraction, normalized over the blend zone and capped at 1:
        // how strongly the leash is engaged right now (0 at the edge, 1 deep
        // past it). This is the "proportional to how far past the threshold,
        // capped" term from the task.
        float blendZone = Math.Max(leashRadius * BlendZoneFraction, MinBlendZone);
        float push = Math.Clamp((dist - leashRadius) / blendZone, 0f, 1f);

        // Continuous steer-back signal toward the anchor (NPC-autopilot law).
        var dir = toAnchor / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float steerYaw = Math.Clamp(SteerGain * body.Y, -1f, 1f);

        // Blend: the further past the threshold, the more the steer term wins
        // over the ambient weave's lateral wander, and the throttle firms up
        // toward the out-of-bounds cruise value so the ship keeps arcing.
        float yaw = weaveYaw + (steerYaw - weaveYaw) * push;
        float throttle = weaveThrottle + (OutOfBoundsThrottle - weaveThrottle) * push;
        return (yaw, throttle);
    }
}
