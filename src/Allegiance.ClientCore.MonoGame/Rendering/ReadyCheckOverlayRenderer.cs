using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P282 — draws the READY-CHECK screen: a translucent backdrop with a centred
/// panel titled "READY CHECK" listing the local player's ready toggle, the
/// live same-side roster of ready states (from real PLAYER_READY relays), the
/// per-side aggregate <c>TEAM_READY(46)</c> line, and (for the mission owner)
/// a force-ready hint. Laid out by <see cref="ReadyCheckLayout"/>; follows the
/// same conventions as <see cref="SideSelectOverlayRenderer"/> / 
/// <see cref="ScoreboardOverlayRenderer"/>: its own SpriteBatch session, a
/// translucent panel with a coloured border, every drawn string pure ASCII.
/// </summary>
public sealed class ReadyCheckOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color Backdrop = new(0.005f, 0.008f, 0.014f, 0.968f);
    private static readonly Color PanelBg = new(0.020f, 0.032f, 0.052f, 0.952f);
    private static readonly Color PanelBorder = new(0.55f, 0.75f, 0.90f);
    private static readonly Color TitleColor = new(0.90f, 0.95f, 0.98f);
    private static readonly Color ItemColor = new(0.86f, 0.90f, 0.92f);
    private static readonly Color HighlightColor = new(0.55f, 1.00f, 0.75f);
    private static readonly Color MutedColor = new(0.50f, 0.56f, 0.58f);
    private static readonly Color HintColor = new(0.60f, 0.72f, 0.70f);
    private static readonly Color InfoColor = new(0.92f, 0.85f, 0.55f);

    private const float Padding = 24f;
    private const float LineGap = 3f;

    public ReadyCheckOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the ready-check panel for the given
    /// <see cref="ReadyCheckLayout"/> rows.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<ReadyCheckLayout.Row> rows)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        float maxLine = 0f, totalH = 0f;
        foreach (var r in rows)
        {
            if (r.Kind == ReadyCheckLayout.Kind.Blank) { totalH += LineGap; continue; }
            bool isTitle = r.Kind == ReadyCheckLayout.Kind.Title;
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
                if (r.Kind == ReadyCheckLayout.Kind.Blank) { y += LineGap; continue; }
                bool isTitle = r.Kind == ReadyCheckLayout.Kind.Title;
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

    private static Color ColorFor(ReadyCheckLayout.Row r) => r.Kind switch
    {
        ReadyCheckLayout.Kind.Title => TitleColor,
        ReadyCheckLayout.Kind.Hint => HintColor,
        ReadyCheckLayout.Kind.Info => InfoColor,
        ReadyCheckLayout.Kind.Item => r.Highlight ? HighlightColor : ItemColor,
        ReadyCheckLayout.Kind.Muted => MutedColor,
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
