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

    // P761c — native's character sheet is an opaque full screen; the port draws
    // it over the flight HUD, so anything below ~0.99 lets the gauges/labels
    // print through the sheet's text. Near-opaque keeps the blue cast only.
    internal const float PanelAlpha = 0.995f;
    private static readonly Color PanelBg = new(0.014f, 0.020f, 0.029f, PanelAlpha);
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
    /// is null (local player not yet in the roster — nothing honest to show).
    /// P761 — <paramref name="profile"/> adds the profile-description section:
    /// the applied (read-only) text, or the live draft + apply hint while
    /// editing (native CharInfoScreen's Edit/Apply pane,
    /// CharInfoScreen.cpp:837-880).</summary>
    public void Draw(Viewport viewport, PilotInfoLayout.PilotInfo? info,
        PilotInfoLayout.ProfileEditor? profile = null)
    {
        if (info is not { } p) return;

        // P761 — the profile section's lines (description + state hint).
        string? descriptionLine = null, profileHint = null;
        if (profile is { } prof)
        {
            descriptionLine = prof.Editing
                ? "DESC: " + ScoreboardLayout.AsciiSafe(prof.Draft) + "_"
                : "DESC: " + (prof.Description.Length == 0 ? "(none)" : ScoreboardLayout.AsciiSafe(prof.Description));
            profileHint = prof.Editing
                ? "EDITING  ENTER/CTRL+P APPLY  ESC CANCEL"
                : "CTRL+P EDIT";
        }

        // Measure to size the panel: the wider of the title header, the name
        // header, the rank line and the widest (label + value) row — plus the
        // P761 profile lines when present.
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
        if (descriptionLine is { } dl) rowW = MathF.Max(rowW, _small.MeasureString(dl).X);
        if (profileHint is { } ph) rowW = MathF.Max(rowW, _small.MeasureString(ph).X);
        float contentW = MathF.Max(titleW, MathF.Max(nameW, MathF.Max(rankW, rowW)));
        float panelW = contentW + 2f * Padding;

        // P761b — advance each line by the FONT'S OWN line spacing. The name
        // line uses the big font, which is taller than a small-font row; the
        // old fixed RowHeight advance made "RANK:" collide with the name/"S0"
        // title.
        float headerH = _small.LineSpacing + 4f;
        float nameH = _big.LineSpacing + 6f;
        float rankH = _small.LineSpacing + 8f;

        // Header + name + rank + per-stat rows + vertical padding (+ the P761
        // profile section: description line + hint).
        float panelH = headerH
                     + nameH
                     + rankH
                     + p.Stats.Count * RowHeight
                     + (profile is null ? 0f : 2f * RowHeight)   // P761 description + hint
                     + 2f * Padding;
        panelH = MathF.Min(panelH, viewport.Height * 0.92f);

        // Anchored lower-left so it never overlaps the upper scoreboard /
        // centre reticle / bottom bars.
        float panelX = 24f;
        float panelY = viewport.Height - panelH - 24f;

        float textX = panelX + Padding;
        float labelX = textX;
        // P761c — values right-align to the panel's INNER right edge (the same
        // content width the panel was measured from), not Padding short of it.
        // The old x ran the widest value over the border (the "0.0" combat
        // rating in pilotinfo-profile-720p.png).
        float valueRight = textX + contentW;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            float y = panelY + Padding;
            Draw("CHARACTER SHEET  (P TOGGLE)", new Vector2(textX, y), HeaderColor);
            y += headerH;

            // The pilot's name + side, slightly larger (big font) as the panel title.
            _sb.DrawString(_big, $"{p.Name}  {p.SideLabel}", new Vector2(textX, y),
                NameColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            y += nameH;

            // The resolved rank name, prominent (native draws the rank banner).
            Draw($"RANK: {p.RankName}", new Vector2(textX, y), RankColor);
            y += rankH;

            foreach (var st in p.Stats)
            {
                if (st.Label == "RANK NAME") continue;   // already shown as the banner above
                Draw(st.Label, new Vector2(labelX, y), LabelColor);
                // Clamp a too-wide value to the inner width, then right-align it.
                string value = st.Value;
                float valueW = _small.MeasureString(value).X;
                float maxValueW = valueRight - textX;
                if (valueW > maxValueW)
                {
                    value = TeamPaneRenderer.Fit(value, _small, maxValueW);
                    valueW = _small.MeasureString(value).X;
                }
                Draw(value, new Vector2(ValueLeft(textX, valueRight, valueW), y), ValueColor);
                y += RowHeight;
            }

            // P761 — the profile-description section (native's edit pane +
            // display string). The draft is drawn with a '_' cursor stand-in
            // while editing; the applied text read-only otherwise.
            if (profile is { } && descriptionLine is { } dLine && profileHint is { } pHint)
            {
                y += 8f;
                Draw(dLine, new Vector2(textX, y), profile.Editing ? HeaderColor : ValueColor);
                y += RowHeight;
                Draw(pHint, new Vector2(textX, y), LabelColor);
            }
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>P761c — the left x that right-aligns a <paramref name="valueWidth"/>
    /// value at <paramref name="innerRight"/>, never left of the text column.
    /// Unit-tested headlessly (the renderer needs a graphics device).</summary>
    internal static float ValueLeft(float textLeft, float innerRight, float valueWidth)
        => MathF.Max(textLeft, innerRight - valueWidth);

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
