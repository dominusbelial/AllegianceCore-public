using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P230/P428 — pure per-state sizing for the missile-lock indicator: the
/// (possibly animated) on-screen half-span for each of the three lock states.
/// No drawing here — just the number the renderer uses to scale the REAL lock
/// sprite (<see cref="LockTextures"/>) so the per-state behaviour is
/// unit-testable without a graphics device (same pure-piece discipline as
/// <see cref="RockMeshFactory"/>).
///
/// The P230 tint (<c>ColorFor</c>: grey→amber→red) and acquisition spin
/// (<c>RotationFor</c>) are gone as of P428: the two real original sprites
/// carry their own fixed colours (orange corner brackets; full adds the green
/// flower), so tinting them is dead — and a slow spin is meaningless on the
/// 4-fold-symmetric flower (native never rotates these assets, radarimage.cpp
/// DrawExpandedBlip). The one animation retained is a gentle scale pulse on
/// top of the real sprite so a live lock still reads as "working" rather than
/// a frozen sticker; see <see cref="HalfSizeFor"/>.
/// </summary>
public static class LockBracketStyle
{
    /// <summary>Base on-screen half-span in px of the lock indicator around its
    /// target centre (native's radar draws these as small blips; this port
    /// renders them at a readable 68px span around the projected target).</summary>
    public const float BaseHalfSize = 34f;

    /// <summary>
    /// The indicator's half-span in screen px at a given wall-clock instant.
    /// A gentle sinusoid pulse for partial/full (bigger amplitude + faster for
    /// full so an acquired lock reads as hotter), static for no-lock. Evaluated
    /// from a caller-supplied monotonic seconds so the animation is
    /// deterministic across frames of an offscreen capture. No-lock is never
    /// drawn (native shows nothing for <c>lock == 0</c>, radarimage.cpp:330),
    /// so its value is unused — kept only for API completeness.
    /// </summary>
    public static float HalfSizeFor(MissileLockFeedback.LockState state, double nowSeconds)
    {
        float pulse = state switch
        {
            MissileLockFeedback.LockState.None => 0f,
            MissileLockFeedback.LockState.Partial => 5f * (float)Math.Sin(nowSeconds * 3.0),
            _ => 9f * (float)Math.Sin(nowSeconds * 5.0),
        };
        return BaseHalfSize + pulse;
    }

    /// <summary>
    /// HUD-4 — native continuous lock sizing (radarimage.cpp:325-332): a partial
    /// lock draws the partial surface at <c>radiusBracket + 40×(1−lock)</c> so the
    /// flower visibly shrinks toward the projected target as the magazine lock
    /// builds; a full lock draws the locked surface at the target radius. The base
    /// radius is the projected object radius when known, else the established
    /// fixed base. No sinusoidal pulse (native has none).
    /// </summary>
    /// <param name="distanceToEdge">The target centre's distance to the nearest
    /// viewport edge, px (native <c>pts-&gt;GetDistanceToEdge()</c>). The base
    /// radius is clipped to <c>distanceToEdge + BorderSide</c> exactly like
    /// native's <c>radiusBracket</c> clamp (radarimage.cpp:308-319).</param>
    public static float HalfSizeForFraction(
        MissileLockFeedback.LockState state, float fraction, float? screenRadius, float distanceToEdge)
    {
        // Native radiusBracket = 1.05 × radiusObject, then clipped to the screen
        // edge so the bracket is always entirely on screen (radarimage.cpp:308-319).
        float baseRadius = screenRadius is { } radius and > 0f
            ? MathF.Max(radius * 1.05f, 8f)
            : BaseHalfSize;
        if (baseRadius > distanceToEdge + BorderSide)
            baseRadius = MathF.Max(distanceToEdge + BorderSide, 8f);
        return state switch
        {
            MissileLockFeedback.LockState.None => 0f,
            MissileLockFeedback.LockState.Partial => baseRadius + 40f * Math.Clamp(1f - fraction, 0f, 1f),
            _ => baseRadius,
        };
    }

    /// <summary>Native <c>c_flBorderSide</c> (radarimage.h:20) — the gutter
    /// added when clipping the lock/selection bracket to the screen edge.</summary>
    public const float BorderSide = 10f;
}
