using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1037 — the ONE closure (range-rate) derivation shared by the projected
/// selection bracket (<see cref="HudTarget"/>) and the bottom target pane
/// (<see cref="HudTargetPane"/>), so a target that leaves the view keeps exactly
/// the same reading (P972 §4.3: "Add optional <c>ClosingSpeed</c> to
/// <c>HudTargetPane</c> from that same shared derivation so an off-screen target
/// does not gain/lose the reading based on projection").
/// <para><b>Sign convention (Jet and Modern both read it this way):</b>
/// <c>positive = separation decreasing</c>. The value is
/// <c>(ownVelocity − targetVelocity) · (targetPosition − ownPosition) / distance</c>,
/// i.e. the closing component of the relative velocity along the line of sight —
/// the exact expression <c>Game1.ComputeTargetSelectionBracket</c> has always used
/// (<c>Game1.Camera.cs:179-183</c>), now named once. Since
/// <c>d(distance)/dt = −(ownVelocity − targetVelocity)·delta / distance</c>, a
/// positive value is decreasing separation; no display-side sign flip is needed.
/// </para>
/// <para>Unknown (no positions, or a degenerate zero distance) stays <c>null</c>,
/// so a renderer leaves the field blank rather than printing an invented zero.
/// Station/asteroid/warp targets pass a zero target velocity (native has no
/// per-frame velocity for a static model, <c>TargetPaneModel.TryTargetPose</c>).</para>
/// </summary>
public static class TargetClosingSpeed
{
    /// <summary>The smallest distance treated as a real line of sight; below it the
    /// direction is meaningless and the reading is unknown (<c>null</c>).</summary>
    public const float MinimumDistance = 0.001f;

    /// <summary>
    /// The signed closure in m/s — positive when the separation is decreasing —
    /// or <c>null</c> when the line of sight is degenerate. Allocation-free.
    /// </summary>
    public static float? Compute(SimVec ownPosition, SimVec ownVelocity,
        SimVec targetPosition, SimVec targetVelocity)
    {
        SimVec delta = targetPosition - ownPosition;
        float distance = delta.Length();
        if (distance <= MinimumDistance) return null;
        return ((ownVelocity - targetVelocity) * delta) / distance;
    }
}
