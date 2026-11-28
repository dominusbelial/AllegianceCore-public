using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1107 — the pure geometry of the compact Modern HUD (<c>visuals.compactHud</c>).
///
/// <para><b>Why this exists.</b> Domingo, flying the 20v20 on the Modern HUD at
/// 1920x1080 (2026-11-28): the own-ship status ring (six 100 % rings with a label
/// under each) sits around the crosshair and the bottom target pane draws its own
/// ARMOR / SHIELD labels with ring-like gauges in the same font, so the two blocks
/// read like duplicate armour/shield gauges. Nothing is removed — the target pane's
/// content is native (<c>Artwork/dialog.mdl:4105-4142</c> draws ARMOR/SHIELD and the
/// hull/shield gauges unconditionally; <c>WinTrek/consoledata.cpp:202-212</c> returns
/// 0 % for a non-damageable or out-of-sector target) — this is a proportion pass.</para>
///
/// <para><b>One table, no per-element plumbing.</b> Every number lives here so the
/// renderer, the tests and the proof harness cannot drift, and the flag-off path is
/// the identity: <see cref="RingRadius"/>/<see cref="StrokeWidth"/>/<see
/// cref="GlowWidth"/>/<see cref="Spread"/>/<see cref="LabelGap"/> all return exactly
/// today's values when <c>compact</c> is false (pinned by
/// <c>P1107CompactHudTests.Flag_off_returns_todays_geometry_exactly</c>).</para>
///
/// <para><b>Classic is untouched.</b> Only <see cref="ModernHudRenderer"/> reads this
/// class; the classic <see cref="HudRenderer"/> path never sees a
/// <see cref="VisualOptions"/> at all.</para>
/// </summary>
public static class CompactHudLayout
{
    /// <summary>Ring radius as a fraction of the current 32 px-at-scale-1 ring, so a
    /// compact ring is 24 px paint radius (48 px outer diameter) at scale 1 — 75 %
    /// of today's 64 px.</summary>
    public const float RingFraction = .75f;

    /// <summary>Cluster spread as a fraction of <see cref="ModernHudScale.Cluster"/>:
    /// 1.3494 at scale 1 instead of 1.5882, so the six gauges move ~15 % toward the
    /// centre. With the 75 % rings the whole compact annulus sits INSIDE today's ring
    /// band, i.e. it can only cover less screen (see the report's clearance table).</summary>
    public const float ClusterFraction = .85f;

    /// <summary>One font step down: the bundled HUD font is baked at 14 px, and the
    /// compact step is 11/14 (the native smVerdana/medVerdana relationship is 1/1.25 =
    /// 0.8, so this is the same one-step ratio) — 11 px of the authored 14 px.</summary>
    public const float SecondaryStep = 11f / 14f;

    /// <summary>The stated minimum rendered text height: a label's layout box
    /// (<c>SpriteFont.LineSpacing × draw scale</c>, the height the HUD reserves and
    /// the eye reads as one line of text) never drops below this. At 1280x720 with
    /// HUD size 100 % the compact step renders a 13.4 px box (see
    /// <see cref="SecondaryScale"/>), i.e. 11 px still holds; a second step (0.63)
    /// would fall to 10.7 px and is refused by
    /// <c>P1107CompactHudTests.Minimum_text_height_is_honoured</c>.</summary>
    public const float MinTextHeightPx = 11f;

    /// <summary>Label gap under its ring, as a fraction of today's 8 px-at-scale-1 gap
    /// (3 px compact: the label hugs the ring instead of floating below it).</summary>
    public const float LabelGapFraction = 3f / 8f;

    /// <summary>Ring stroke thickness at scale 1 (today 2 px).</summary>
    public const float StrokePx = 1.5f;

    /// <summary>Ring glow (the wide translucent arc) at scale 1 (today 6 px).</summary>
    public const float GlowPx = 4f;

    /// <summary>Track (unfilled arc) thickness at scale 1 (today 2 px).</summary>
    public const float TrackPx = 1.5f;

    /// <summary>Target hull/shield bar: native pane-local size. Slim by design — a
    /// 5-native-px-tall horizontal bar can never be mistaken for a 48 px own-ship
    /// ring, which was the whole point of the change.</summary>
    public const float BarHeight = 5f;
    public const float BarWidth = 80f;
    /// <summary>Gap between a bar and its ARMOR/SHIELD label.</summary>
    public const float BarLabelGap = 4f;
    /// <summary>Native pane-local row the bars live on: the same y the ring gauges
    /// used (<c>dialog.mdl:4217-4242</c>, the hull gauge at local (276.5, 83.5)).</summary>
    public const float BarCentreY = 83.5f;
    /// <summary>Native pane-local right edge of the LEFT (ARMOR) group: the target
    /// model preview starts at local x 270 (HUD-21 <see cref="TargetModelPreview"/>),
    /// so the group stops 4 px short of it.</summary>
    public const float LeftGroupRight = 266f;
    /// <summary>Native pane-local left edge of the RIGHT (SHIELD) group: the preview
    /// ends at local x 366, so the group starts 4 px after it.</summary>
    public const float RightGroupLeft = 370f;

