using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P360 — pure, headless-testable placement math for the persistent compact
/// corner mini-map (native's SectorInfoPane "closed" view). Following the
/// established pure-logic discipline of this client (StarmapLayout,
/// SectorInfoPaneLayout are System.Numerics-only so they unit-test without a
/// graphics device), this class owns the only geometry the mini-map needs that
/// is worth pinning down: where the small bottom-right panel goes and how big
/// it gets. The renderer (<see cref="StarmapRenderer.DrawMini"/>) then fits
/// the same authored ScreenX/ScreenY layout into that rectangle via
/// <see cref="StarmapLayout.Fit"/> — no duplicated maths.
/// </summary>
public static class MinimapGeometry
{
    /// <summary>Screen-space margin from the bottom/right viewport edges.</summary>
    public const float Margin = 14f;
    /// <summary>Hard cap on the mini-map width (keep it a compact widget).</summary>
    public const float MaxWidth = 360f;
    /// <summary>Hard cap on the mini-map height.</summary>
    public const float MaxHeight = 240f;
    /// <summary>Width scales with the viewport up to <see cref="MaxWidth"/>.</summary>
    public const float WidthFraction = 0.26f;
    /// <summary>Height scales with the viewport up to <see cref="MaxHeight"/>.</summary>
    public const float HeightFraction = 0.30f;
    /// <summary>Absolute floor so a tiny window still shows a usable widget.</summary>
    private const float FloorWidth = 80f;
    /// <summary>Absolute floor so a tiny window still shows a usable widget.</summary>
    private const float FloorHeight = 60f;

    /// <summary>
    /// The panel rectangle (viewport px) for a bottom-right-corner mini-map on a
    /// viewport of <paramref name="viewportW"/>×<paramref name="viewportH"/>:
    /// width/height scale with the viewport but are clamped to the
    /// <see cref="MaxWidth"/>/<see cref="MaxHeight"/> caps (and to the viewport
    /// minus both margins so the panel can never run off-screen), floored so it
    /// stays usable, and anchored <see cref="Margin"/> from the bottom/right
    /// edges. The <see cref="Vector2"/> return lets callers treat it as a
    /// rectangle the same way <see cref="StarmapLayout"/> deals in points.
    /// <paramref name="coreScale"/> is the P833 Modern-HUD density scale: when
    /// supplied the widget's size caps grow with the Modern HUD scale so the
    /// mini-map is no longer dwarfed by the scaled text around it; omitted
    /// (Classic and the shared global minimap) keeps the fixed caps.
    /// </summary>
    public static (float X, float Y, float Width, float Height) CornerPanel(
        float viewportW, float viewportH, float? coreScale = null)
    {
        float k = coreScale ?? 1f;
        float w = MathF.Min(viewportW * WidthFraction * k, MaxWidth * k);
        float h = MathF.Min(viewportH * HeightFraction * k, MaxHeight * k);
        // Never overflow the viewport once the margin is honoured on both sides.
        w = MathF.Min(w, MathF.Max(FloorWidth, viewportW - 2f * Margin));
        h = MathF.Min(h, MathF.Max(FloorHeight, viewportH - 2f * Margin));
        w = MathF.Max(w, FloorWidth);
        h = MathF.Max(h, FloorHeight);
        float x = viewportW - Margin - w;
        float y = viewportH - Margin - h;
        return (x, y, w, h);
    }
}
