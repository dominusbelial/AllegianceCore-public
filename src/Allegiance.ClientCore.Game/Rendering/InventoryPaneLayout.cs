using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-7 — pure geometry for the native flight equipment/inventory pane. The
/// pane is anchored at <c>SideBottomRight (-36, 40)</c> with a lower-left origin
/// and +Y up (<c>Artwork/dialog.mdl:5387</c>); its line background is 214 px
/// wide with 18 px rows and 14 px end caps (<c>dialog.mdl:1812-1821</c>,
/// <c>invsweapon.mdl</c> row columns). The only divergence from the native
/// anchor is a clamp that raises the pane clear of the port's bottom-right
/// minimap (the HUD-2 overlap rule), exactly like HUD-3's target-pane clamp.
/// </summary>
public static class InventoryPaneLayout
{
    public const float LineWidth = 214f;
    public const float RowHeight = 18f;
    public const float CapHeight = 14f;   // top/bottom end caps
    public const float AnchorX = 36f;     // SideBottomRight -36
    public const float AnchorY = 40f;     // SideBottomRight +40

    // Row-local column anchors (invdefault.mdl / invsweapon.mdl).
    public const float IconX = 5f;
    public const float KeyX = 23f;
    public const float NameX = 35f;
    public const float AmmoX = 110f;
    public const float StatsX = 130f;

    public static float Scale(float viewportH) => Math.Clamp(viewportH / 680f, 0.9f, 2.1f);

    public static float Height(int rowCount, float viewportH) =>
        (CapHeight * 2f + RowHeight * rowCount) * Scale(viewportH);

    public static float RightX(float viewportW, float viewportH) =>
        viewportW - AnchorX * Scale(viewportH);

    public static float NativeBottomY(float viewportH) =>
        viewportH - AnchorY * Scale(viewportH);

    /// <summary>Screen Y (down) of the pane's bottom edge, clamped so the pane
    /// never overlaps the bottom-right minimap (HUD-2 overlap rule).</summary>
    public static float BottomY(float viewportW, float viewportH)
    {
        float native = NativeBottomY(viewportH);
        var (mx, my, _, _) = MinimapGeometry.CornerPanel(viewportW, viewportH);
        float clearY = my - MinimapGeometry.Margin;
        return Math.Min(native, clearY);
    }

    public static float LeftX(float viewportW, float viewportH) =>
        RightX(viewportW, viewportH) - LineWidth * Scale(viewportH);

    public static float TopY(int rowCount, float viewportW, float viewportH) =>
        BottomY(viewportW, viewportH) - Height(rowCount, viewportH);

    /// <summary>The on-screen rectangle the pane occupies, for text-layout
    /// reservation.</summary>
    public static Rectangle ReserveRect(int rowCount, float viewportW, float viewportH)
    {
        float s = Scale(viewportH);
        int x = (int)MathF.Round(LeftX(viewportW, viewportH));
        int y = (int)MathF.Round(TopY(rowCount, viewportW, viewportH));
        int w = Math.Max(1, (int)MathF.Round(LineWidth * s));
        int h = Math.Max(1, (int)MathF.Round(Height(rowCount, viewportH)));
        return new Rectangle(x, y, w, h);
    }
}