    /// <summary>Is the compact Modern HUD requested? A null option set (a renderer
    /// built by a proof/fixture that never resolved the profile) reads OFF.</summary>
    // No settings object (renderers built directly by tests/proofs) = the pre-P1107 Modern
    // layout; the desktop and browser clients pass their live VisualOptions, whose unset
    // key reads the table default (ON since 2026-11-28).
    public static bool Enabled(VisualOptions? visuals) => visuals?.IsOn(VisualFlags.CompactHudKey) ?? false;

    /// <summary>The gauge-cluster spread.
    /// <para>Off: exactly <see cref="ModernHudScale.Cluster"/> (today's 1.588-band).
    /// On: the same authored ratio narrowed by <see cref="ClusterFraction"/>, computed
    /// from the CORE scale rather than from the already-clamped cluster value. That
    /// matters at large HUD sizes: <see cref="ModernHudScale.Cluster"/> stops at 2.1
    /// while the ring radius keeps growing, so at HUD size 200 % the shipped rings
    /// overlap each other (measured by <c>p1107-compact-hud-proof</c> at 5120x2160: four
    /// ring/ring and two ring/label intersections before P1107). Keeping the compact
    /// spread proportional to the ring keeps the ring-to-spacing ratio — and therefore
    /// the clearances — constant at every scale. The 1.046 lower floor is the classic
    /// skin's small-window floor, shared with <see cref="ModernHudScale.Cluster"/>.</para></summary>
    public static float Spread(float coreScale, bool compact)
        => compact
            ? MathF.Max(1.046f, coreScale * ModernHudScale.ClusterAtReference * ClusterFraction)
            : ModernHudScale.Cluster(coreScale);

    /// <summary>Ring paint radius in px: <c>32 × scale</c> today, 24 at scale 1 compact.</summary>
    public static float RingRadius(float scale, bool compact)
        => compact ? 32f * scale * RingFraction : 32f * scale;

    /// <summary>Ring stroke in px: <c>2 × scale</c> today, 1.5 at scale 1 compact.</summary>
    public static float StrokeWidth(float scale, bool compact) => compact ? StrokePx * scale : 2f * scale;

    /// <summary>The wide translucent glow arc in px: 6 × scale today.</summary>
    public static float GlowWidth(float scale, bool compact) => compact ? GlowPx * scale : 6f * scale;

    /// <summary>
    /// Half-extent of the PAINTED ring box, used for the layout reservation. The
    /// arc's widest stroke is the translucent glow (<see cref="GlowWidth"/>), so the
    /// reservation must reach <c>radius + glow/2 + 1</c> or measured text can land on
    /// the ring's outermost pixel (the proof caught exactly that: the systems-panel
    /// heading landed 1 px inside the energy ring's glow at 1920x1080 HUD 200 %).
    /// Off: today's fixed 35 × scale half-extent, unchanged.
    /// </summary>
    public static float RingExtent(float scale, bool compact)
        => compact ? RingRadius(scale, true) + GlowWidth(scale, true) / 2f + 1f : 35f * scale;

    /// <summary>The unfilled track arc in px: 2 × scale today.</summary>
    public static float TrackWidth(float scale, bool compact) => compact ? TrackPx * scale : 2f * scale;

    /// <summary>Gap between a ring's bottom and its label's top: 8 × scale today.</summary>
    public static float LabelGap(float scale, bool compact) => compact ? 8f * scale * LabelGapFraction : 8f * scale;

    /// <summary>
    /// The draw scale of SECONDARY text (gauge labels, own-status readouts, the target
    /// pane's type/SPEED/TTI/RANGE lines, the ARMOR/SHIELD labels).
    /// <para>Compact applies <see cref="SecondaryStep"/>, but never below the stated
    /// minimum: the floor is <c>MinTextHeightPx / lineSpacing</c>, capped at the
    /// current scale so a small HUD keeps the user's HUD-size choice instead of
    /// inflating one font above the others. At a 14 px font (LineSpacing 17) the floor
    /// bites below scale 0.65; above it the compact step is the smaller of the two.</para>
    /// </summary>
    public static float SecondaryScale(float scale, float lineSpacing, bool compact)
    {
        if (!compact) return scale;
        float floor = lineSpacing > 0f ? MinTextHeightPx / lineSpacing : 0f;
        return MathF.Max(scale * SecondaryStep, MathF.Min(scale, floor));
    }

    /// <summary>The rendered height of one line of text at <paramref name="drawScale"/>.</summary>
    public static float TextHeight(float lineSpacing, float drawScale) => lineSpacing * drawScale;

