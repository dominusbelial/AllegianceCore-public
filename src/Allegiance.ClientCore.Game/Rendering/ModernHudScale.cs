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

    /// <summary>P859 — the authored layout budget: the largest text scale the HUD
    /// proofs have ever verified at the 1080-tall reference window (the pre-P833
    /// Size ceiling). The HUD-2 / HUD-5 / P833 sweeps were all authored against
    /// <c>scale = 1.5</c> at <c>height = 1080</c>, so the invariant the geometry
    /// keeps is "1.5 scale units per 1080 px of window height".</summary>
    public const float MaxAuthoredScale = 1.5f;

    /// <summary>P859 — the largest scale this WINDOW can hold: the authored ratio
    /// above, i.e. <see cref="MaxAuthoredScale"/> × <c>height/1080</c>.
    ///
    /// <para>P833 correctly stopped <em>inflating</em> the HUD from window height
    /// (a 2160-tall window used to give 2× on a 1.0 desktop), but dropping the
    /// height term entirely lost the ceiling a SMALL window relied on: at
    /// 1280×720 with the widened Size 1.5 the Modern radar label block measured
    /// 582×83 px, no free zone was left between the six gauge rings, the target
    /// pane and the centred ripcord countdown (dialog.mdl:5145-5151), and
    /// <see cref="HudTextLayout"/> fell through to its native-anchor overflow —
    /// drawing the label on top of the ripcord glyphs. Native cannot collide this
    /// way: its fonts are fixed bitmaps (<c>Artwork/dialog.mdl</c>
    /// <c>smVerdana</c>/<c>lgBoldVerdana</c>), so 720p text is exactly as large as
    /// 1080p text and never grows with a user text-size setting. Restoring the
    /// ratio as a one-sided CAP keeps P833's density rule wherever the window has
    /// room for it (a 1.25 desktop still gets 1.25× on a 1080p or taller window),
    /// while a small window keeps exactly the layout it was authored for.</para></summary>
    public static float WindowFactor(float viewportHeight)
        => Math.Clamp(MathF.Max(viewportHeight, 1f) / 1080f * MaxAuthoredScale, MinScale, MaxScale);

    /// <summary>P859 — the core scale the RENDERER uses: the density rule
    /// (<see cref="Core(float,float)"/>, P833) capped by what the window can hold
    /// (<see cref="WindowFactor"/>), so no (window, density, Size) combination can
    /// inflate the HUD past the layout the proofs verified. At 1080p the cap is the
    /// pre-P833 1.5 ceiling, reached only by Size 2.0 or a 1.5+ desktop — never by
    /// the reference configuration, which stays exactly 1.0.</summary>
    public static float Core(float displayScale, float size, float viewportHeight)
        => Math.Clamp(Math.Min(Core(displayScale, size), WindowFactor(viewportHeight)), MinScale, MaxScale);

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
