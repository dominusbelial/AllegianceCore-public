using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — draws the CHARACTER SHEET (the upgraded Pilot Info screen): a
/// translucent panel showing the local player's <b>own</b> live session stats
/// (kills, deaths, ejections, rescues, base kills, base captures, artifacts,
/// flags, combat rating, score) and their resolved rank name, from the local
/// player's row of the periodic <c>ScoreRosterUpdate(209)</c> snapshot
/// (<c>ClientWorld.ScoreRoster</c>) + the <c>RANK_INFO(13)</c> rank table
/// (<c>ClientWorld.RankTable</c>). See <see cref="PilotInfoLayout"/> for the
/// field mapping and the native CharInfoScreen fidelity notes (career
/// medals/squads/civ are an era/infra constraint — this port has no Zone Club
/// backend).
///
/// <para>Conventions mirror <see cref="ScoreboardOverlayRenderer"/>: it begins/
/// ends its own <see cref="SpriteBatch"/> session, draws a translucent panel
/// with a coloured border, and keeps every drawn string pure ASCII (via
/// <see cref="PilotInfoLayout"/> / <see cref="ScoreboardLayout.AsciiSafe"/>).
/// It is a single-player panel anchored lower-left (under the centre-reticle /
/// upper scoreboard footprint) so it can coexist with the scoreboard on screen.
/// Drawn via an independent flag from the scoreboard/starmap/help flags so the
/// prior view resumes unchanged when it closes.</para>
/// </summary>
public sealed class PilotInfoOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color PanelBg = new(0.014f, 0.020f, 0.029f, 0.92f);
    private static readonly Color PanelBorder = new(0.42f, 0.72f, 0.95f);
    private static readonly Color HeaderColor = new(1.00f, 0.90f, 0.55f);
    private static readonly Color NameColor = new(0.95f, 0.97f, 0.98f);
    private static readonly Color RankColor = new(0.65f, 0.92f, 1.00f);
    private static readonly Color LabelColor = new(0.68f, 0.75f, 0.80f);
    private static readonly Color ValueColor = new(0.88f, 0.93f, 0.92f);

    private const float Padding = 16f;
    private const float RowHeight = 20f;
    private const float LabelGap = 24f;

    public PilotInfoOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the character sheet for the local player's extracted stats
    /// (<see cref="PilotInfoLayout.Build"/>). No-op when <paramref name="info"/>
    /// is null (local player not yet in the roster — nothing honest to show).</summary>
    public void Draw(Viewport viewport, PilotInfoLayout.PilotInfo? info)
    {
        if (info is not { } p) return;

        // Measure to size the panel: the wider of the title header, the name
        // header, the rank line and the widest (label + value) row.
        float titleW = _small.MeasureString($"CHARACTER SHEET  (P TOGGLE)").X;
        float nameW = _big.MeasureString($"{p.Name}  {p.SideLabel}").X;
        float rankW = _small.MeasureString(p.RankName).X;
        float rowW = 0f;
        foreach (var st in p.Stats)
        {
            float lbl = _small.MeasureString(st.Label).X;
            float val = _small.MeasureString(st.Value).X;
            rowW = MathF.Max(rowW, lbl + LabelGap + val);
        }
        float contentW = MathF.Max(titleW, MathF.Max(nameW, MathF.Max(rankW, rowW)));
        float panelW = contentW + 2f * Padding;

        // Header + name + rank + per-stat rows + vertical padding.
        float panelH = RowHeight                       // header
                     + 6f
                     + RowHeight + 6f                  // name (big)
                     + RowHeight                       // rank name
                     + 8f
                     + p.Stats.Count * RowHeight
                     + 2f * Padding;
        panelH = MathF.Min(panelH, viewport.Height * 0.92f);

        // Anchored lower-left so it never overlaps the upper scoreboard /
        // centre reticle / bottom bars.
        float panelX = 24f;
        float panelY = viewport.Height - panelH - 24f;

        float textX = panelX + Padding;
        float labelX = textX;
        float valueX = textX + contentW - Padding;   // values right-aligned in the panel

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            float y = panelY + Padding;
            Draw("CHARACTER SHEET  (P TOGGLE)", new Vector2(textX, y - 10f), HeaderColor);
            y += RowHeight + 6f;

            // The pilot's name + side, slightly larger (big font) as the panel title.
            _sb.DrawString(_big, $"{p.Name}  {p.SideLabel}", new Vector2(textX, y),
                NameColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            y += RowHeight + 6f;

            // The resolved rank name, prominent (native draws the rank banner).
            Draw($"RANK: {p.RankName}", new Vector2(textX, y), RankColor);
            y += RowHeight + 8f;

            foreach (var st in p.Stats)
            {
                if (st.Label == "RANK NAME") continue;   // already shown as the banner above
                Draw(st.Label, new Vector2(labelX, y), LabelColor);
                Draw(st.Value, new Vector2(valueX, y), ValueColor);
                y += RowHeight;
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
