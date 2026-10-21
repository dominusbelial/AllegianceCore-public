namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>HUD-17 — the native radar label block's left column geometry
/// (<c>WinTrek/radarimage.cpp:1257-1266,1281-1282,1333-1352</c>): the command
/// icon and the friendly detection eye share one column to the left of the
/// name/range text. The column is as wide as the WIDEST of the two surfaces
/// (native: <c>if (w &gt; xshift) { width -= xshift; xshift = w; width += xshift; }</c>,
/// :1257-1266), the eye is drawn BELOW the command icon when one is present
/// (:1333-1352), and any left-side surface adds one label line (:1281-1282).
/// Pure (no graphics types) so the layout is unit-testable headless.</summary>
public readonly record struct RadarLabelLeftColumn(float Width, float Height, float EyeTop, bool AddsLine);

public static class RadarLabelLayout
{
    /// <summary>Resolve the left column for this frame. <paramref name="commandSize"/>
    /// / <paramref name="eyeSize"/> are the scaled surface sizes (both native
    /// 16×16), <paramref name="gap"/> the port's small vertical gap between the
    /// stacked icons. When only the eye draws it takes the first slot
    /// (<c>EyeTop == 0</c>); with a command icon above it the eye starts one
    /// command size + gap down.</summary>
    public static RadarLabelLeftColumn LeftColumn(
        bool hasCommandIcon, float commandSize, bool hasEye, float eyeSize, float gap)
    {
        float width = System.MathF.Max(hasCommandIcon ? commandSize : 0f, hasEye ? eyeSize : 0f);
        float eyeTop = hasEye && hasCommandIcon ? commandSize + gap : 0f;
        float height = hasEye && hasCommandIcon ? commandSize + gap + eyeSize
            : hasEye ? eyeSize
            : hasCommandIcon ? commandSize
            : 0f;
        return new(width, height, eyeTop, hasCommandIcon || hasEye);
    }
}
