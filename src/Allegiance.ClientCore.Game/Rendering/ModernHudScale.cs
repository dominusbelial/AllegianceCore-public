namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P833 — the ONE Modern-HUD scale, derived from the display's physical density
/// (the OS desktop scale factor) instead of the window height ÷ 1080 heuristic.
///
/// Why: Modern previously scaled with <c>Math.Clamp(viewport.Height / 1080f * Size)</c>,
/// so on a tall high-DPI monitor (e.g. a 40&quot; 5K2K, 2160-tall window) every glyph
/// became ~2× a 1080p fraction of the screen, then the desktop's fractional upscale
/// on top made it ~2.5× and blurred — physically huge text and label collisions.
/// Native draws fixed-size bitmap fonts (text physically shrinks at higher
/// resolution). The desktop's OWN UI text is sized by the OS scale factor, so the
/// Modern HUD tracks that same quantity: a 1.0 desktop behaves exactly as today,
/// a 1.25 desktop scales the HUD with the desktop UI instead of with the window.
///
/// This class is pure floating-point math (no XNA/vector dependency) so the rule
/// unit-tests headlessly. Every Modern element — text, glyph/art, the gauge-cluster
/// spread, the bottom target pane, the minimap, the own-status grid — derives from
/// <see cref="Core"/> so a change in physical density scales the whole HUD together
/// and no element can drift onto another.
/// </summary>
public static class ModernHudScale
{
    /// <summary>The user Size multiplier range (widened from the old 0.75–1.5 so
    /// the pilot can compensate for very high/low density displays).</summary>
    public const float MinSize = 0.5f;
    public const float MaxSize = 2.0f;

    /// <summary>Overall scale clamps (the user Size × density product).</summary>
    public const float MinScale = 0.4f;
    public const float MaxScale = 4.0f;

    /// <summary>The authored geometry-cluster spread at the reference density
    /// (a 1080-tall window on a 1.0 desktop), which is the old <c>h/680</c> value
    /// at 1080p. Kept as the cluster base so that at <see cref="Core"/> == 1 the
    /// Modern gauge ring / target pane land EXACTLY where they do today.</summary>
    public const float ClusterAtReference = 1080f / 680f;   // 1.588…

    /// <summary>The density-derived core scale: the OS desktop scale factor
    /// times the user Size multiplier, clamped. A 1.0 desktop at Size 1.0 gives
    /// exactly 1.0 — today's behaviour on a 1080p screen is unchanged.</summary>
    public static float Core(float displayScale, float size)
    {
        float density = displayScale > 0f ? displayScale : 1f;
        float s = Math.Clamp(size, MinSize, MaxSize);
        return Math.Clamp(density * s, MinScale, MaxScale);
    }

    /// <summary>The gauge-cluster / own-status grid spread: the authored ring
    /// spread at reference density scaled by the core scale, clamped to the
    /// same band the classic skin's spread uses (1.15–2.1 at reference). The
    /// whole cluster moves together with <see cref="Core"/>, so text and
    /// geometry can never diverge at any density.</summary>
    public static float Cluster(float coreScale)
        => Math.Clamp(coreScale * ClusterAtReference, 1.046f, 2.1f);

    /// <summary>The bottom target-pane scale: the same cluster family (the pane
    /// geometry sits on the same native anchor grid as the gauges), clamped to
    /// the classic pane range.</summary>
    public static float Pane(float coreScale)
        => Math.Clamp(coreScale * ClusterAtReference, 0.837f, 2.1f);
}
