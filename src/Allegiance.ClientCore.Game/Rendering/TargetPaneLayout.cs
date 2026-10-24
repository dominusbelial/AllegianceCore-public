using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-3 — pure geometry for the native bottom target pane. The pane is
/// anchored at <c>SideBottom (-320, 25)</c> with a lower-left origin and +Y up
/// (<c>Artwork/dialog.mdl:5402</c>); child positions are pane-local
/// (<c>dialog.mdl:4217-4242</c>). This class owns the local extents and the
/// viewport scale, including the width clamp that keeps the pane clear of the
/// bottom-right minimap on narrow viewports.
/// </summary>
public static class TargetPaneLayout
{
    // Pane-local extents from the DefaultNormalTargetDisplay children.
    public const float LocalLeft = 92f;      // TargetSpeed left edge
    public const float LocalRight = 550f;    // TargetRange right edge
    public const float LocalCenterX = 320f;  // pane centre (TargetName centre)
    public const float LocalGaugeBottom = 33f;
    public const float LocalNameBottom = 125f;
    public const float LocalTop = 143f;      // name top (125 + font height)
    public const float LocalAnchorX = 320f;  // SideBottom -320
    public const float LocalAnchorY = 25f;   // SideBottom +25

    // HUD-20 — native TargetExperienceImage inside DefaultNormalTargetDisplay:
    // group at Point(280,110) holding a JustifyCenter 80-wide StringImage and the
    // targetexperiencebmp translated Point(25,-21) (dialog.mdl:4192-4206,:4232).
    // A native StringImage hangs BELOW its origin: bounds Rect(0,-ysize,w,0) and
    // each line drawn at y -= size.Y (engine/image.cpp:336-388), so in the y-up
    // pane space local 110 is the TOP of the number (glyphs span ~97..110,
    // inside the 32x32 medal at 89..121).
    public const float ExperienceLeft = 280f;
    public const float ExperienceTextTop = 110f;
    public const float ExperienceBoxWidth = 80f;
    public const float ExperienceBitmapX = 25f;   // TranslateImage Point(25,…)
    public const float ExperienceBitmapY = 21f;   // TranslateImage Point(…,-21)

    /// <summary>HUD-20 — screen lower-left of the targetexperiencebmp: pane-local
    /// (280+25, 110−21) mapped by the pane rule <c>x0 + x·s</c>, <c>y0 − y·s</c>.</summary>
    public static (float Left, float Bottom) ExperienceArtBottomLeft(float x0, float y0, float s) =>
        (x0 + (ExperienceLeft + ExperienceBitmapX) * s, y0 - (ExperienceTextTop - ExperienceBitmapY) * s);

    /// <summary>HUD-20 — screen top-left of the kill-bonus number of measured
    /// width <paramref name="numberWidth"/>: JustifyCenter in the 80-wide box at
    /// pane-local x 280, with the StringImage origin (local y 110) as the text
    /// TOP (<c>engine/image.cpp:336-388</c>).</summary>
    public static (float Left, float Top) ExperienceNumberTopLeft(float x0, float y0, float s, float numberWidth) =>
        (x0 + (ExperienceLeft + ExperienceBoxWidth / 2f) * s - numberWidth / 2f, y0 - ExperienceTextTop * s);

    /// <summary>The native coordinate scale for a viewport, width-clamped so the
    /// pane's right edge never reaches the bottom-right minimap (the HUD-2
    /// overlap rule). Matches the gauge spread at large heights.</summary>
    public static float Scale(float viewportW, float viewportH)
    {
        float desired = Math.Clamp(viewportH / 680f, 0.9f, 2.1f);
        var (mx, _, _, _) = MinimapGeometry.CornerPanel(viewportW, viewportH);
        float centreX = viewportW / 2f;
        float availableRight = mx - MinimapGeometry.Margin - centreX;
        float availableLeft = centreX;
        float maxByRight = availableRight / (LocalRight - LocalCenterX);
        float maxByLeft = availableLeft / (LocalCenterX - LocalLeft);
        return Math.Clamp(Math.Min(desired, Math.Min(maxByRight, maxByLeft)), 0.9f, 2.1f);
    }

    /// <summary>Screen X (right, +X) of the pane's local origin.</summary>
    public static float OriginX(float viewportW, float viewportH) =>
        viewportW / 2f - LocalAnchorX * Scale(viewportW, viewportH);

    /// <summary>Screen Y (down, +Y) of the pane's local y=0.</summary>
    public static float OriginY(float viewportW, float viewportH) =>
        viewportH - LocalAnchorY * Scale(viewportW, viewportH);

    /// <summary>Screen Y (down) of the pane's top edge (name top).</summary>
    public static float TopY(float viewportW, float viewportH) =>
        OriginY(viewportW, viewportH) - LocalTop * Scale(viewportW, viewportH);

    /// <summary>The on-screen rectangle the pane occupies (for text-layout
    /// reservation), excluding the below-anchor oval decoration.</summary>
    public static Rectangle ReserveRect(float viewportW, float viewportH)
    {
        float s = Scale(viewportW, viewportH);
        int x = (int)MathF.Round(OriginX(viewportW, viewportH) + LocalLeft * s);
        int y = (int)MathF.Round(TopY(viewportW, viewportH));
        int width = (int)MathF.Round((LocalRight - LocalLeft) * s);
        int height = Math.Max(1, (int)MathF.Round(OriginY(viewportW, viewportH) - TopY(viewportW, viewportH)));
        return new Rectangle(x, y, width, height);
    }

    /// <summary>P805 (row 471) — the target pane's slide offset, screen px down.
    /// Native slides <c>TargetDisplayImage</c> up from fully below the screen —
    /// <c>SideBottom(-320,-183) → (-320,25)</c>, 0.5 s (dialog.mdl:5404) — a
    /// 208 native px travel; while the slide is in flight the pane sits lower
    /// by the remaining travel, scaled with the pane (y-down positive).</summary>
    public static float SlideOffset(float slideProgress, float scale)
        => (1f - Math.Clamp(slideProgress, 0f, 1f)) * 208f * scale;
}
