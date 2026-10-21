using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — draws the LEADERBOARD screen (the upgraded in-mission scoreboard): a
/// translucent panel listing every real-side player's in-side position, name,
/// side, rank name, overall rating, combat rating, kills, deaths, base kills
/// and base captures — from the derived roster (P774: PLAYERINFO + KILL_SHIP)
/// in <c>ClientWorld.ScoreRoster</c> + the <c>RANK_INFO(13)</c> rank
/// table (<c>ClientWorld.RankTable</c>). See <see cref="ScoreboardLayout"/> for
/// the ordering (side asc, score desc), the in-side position numbering and the
/// native (leaderboardscreen.cpp:54-67) column mapping; the career-ladder
/// columns native drew from its stats DB are an era/infra constraint (see the
/// layout docs) and are marked in the report, not faked.
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
    /// <paramref name="selfName"/> (when provided) highlights the local player's row.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<ScoreboardLayout.Row> rows,
        string? selfName = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        var (cwPos, cwName, cwSide, cwRank, cwScore, cwCombat, cwKills, cwDeaths, cwBk, cwBc) =
            ScoreboardLayout.ColumnWidths(rows, s => _small.MeasureString(s).X);

        // Panel width = header/row extent + side margins; height fits header +
        // rows (cap so many players never overflow past the screen).
        float[] cw = { cwPos, cwName, cwSide, cwRank, cwScore, cwCombat, cwKills, cwDeaths, cwBk, cwBc };
        float contentW = 0f;
        for (int c = 0; c < cw.Length; c++)
        {
            contentW += cw[c];
            if (c < cw.Length - 1) contentW += ScoreboardLayout.ColumnGap;
        }
        float panelW = contentW + 2f * ScoreboardLayout.Padding;
        float panelH = ScoreboardLayout.RowHeight /* header */
                     + ScoreboardLayout.HeaderGap
                     + rows.Count * ScoreboardLayout.RowHeight
                     + 2f * ScoreboardLayout.Padding;
        panelH = MathF.Min(panelH, h * 0.92f);

        // Centred horizontally so the wider leaderboard stays on screen; slightly
        // inset from the top so it never covers the centre reticle / bottom bars.
        float panelX = MathF.Max(12f, (w - panelW) / 2f);
        float panelY = 40f;

        // Running column origins, aligned with the header array above.
        float[] xs = new float[cw.Length];
        xs[0] = panelX + ScoreboardLayout.Padding;
        for (int c = 1; c < cw.Length; c++) xs[c] = xs[c - 1] + cw[c - 1] + ScoreboardLayout.ColumnGap;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            // Header.
            Draw("LEADERBOARD  (B TOGGLE)", new Vector2(xs[0], panelY + ScoreboardLayout.Padding - 14f), HeaderColor);
            float headY = panelY + ScoreboardLayout.Padding + ScoreboardLayout.HeaderGap - 4f;
            string[] headers =
            {
                ScoreboardLayout.Row.HeaderPosition, ScoreboardLayout.Row.HeaderName,
                ScoreboardLayout.Row.HeaderSide, ScoreboardLayout.Row.HeaderRank,
                ScoreboardLayout.Row.HeaderScore, ScoreboardLayout.Row.HeaderCombat,
                ScoreboardLayout.Row.HeaderKills, ScoreboardLayout.Row.HeaderDeaths,
                ScoreboardLayout.Row.HeaderBaseKills, ScoreboardLayout.Row.HeaderBaseCaptures,
            };
            for (int c = 0; c < headers.Length; c++)
                Draw(headers[c], new Vector2(xs[c], headY), HeaderColor);

            // Rows.
            float rowY = headY + ScoreboardLayout.RowHeight;
            foreach (var r in rows)
            {
                bool self = selfName != null && string.Equals(r.Name, selfName, StringComparison.Ordinal);
                Color nc = self ? SelfColor : NameColor;
                string[] cells =
                {
                    r.Position, r.Name, r.SideLabel, r.RankName, r.Score, r.CombatRating,
                    r.Kills, r.Deaths, r.BaseKills, r.BaseCaptures,
                };
                for (int c = 0; c < cells.Length; c++)
                {
                    Color cc = c switch
                    {
                        1 => nc,      // name — self highlighted
                        3 => RankColor, // rank name
                        _ => StatColor,
                    };
                    Draw(cells[c], new Vector2(xs[c], rowY), cc);
                }
                rowY += ScoreboardLayout.RowHeight;
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
