using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P345 — pure styling for the player's OWN autopilot-target selection bracket
/// (the "square thing" T-selection box). Two discrete states, not a lock gradient:
/// merely SELECTED (<c>T</c> picked it, <c>G</c> not engaged) and ENGAGED (<c>G</c>
/// on, flying toward it). No drawing here — just the numbers the renderer plugs
/// into <see cref="HudRenderer.DrawTargetSelectionBracket"/> — so the visuals are
/// unit-testable without a graphics device (same pure-piece discipline as
/// <see cref="LockBracketStyle"/>).
///
/// The visual is deliberately DISTINCT from the missile-lock bracket
/// (<see cref="LockBracketStyle"/>): the lock box is four OPEN corner-L arms in
/// dim-grey / amber / hot-red; this is a FULL CLOSED rectangular outline in a cool
/// aqua-blue. Different colour AND different shape (closed vs open corners), so the
/// two can be on screen at once and be told apart at a glance — including when they
/// coincide on the same object.
/// </summary>
public static class TargetSelectionStyle
{
    /// <summary>Selected but not engaged — cool aqua-blue (not grey/amber/red,
    /// not the teal HUD accent, not the green/red lead ramp).</summary>
    public static readonly Color Selected = new(0.28f, 0.78f, 0.95f);

    /// <summary>Engaged — brighter icy-cyan (clearly "actively flying there").</summary>
    public static readonly Color Engaged = new(0.50f, 1.00f, 1.00f);

    /// <summary>The closed box's half-extent in screen px (fixed — no pulse needed
    /// for an instant selection; the size just has to read as a firm "locked on").</summary>
    public const float HalfSize = 20f;

    /// <summary>Border thickness in px — thicker while engaged so the flying-toward
    /// state pops next to a merely-selected target.</summary>
    public static float ThicknessFor(bool engaged) => engaged ? 3.2f : 2.2f;
}
