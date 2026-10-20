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
}
