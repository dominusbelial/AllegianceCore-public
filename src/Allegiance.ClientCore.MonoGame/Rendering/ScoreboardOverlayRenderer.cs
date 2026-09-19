using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P265 — draws the in-mission scoreboard overlay: a translucent panel listing
/// every real-side player's name, side, kills and deaths (from the periodic
/// owner-only <c>ScoreRosterUpdate(209)</c> snapshot in
/// <c>ClientWorld.ScoreRoster</c>). It is a small floating panel (unlike the
/// near-full-screen help overlay) because it is consulted while flying.
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

    private static readonly Color PanelBg = new(0.020f, 0.028f, 0.038f, 0.90f);
    private static readonly Color PanelBorder = new(0.90f, 0.62f, 0.22f);
    private static readonly Color HeaderColor = new(1.00f, 0.85f, 0.45f);
    private static readonly Color NameColor = new(0.95f, 0.97f, 0.98f);
    private static readonly Color StatColor = new(0.88f, 0.93f, 0.92f);
    private static readonly Color SelfColor = new(0.62f, 1.00f, 0.80f);

    public ScoreboardOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the scoreboard for the given roster snapshot. <paramref name="rows"/>
    /// is the already-ordered/ASCII-safe output of <see cref="ScoreboardLayout.Build"/>;
    /// <paramref name="selfName"/> (when provided) highlights the local player's row.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<ScoreboardLayout.Row> rows,
        string? selfName = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        var (cwName, cwSide, cwKills, cwDeaths) =
            ScoreboardLayout.ColumnWidths(rows, s => _small.MeasureString(s).X);

        // Panel width = header/row extent + side margins; height fits header +
        // rows (cap so many players never overflow past the screen).
        float contentW = cwName + ScoreboardLayout.ColumnGap + cwSide
                       + ScoreboardLayout.ColumnGap + cwKills
                       + ScoreboardLayout.ColumnGap + cwDeaths;
        float panelW = contentW + 2f * ScoreboardLayout.Padding;
        float panelH = ScoreboardLayout.RowHeight /* header */
                     + ScoreboardLayout.HeaderGap
                     + rows.Count * ScoreboardLayout.RowHeight
                     + 2f * ScoreboardLayout.Padding;
        panelH = MathF.Min(panelH, h * 0.92f);

        // Anchored upper-right so it never covers the centre reticle / bottom bars.
        float panelX = w - panelW - 24f;
        float panelY = 18f;

        float colX = panelX + ScoreboardLayout.Padding;
        float nameX = colX;
        float sideX = colX + cwName + ScoreboardLayout.ColumnGap;
        float killsX = sideX + cwSide + ScoreboardLayout.ColumnGap;
        float deathsX = killsX + cwKills + ScoreboardLayout.ColumnGap;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            // Header.
            Draw("SCOREBOARD  (B TOGGLE)", new Vector2(nameX, panelY + ScoreboardLayout.Padding - 14f), HeaderColor);
            float headY = panelY + ScoreboardLayout.Padding + ScoreboardLayout.HeaderGap - 4f;
            Draw(ScoreboardLayout.Row.HeaderName, new Vector2(nameX, headY), HeaderColor);
            Draw(ScoreboardLayout.Row.HeaderSide, new Vector2(sideX, headY), HeaderColor);
            Draw(ScoreboardLayout.Row.HeaderKills, new Vector2(killsX, headY), HeaderColor);
            Draw(ScoreboardLayout.Row.HeaderDeaths, new Vector2(deathsX, headY), HeaderColor);

            // Rows.
            float rowY = headY + ScoreboardLayout.RowHeight;
            foreach (var r in rows)
            {
                bool self = selfName != null && string.Equals(r.Name, selfName, StringComparison.Ordinal);
                Color nc = self ? SelfColor : NameColor;
                Draw(r.Name, new Vector2(nameX, rowY), nc);
                Draw(r.SideLabel, new Vector2(sideX, rowY), StatColor);
                Draw(r.Kills, new Vector2(killsX, rowY), StatColor);
                Draw(r.Deaths, new Vector2(deathsX, rowY), StatColor);
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
