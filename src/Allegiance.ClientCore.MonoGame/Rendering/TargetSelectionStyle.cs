using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P345/P376 — pure styling for the player's OWN autopilot-target selection bracket
/// (the "square thing" T-selection box). Two discrete states, not a lock gradient:
/// merely SELECTED (<c>T</c> picked it, <c>G</c> not engaged) and ENGAGED (<c>G</c>
/// on, flying toward it). P376 adds a universal friend/enemy colour convention to
/// that same bracket: an ENEMY ship's box is red, a FRIENDLY (same-side) ship's box
/// is blue — independent of which side the local player is on — while a non-ship
/// target (station/asteroid/warp, no side) keeps the neutral aqua. No drawing here —
/// just the numbers the renderer plugs into
/// <see cref="HudRenderer.DrawTargetSelectionBracket"/> — so the visuals are
/// unit-testable without a graphics device (same pure-piece discipline as
/// <see cref="LockBracketStyle"/>).
///
/// The visual is deliberately DISTINCT from the missile-lock bracket
/// (<see cref="LockBracketStyle"/>): the lock box is four OPEN corner-L arms in
/// dim-grey / amber / hot-red; this is a FULL CLOSED rectangular outline in a cool
/// colour (aqua neutral / red for enemies / blue for friendlies). Different colour
/// AND different shape (closed vs open corners), so the two can be on screen at once
/// and be told apart at a glance — including when they coincide on the same object.
/// </summary>
public static class TargetSelectionStyle
{
    /// <summary>Selected but not engaged — the P345 neutral colour for a NON-SHIP
    /// target (station/asteroid/warp), which has no side: cool aqua-blue (not
    /// grey/amber/red, not the teal HUD accent, not the green/red lead ramp).</summary>
    public static readonly Color Selected = new(0.28f, 0.78f, 0.95f);

    /// <summary>Engaged — the P345 neutral colour for a NON-SHIP target: brighter
    /// icy-cyan (clearly "actively flying there").</summary>
    public static readonly Color Engaged = new(0.50f, 1.00f, 1.00f);

    /// <summary>Selected, not engaged, toward a FRIENDLY (same-side) ship — P376:
    /// clear blue. Blue for friendlies regardless of which side the local player is
    /// actually on (the simple universal red=enemy/blue=friendly convention).</summary>
    public static readonly Color FriendSelected = new(0.080f, 0.478f, 0.936f);

    /// <summary>Engaged toward a FRIENDLY (same-side) ship — P376: brighter light
    /// blue, so the flying-toward state still reads within the blue family.</summary>
    public static readonly Color FriendEngaged = new(0.494f, 0.886f, 1.000f);

    /// <summary>Selected, not engaged, toward an ENEMY (other-side) ship — P376:
    /// clear red. Deliberately a softer rose-red (NOT LockBracketStyle.Full's hot
    /// 1.0,0.2,0.1 missile-lock red) so the two coexisting boxes stay distinguishable
    /// by colour as well as by shape (closed rectangle vs open corner-L arms).</summary>
    public static readonly Color EnemySelected = new(0.922f, 0.484f, 0.426f);

    /// <summary>Engaged toward an ENEMY (other-side) ship — P376: brighter light
    /// red, so the flying-toward state reads within the red family.</summary>
    public static readonly Color EnemyEngaged = new(1.000f, 0.594f, 0.514f);

    /// <summary>The closed box's half-extent in screen px (fixed — no pulse needed
    /// for an instant selection; the size just has to read as a firm "locked on").</summary>
    public const float HalfSize = 20f;

    /// <summary>Border thickness in px — thicker while engaged so the flying-toward
    /// state pops next to a merely-selected target.</summary>
    public static float ThicknessFor(bool engaged) => engaged ? 3.2f : 2.2f;

    /// <summary>
    /// P376 — the target-selection bracket colour for a given engagement state and
    /// target side. Red family for an ENEMY ship, blue family for a FRIENDLY
    /// (same-side) ship, and the original neutral aqua family for a NON-SHIP target
    /// (no side). Within each family the engaged variant is strictly brighter, so
    /// the flying-toward distinction survives the friend/enemy recolouring.
    /// </summary>
    /// <param name="engaged">True while autopilot is engaged toward the target.</param>
    /// <param name="isEnemy">True = other-side ship (red), false = same-side ship
    /// (blue), null = non-ship target (neutral).</param>
    public static Color ColorFor(bool engaged, bool? isEnemy) => (engaged, isEnemy) switch
    {
        (true, true)  => EnemyEngaged,
        (false, true) => EnemySelected,
        (true, false) => FriendEngaged,
        (false, false) => FriendSelected,
        (true, null)  => Engaged,
        _             => Selected,
    };
}
