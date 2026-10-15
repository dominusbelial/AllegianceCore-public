using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — draws the TEAM PANE: the full-roster screen (every side's pilots with
/// rank name, ship status, kills, deaths, plus money / active-ballot footer).
/// Laid out by <see cref="TeamPaneLayout"/>; follows the same conventions as
/// <see cref="DockedScreenRenderer"/> / <see cref="ScoreboardOverlayRenderer"/>:
/// its own <see cref="SpriteBatch"/> session, a translucent panel with a
/// coloured border, every drawn string pure ASCII.
/// </summary>
public sealed class TeamPaneRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color PanelBg = new(0.012f, 0.024f, 0.032f, 0.94f);
    private static readonly Color PanelBorder = new(0.55f, 0.78f, 0.60f);
    private static readonly Color TitleColor = new(0.95f, 0.96f, 0.80f);
    private static readonly Color SideHeaderColor = new(0.75f, 0.85f, 0.60f);
    private static readonly Color PilotColor = new(0.88f, 0.92f, 0.88f);
    private static readonly Color HighlightColor = new(0.62f, 1.00f, 0.66f);
    private static readonly Color MoneyColor = new(1.00f, 0.90f, 0.50f);
    private static readonly Color BallotColor = new(0.60f, 0.80f, 1.00f);
    private static readonly Color NoteColor = new(0.52f, 0.58f, 0.58f);
    private static readonly Color HintColor = new(0.60f, 0.68f, 0.64f);

    private const float Padding = 22f;
    private const float LineGap = 2f;

    public TeamPaneRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the team-pane roster for the given <see cref="TeamPaneLayout"/> lines.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<TeamPaneLayout.Line> lines, bool compact = false)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Size from the actual lines; the title uses the large font.
        float maxLine = 0f, totalH = 0f;
        foreach (var ln in lines)
        {
            if (ln.Kind == TeamPaneLayout.LineKind.Blank) { totalH += LineGap + 6f; continue; }
            bool isTitle = ln.Kind == TeamPaneLayout.LineKind.Title;
            var f = isTitle ? _big : _small;
            totalH += f.LineSpacing + LineGap;
            maxLine = MathF.Max(maxLine, f.MeasureString(ln.Text).X);
        }
        if (totalH <= 0f) totalH = 1f;

        float panelW = MathF.Min(maxLine + 2f * Padding, w * 0.985f);
        float panelH = MathF.Min(totalH + 2f * Padding - LineGap, h * 0.94f);
        float panelX = MathF.Max(8f, compact ? w - panelW - 8 : (w - panelW) / 2f);
        float panelY = MathF.Max(8f, compact ? 80 : (h - panelH) / 2f);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 3f, PanelBorder);   // top accent edge

            float y = panelY + Padding;
            foreach (var ln in lines)
            {
                if (ln.Kind == TeamPaneLayout.LineKind.Blank) { y += LineGap + 6f; continue; }
                bool isTitle = ln.Kind == TeamPaneLayout.LineKind.Title;
                var f = isTitle ? _big : _small;
                var c = ColorFor(ln);
                _sb.DrawString(f, ln.Text, new Vector2(panelX + Padding, y), c, 0f,
                    Vector2.Zero, 1f, SpriteEffects.None, 0f);
                y += f.LineSpacing + LineGap;
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private static Color ColorFor(TeamPaneLayout.Line l) => l.Kind switch
    {
        TeamPaneLayout.LineKind.Title => TitleColor,
        TeamPaneLayout.LineKind.SideHeader => SideHeaderColor,
        TeamPaneLayout.LineKind.Pilot => l.Highlight ? HighlightColor : PilotColor,
        TeamPaneLayout.LineKind.Money => MoneyColor,
        TeamPaneLayout.LineKind.Ballot => BallotColor,
        TeamPaneLayout.LineKind.Note => NoteColor,
        TeamPaneLayout.LineKind.Hint => HintColor,
        _ => PilotColor,
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
