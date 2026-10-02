using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P281 — draws the post-connect SIDE-SELECT screen: a translucent backdrop
/// with a centred panel titled "SELECT SIDE" listing each joinable side from
/// the SIDE_ROSTER(211) snapshot (name + live player count), highlighting the
/// current selection, plus a navigate/confirm hint and (for the mission owner)
/// a randomize-teams hint. Laid out by <see cref="SideSelectLayout"/>; follows
/// the same conventions as <see cref="DockedScreenRenderer"/> /
/// <see cref="ScoreboardOverlayRenderer"/>: its own SpriteBatch session, a
/// translucent panel with a coloured border, every drawn string pure ASCII.
/// </summary>
public sealed class SideSelectOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color Backdrop = new(0.006f, 0.009f, 0.013f, 0.968f);
    private static readonly Color PanelBg = new(0.022f, 0.034f, 0.050f, 0.952f);
    private static readonly Color PanelBorder = new(0.72f, 0.80f, 0.50f);
    private static readonly Color TitleColor = new(0.94f, 0.96f, 0.86f);
    private static readonly Color ItemColor = new(0.88f, 0.92f, 0.88f);
    private static readonly Color HighlightColor = new(0.60f, 1.00f, 0.66f);
    private static readonly Color MutedColor = new(0.50f, 0.55f, 0.55f);
    private static readonly Color HintColor = new(0.60f, 0.68f, 0.64f);

    private const float Padding = 24f;
    private const float LineGap = 3f;

    public SideSelectOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the side-select panel for the given
    /// <see cref="SideSelectLayout"/> rows.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<SideSelectLayout.Row> rows)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Height/width from the actual rows; the title line uses the large font.
        float maxLine = 0f, totalH = 0f;
        foreach (var r in rows)
        {
            if (r.Kind == SideSelectLayout.Kind.Blank) { totalH += LineGap; continue; }
            bool isTitle = r.Kind == SideSelectLayout.Kind.Title;
            var f = isTitle ? _big : _small;
            totalH += f.LineSpacing + LineGap;
            maxLine = MathF.Max(maxLine, f.MeasureString(r.Text).X);
        }
        if (totalH <= 0f) totalH = 1f;

        float panelW = MathF.Min(maxLine + 2f * Padding, w * 0.96f);
        float panelH = totalH + 2f * Padding - LineGap;
        float panelX = MathF.Max(12f, (w - panelW) / 2f);
        float panelY = MathF.Max(12f, (h - panelH) / 2f);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(0, 0, w, h, Backdrop);
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 3f, PanelBorder);   // top accent edge

            float y = panelY + Padding;
            foreach (var r in rows)
            {
                if (r.Kind == SideSelectLayout.Kind.Blank) { y += LineGap; continue; }
                bool isTitle = r.Kind == SideSelectLayout.Kind.Title;
                var f = isTitle ? _big : _small;
                var c = ColorFor(r);
                _sb.DrawString(f, r.Text, new Vector2(panelX + Padding, y), c, 0f,
                    Vector2.Zero, 1f, SpriteEffects.None, 0f);
                y += f.LineSpacing + LineGap;
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private static Color ColorFor(SideSelectLayout.Row r) => r.Kind switch
    {
        SideSelectLayout.Kind.Title => TitleColor,
        SideSelectLayout.Kind.Hint => HintColor,
        SideSelectLayout.Kind.Item => r.Highlight ? HighlightColor : ItemColor,
        SideSelectLayout.Kind.Muted => MutedColor,
        _ => ItemColor,
    };

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x),
            (int)MathF.Round(y), (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
