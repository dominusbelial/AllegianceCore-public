using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using Allegiance.ClientCore.MonoGame.Rendering.Skins;
namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — draws the LEADERBOARD screen (the upgraded in-mission scoreboard): a
/// translucent panel listing every real-side player's in-side position, name,
/// side, rank name and live kills/deaths — from the derived roster (P774:
/// PLAYERINFO + KILL_SHIP) in <c>ClientWorld.ScoreRoster</c> + the
/// <c>RANK_INFO(13)</c> rank table (<c>ClientWorld.RankTable</c>). See
/// <see cref="ScoreboardLayout"/> for the ordering (side asc, kills desc,
/// deaths asc, name), the in-side position numbering and the column mapping
/// (P774b: the in-mission columns are exactly RANK/name/K/D — score and base
/// kills/captures are game-over-only, GAME_OVER_PLAYERS,
/// gameoverscreen.cpp:146-158, and the game-over screen shows them;
/// career-ladder columns native drew from its stats DB are an era/infra
/// constraint (see the layout docs) and are marked in the report, not faked).
///
/// <para>Conventions mirror <see cref="HelpOverlayRenderer"/>: it begins/ends
/// its own <see cref="SpriteBatch"/> session, draws a translucent panel with a
/// coloured border, and keeps every drawn string pure ASCII via
/// <see cref="ScoreboardLayout.AsciiSafe"/>. Drawn top-most (after HUD) so it
/// is legible over the scene; an independent flag from the starmap/help flags
/// so the prior view resumes unchanged when it closes.</para>
/// </summary>
public sealed class ScoreboardOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color PanelBg = new(0.018f, 0.026f, 0.036f, 0.92f);
    private static readonly Color PanelBorder = new(0.90f, 0.62f, 0.22f);
    private static readonly Color HeaderColor = new(1.00f, 0.85f, 0.45f);
    private static readonly Color NameColor = new(0.95f, 0.97f, 0.98f);
    private static readonly Color StatColor = new(0.88f, 0.93f, 0.92f);
    private static readonly Color SelfColor = new(0.62f, 1.00f, 0.80f);
    private static readonly Color RankColor = new(0.75f, 0.80f, 0.95f);

    public ScoreboardOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the leaderboard for the given roster snapshot. <paramref name="rows"/>
    /// is the already-ordered/ASCII-safe output of <see cref="ScoreboardLayout.Build"/>;
    /// <paramref name="selfName"/> (when provided) highlights the local player's row.
    /// <para>P1042c — <paramref name="palette"/> (null = the P639 constants) lets the Jet
    /// screen re-ink the panel/cells through its five-role palette without moving a box.</para></summary>
    public void Draw(Viewport viewport, IReadOnlyList<ScoreboardLayout.Row> rows,
        string? selfName = null, ScoreboardPalette? palette = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // P1042c — one geometry source: the renderer paints from ScoreboardLayout.Layout,
        // the frame resolves the same panel/column/row boxes (draw == hit).
        var geometry = ScoreboardLayout.Layout(rows, s => _small.MeasureString(s).X, w, h);
        var panel = geometry.Panel;
        float[] xs = geometry.ColumnX;
        float panelX = panel.X, panelY = panel.Y, panelW = panel.Width, panelH = panel.Height;
        float headY = geometry.HeaderY;

        Color panelBg = palette?.Panel ?? PanelBg;
        Color panelBorder = palette?.Border ?? PanelBorder;
        Color headerColor = palette?.Title ?? HeaderColor;
        Color nameColor = palette?.Name ?? NameColor;
        Color statColor = palette?.Stat ?? StatColor;
        Color selfColor = palette?.Self ?? SelfColor;
        Color rankColor = palette?.Rank ?? RankColor;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, panelBg);
            OutlineRect(panelX, panelY, panelW, panelH, panelBorder);
            FillRect(panelX, panelY, panelW, 2f, panelBorder);   // top accent edge

            // Header.
            Draw("LEADERBOARD  (B TOGGLE)", new Vector2(xs[0], panelY + ScoreboardLayout.Padding - 14f), headerColor);
            string[] headers = ScoreboardLayout.Row.HeaderCells;
            for (int c = 0; c < headers.Length; c++)
                Draw(headers[c], new Vector2(xs[c], headY), headerColor);

            // Rows.
            float rowY = headY + geometry.RowHeight;
            foreach (var r in rows)
            {
                bool self = selfName != null && string.Equals(r.Name, selfName, StringComparison.Ordinal);
                Color nc = self ? selfColor : nameColor;
                string[] cells =
                {
                    r.Position, r.Name, r.SideLabel, r.RankName, r.Kills, r.Deaths,
                };
                for (int c = 0; c < cells.Length; c++)
                {
                    Color cc = c switch
                    {
                        1 => nc,      // name — self highlighted
                        3 => rankColor, // rank name
                        _ => statColor,
                    };
                    Draw(cells[c], new Vector2(xs[c], rowY), cc);
                }
                rowY += geometry.RowHeight;
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private void Draw(string text, Vector2 pos, Color color) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, 1f,
            SpriteEffects.None, 0f);

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