    /// <summary>Does a text element at <paramref name="drawScale"/> honour the stated
    /// minimum? Only used where the HUD itself is large enough for the floor to be
    /// reachable (see <see cref="SecondaryScale"/>).</summary>
    public static bool MeetsMinimumTextHeight(float lineSpacing, float drawScale)
        => TextHeight(lineSpacing, drawScale) >= MinTextHeightPx;

    /// <summary>Clear-space padding kept on both sides of the gauge percentage inside
    /// its ring, in px at scale 1.</summary>
    public const float PercentPadding = 1f;

    /// <summary>
    /// The draw scale of the gauge PERCENTAGE inside its ring. P1107 review fix: the
    /// percentage is drawn WITHOUT the dark text box in the compact ring (that box,
    /// 3 px wider than the text on each side, is what cut the stroke), and the fit is
    /// the glyph INK box against the ring's inner edge measured on the diagonal: the
    /// ring is a circle, so a box that clears the stroke at its centre line can still
    /// poke into the arc at its corners. Inner edge = radius − glow/2 − padding. A run
    /// whose ink corners already clear keeps today's size; a wider one ("100%")
    /// shrinks exactly to fit, never below 0.6 of today's size.
    /// </summary>
    /// <param name="inkWidth">Ink width at draw scale 1.</param>
    /// <param name="inkHeight">Ink height at draw scale 1.</param>
    public static float PercentScale(float scale, float inkWidth, float inkHeight, float radius, float glow, bool compact)
    {
        if (!compact || inkWidth <= 0f) return scale;
        float inner = radius - glow / 2f - PercentPadding;
        if (inner <= 0f) return .6f * scale;
        float diagonal = MathF.Sqrt(inkWidth * inkWidth + inkHeight * inkHeight);
        float fit = 2f * inner / diagonal;
        return MathF.Max(MathF.Min(scale, fit), .6f * scale);
    }

    /// <summary>Do the ink box's corners at <paramref name="drawScale"/>, centred on the
    /// ring centre, stay inside the ring's inner edge?</summary>
    public static bool InkFitsRing(float inkWidth, float inkHeight, float drawScale, float radius, float glow)
    {
        float inner = radius - glow / 2f - PercentPadding;
        float hw = inkWidth * drawScale / 2f, hh = inkHeight * drawScale / 2f;
        return hw * hw + hh * hh <= inner * inner + .01f;
    }

    /// <summary>P1107 review fix — half-size of the box kept clear around the screen
    /// centre (crosshair) in the compact HUD's text layout, in px.</summary>
    public static float CrosshairClearHalf(float scale) => 24f * scale;

    /// <summary>The bar fill for a target hull/shield reading: native's 0 % for a
    /// non-damageable (<c>null</c>: alephs, out-of-sector statics) or empty target,
    /// clamped to 0..1.</summary>
    public static float BarFill(float? fraction)
        => fraction is { } value ? Math.Clamp(value, 0f, 1f) : 0f;

    /// <summary>
    /// One slim target-pane bar and its ARMOR/SHIELD label, in screen space, for the
    /// native pane origin (<paramref name="x0"/>, <paramref name="y0"/>) and pane scale
    /// <paramref name="s"/>. <paramref name="labelWidth"/>/<paramref name="labelHeight"/>
    /// are the measured size of the label at its own draw scale. The two bars flank the
    /// native target model preview (local x 270..366) on the row the ring gauges used,
    /// so `ARMOR ====` reads left-to-right on the left and `==== SHIELD` on the right —
    /// a different SHAPE from every own-ship gauge.
    /// </summary>
    public static (Rectangle Bar, Vector2 LabelTopLeft) TargetBar(
        bool shield, float x0, float y0, float s, float labelWidth, float labelHeight)
    {
        float barWidth = BarWidth * s, barHeight = BarHeight * s, gap = BarLabelGap * s;
        float centreY = y0 - BarCentreY * s;
        float top = centreY - barHeight / 2f;
        float labelTop = centreY - labelHeight / 2f;
        if (shield)
        {
            float labelLeft = x0 + RightGroupLeft * s;
            float barLeft = labelLeft + labelWidth + gap;
            return (new Rectangle((int)MathF.Round(barLeft), (int)MathF.Round(top),
                Math.Max(1, (int)MathF.Round(barWidth)), Math.Max(1, (int)MathF.Round(barHeight))),
                new Vector2(labelLeft, labelTop));
        }
        float barRight = x0 + LeftGroupRight * s;
        float left = barRight - barWidth;
        return (new Rectangle((int)MathF.Round(left), (int)MathF.Round(top),
                Math.Max(1, (int)MathF.Round(barWidth)), Math.Max(1, (int)MathF.Round(barHeight))),
            new Vector2(barRight - barWidth - gap - labelWidth, labelTop));
    }
}
