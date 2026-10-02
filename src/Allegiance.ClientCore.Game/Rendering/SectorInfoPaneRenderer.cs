using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P272 — draws the starmap's per-cluster info pane, the port's rendering half
/// of native's <c>SectorInfoPane</c> (<c>WinTrek/sectormap.cpp:139-538</c>).
/// The CONTENT it paints is computed purely by <see cref="SectorInfoPaneLayout"/>
/// (asteroid counts per capability group, helium ore) from the client's own
/// decoded <c>DataAsteroidIGC</c> data; this class only lays the text out and
/// paints it, anchored to the top-left of the starmap panel below the title.
///
/// Like <see cref="StarmapRenderer"/>, it begins/ends its own
/// <see cref="SpriteBatch"/> session so it can be invoked anywhere in the draw
/// order (over the 3D scene or into an offscreen capture target).
/// </summary>
public sealed class SectorInfoPaneRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;

    private static readonly Color PaneBg = new(0.016f, 0.045f, 0.048f, 0.90f);
    private static readonly Color PaneBorder = new(0.20f, 0.52f, 0.48f);
    private static readonly Color HeadingColor = new(0.95f, 0.97f, 0.94f);
    private static readonly Color BodyColor = new(0.72f, 0.85f, 0.82f);
    private static readonly Color AccentColor = new(0.35f, 0.85f, 0.75f);

    /// <summary>Horizontal padding inside the pane box.</summary>
    private const float PadX = 12f;
    /// <summary>Vertical padding inside the pane box.</summary>
    private const float PadY = 8f;
    /// <summary>The map panel border the pane aligns to (mirrors StarmapRenderer's).</summary>
    private const float Border = 26f;

    public SectorInfoPaneRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont small)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
    }

    /// <summary>
    /// Draw the info pane for <paramref name="pane"/> inside the given map
    /// panel rectangle (top-left anchored just below the panel title, inside
    /// the same <see cref="Border"/> inset the map uses). Uses
    /// <see cref="SectorInfoPaneLayout.LineOrigins"/> for the line stacking so
    /// the 2-D layout stays in the pure logic class.
    /// </summary>
    public void Draw(SectorInfoPaneLayout.Data pane, Viewport viewport,
        float panelX, float panelY, float panelW, float panelH, Color? ownSideColor = null)
    {
        if (pane.DisplayLines.Count == 0 && !pane.ShowsOwnProbes)
            return;

        // Measure the widest line for the box width, and one line's height.
        float lineHeight = _small.LineSpacing;
        float maxW = 0f;
        foreach (var line in pane.DisplayLines)
            maxW = MathF.Max(maxW, _small.MeasureString(line).X);
        // The probe glyph row's text contributes to the width too.
        string probeLabel = $"PROBES  {pane.OwnProbeCount}";
        if (pane.ShowsOwnProbes)
            maxW = MathF.Max(maxW, ProbeGlyphWidth + 6f + _small.MeasureString(probeLabel).X);

        float boxW = maxW + 2f * PadX;
        int lineCount = pane.DisplayLines.Count + (pane.ShowsOwnProbes ? 1 : 0);
        float boxH = lineCount * lineHeight + 2f * PadY;

        // Anchor below the panel title (panelY + titleBaseline + a gap).
        float topLeftX = panelX + Border;
        float topLeftY = panelY + 10f + lineHeight + 10f;

        // Clamp inside the panel so a tiny window can't push it off-screen.
        if (topLeftX + boxW > panelX + panelW - Border)
            topLeftX = panelX + panelW - Border - boxW;
        if (topLeftY + boxH > panelY + panelH - Border)
            topLeftY = panelY + panelH - Border - boxH;

        var origins = SectorInfoPaneLayout.LineOrigins(
            new System.Numerics.Vector2(topLeftX + PadX, topLeftY + PadY), lineHeight, lineCount);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // Translucent backing + border.
            FillRect(topLeftX, topLeftY, boxW, boxH, PaneBg);
            OutlineRect(topLeftX, topLeftY, boxW, boxH, PaneBorder);
            FillRect(topLeftX, topLeftY, boxW, 2f, PaneBorder);

            for (int i = 0; i < pane.DisplayLines.Count; i++)
            {
                Color c = i == 0 ? HeadingColor : (i == pane.DisplayLines.Count - 1 ? AccentColor : BodyColor);
                _sb.DrawString(_small, pane.DisplayLines[i],
                    new Vector2(origins[i].X, origins[i].Y), c);
            }

            // P460 — the own-probe row: a small filled diamond probe-glyph in the
            // own side's colour followed by the count (native draws m_pprobeIcon
            // in the own side colour + the count, sectormap.cpp:338-344). Falls
            // back to the accent colour if no side colour is resolvable.
            if (pane.ShowsOwnProbes)
            {
                int pr = origins.Count - 1;
                Color pc = ownSideColor ?? AccentColor;
                DrawProbeGlyph(origins[pr].X + ProbeGlyphWidth / 2f, origins[pr].Y + lineHeight / 2f, pc);
                _sb.DrawString(_small, probeLabel,
                    new Vector2(origins[pr].X + ProbeGlyphWidth + 6f, origins[pr].Y), AccentColor);
            }
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>The horizontal span reserved for the probe glyph row's icon.</summary>
    private const float ProbeGlyphWidth = 12f;

    /// <summary>Draw a small filled diamond standing in for native's probe icon
    /// (<c>m_pprobeIcon</c>) at the row's baseline centre.</summary>
    private void DrawProbeGlyph(float cx, float cy, Color color)
    {
        float r = 4.5f;
        // Horizontal scanline fill of a diamond of radius r around (cx, cy).
        for (float dy = -r; dy <= r; dy += 1f)
        {
            float halfW = r - MathF.Abs(dy);
            FillRect(cx - halfW, cy + dy, 2f * halfW + 1f, 1f, color);
        }
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
