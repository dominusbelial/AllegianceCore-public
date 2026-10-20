using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P262 — draws the real game-over result screen: a large, centred,
/// high-contrast panel over the frozen scene announcing the winner and the
/// <em>real</em> reason text from the wire, exactly the "why did everything
/// just stop" answer the mission-end previously hid (before this the client
/// only played a sound and set a flag — <c>Game1</c>).
///
/// Text is supplied already-resolved by <see cref="GameOverPanelInfo.From"/>
/// from the live <c>FM_S_GAME_OVER(62)</c> snapshot — never a hardcoded
/// placeholder. Lay out here, derive there.
///
/// <para>P752 — the panel now carries native's two sortable result tables
/// (<c>gameoverscreen.cpp:840-1009</c>): the per-SIDE totals table and the
/// per-player results table, both built by the pure
/// <see cref="GameOverTablesLayout"/> and both with clickable column headers
/// (first click sorts ascending — low values on top — second click reverses). The header hit
/// rectangles of the last draw are exposed via
/// <see cref="LastSideHeaderRects"/>/<see cref="LastPlayerHeaderRects"/> so
/// Game1's click routing hits exactly what was painted.</para>
/// </summary>
public sealed class GameOverScreenRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    // High-contrast palette on the teal HUD-accent family: a very dark backdrop
    // panel edged in bright teal, a gold title banner (gold = the starmap's
    // "you are here" highlight color) so the eye lands on it instantly.
    private static readonly Color PanelBg = new(0.008f, 0.018f, 0.026f, 0.965f);
    private static readonly Color PanelBorder = new(0.24f, 0.72f, 0.66f);
    private static readonly Color TopAccent = new(0.32f, 0.96f, 0.86f);
    private static readonly Color TitleVictory = new(1.00f, 0.90f, 0.34f);
    private static readonly Color TitleDefeat = new(1.00f, 0.58f, 0.48f);
    private static readonly Color TitleDraw = new(0.85f, 0.86f, 0.88f);
    private static readonly Color WinnerColor = new(0.40f, 0.94f, 0.88f);
    private static readonly Color ReasonColor = new(0.94f, 0.97f, 0.96f);
    private static readonly Color SubColor = new(0.60f, 0.76f, 0.74f);
    private static readonly Color PositionDim = new(0.66f, 0.70f, 0.69f);
    private static readonly Color CommanderColor = new(1.00f, 0.93f, 0.55f);

    /// <summary>The two tables plus their sort states, as built by
    /// <see cref="GameOverTablesLayout"/> from the live wire snapshot.</summary>
    public sealed record Tables(
        IReadOnlyList<GameOverTablesLayout.SideRow> SideRows,
        IReadOnlyList<GameOverTablesLayout.PlayerRow> PlayerRows,
        GameOverTablesLayout.SideSort SideSort,
        GameOverTablesLayout.PlayerSort PlayerSort,
        IReadOnlyDictionary<short, Color> SideColors);

    public GameOverScreenRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Column-header hit rectangles recorded by the most recent
    /// <see cref="Draw"/>, in column order — Game1's click routing reads these
    /// so a click always lands where the header was painted (native's
    /// button-bar, gameoverscreen.cpp:840-874,922-956).</summary>
    public IReadOnlyList<(GameOverTablesLayout.SideColumn Column, Rectangle Rect)> LastSideHeaderRects { get; private set; } = [];
    public IReadOnlyList<(GameOverTablesLayout.PlayerColumn Column, Rectangle Rect)> LastPlayerHeaderRects { get; private set; } = [];

    /// <summary>Draw the game-over result panel for the current viewport: the
    /// winner/reason header plus the P752 side-totals and player-results
    /// tables from <paramref name="tables"/> (already sorted by
    /// <see cref="GameOverTablesLayout"/>; the active sort column is
    /// underlined with its direction arrow).</summary>
    public void Draw(Viewport viewport, GameOverPanelInfo info, Tables tables, int? nextMatchSeconds = null)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Large, centred, unmissable panel — sized to hold both tables.
        float panelW = System.MathF.Min(w * 0.94f, 1240f);
        float panelH = System.MathF.Min(h * 0.94f, 760f);
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float pad = 30f;

        Color titleColor = info.Outcome switch
        {
            GameOverOutcome.Victory => TitleVictory,
            GameOverOutcome.Defeat  => TitleDefeat,
            _                       => TitleDraw,
        };

        string title = info.Title;
        var tsz = _big.MeasureString(title);
        float titleScale = System.MathF.Min(1f, (panelW - 2f * pad) / tsz.X);
        float titleX = panelX + (panelW - tsz.X * titleScale) / 2f;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder, 2f);
            FillRect(panelX, panelY, panelW, 4f, TopAccent);   // bright top edge

            float cursorY = panelY + 18f;
            float bigRow = _big.MeasureString("Mg").Y * titleScale;
            DrawBig(title, new Vector2(titleX, cursorY), titleColor, titleScale);
            cursorY += bigRow + 12f;

            // Winner banner (empty on a draw — nothing to crown).
            if (info.WinnerLine.Length > 0)
            {
                var wsz = _small.MeasureString(info.WinnerLine);
                DrawSmall(info.WinnerLine,
                    new Vector2(panelX + (panelW - wsz.X) / 2f, cursorY), WinnerColor, 1.2f);
                cursorY += _small.MeasureString("Mg").Y * 1.2f + 8f;
            }

            // Reason text, single centred line (clipped to the panel width).
            string reason = Clip(info.Reason, panelW - 2f * pad);
            var rsz = _small.MeasureString(reason);
            DrawSmall(reason, new Vector2(panelX + (panelW - rsz.X) / 2f, cursorY), ReasonColor, 1f);
            cursorY += rsz.Y + 6f;

            if (nextMatchSeconds is { } seconds)
            {
                string timer = GameOverPanelInfo.NextMatchTimer(seconds);
                var size = _small.MeasureString(timer);
                DrawSmall(timer, new Vector2(panelX + (panelW - size.X) / 2f, cursorY), WinnerColor, 1f);
                cursorY += size.Y + 6f;
            }

            // ── P752: the side-totals table (native SidePainter) ──────────
            float tableX = panelX + pad;
            float tableW = panelW - 2f * pad;
            if (tables.SideRows.Count > 0)
            {
                cursorY += 6f;
                cursorY = DrawSideTable(tableX, cursorY, tableW, tables);
            }

            // ── P752: the player-results table (native PlayerPainter) ──────
            if (tables.PlayerRows.Count > 0)
            {
                cursorY += 12f;
                cursorY = DrawPlayerTable(tableX, cursorY, tableW, tables);
            }

            // Footer + Back button at the bottom of the panel.
            FillRect(panelX, panelY + panelH - 52f, panelW, 1f,
                new Color(PanelBorder.R, PanelBorder.G, PanelBorder.B, 0.5f));

            // P481 — Back button (native m_pbuttonBack / OnButtonBack,
            // gameoverscreen.cpp:609-625): the only control on the game-over
            // screen. The port is keyboard-driven, so Backspace triggers it;
            // the button is drawn to match native's on-screen affordance.
            float btnW = 120f, btnH = 30f;
            float btnX = panelX + (panelW - btnW) / 2f;
            float btnY = panelY + panelH - 46f;
            FillRect(btnX, btnY, btnW, btnH, new Color(PanelBorder.R, PanelBorder.G, PanelBorder.B, 0.22f));
            OutlineRect(btnX, btnY, btnW, btnH, PanelBorder, 1.5f);
            string backLabel = "BACK";
            var backSz = _small.MeasureString(backLabel);
            DrawSmall(backLabel, new Vector2(btnX + (btnW - backSz.X) / 2f, btnY + (btnH - backSz.Y) / 2f),
                SubColor, 1f);
            string backHint = "(BACKSPACE)";
            var hintSz = _small.MeasureString(backHint);
            DrawSmall(backHint, new Vector2(panelX + (panelW - hintSz.X) / 2f, btnY + btnH + 3f),
                SubColor, 0.8f);
        }
        finally
        {
            _sb.End();
        }
    }

    private static readonly string[] SideHeaders =
        ["#", "SIDE", "BK", "BC", "FL", "AR", "K", "D"];
    private static readonly GameOverTablesLayout.SideColumn[] SideHeaderColumns =
    [
        GameOverTablesLayout.SideColumn.Position, GameOverTablesLayout.SideColumn.Name,
        GameOverTablesLayout.SideColumn.BaseKills, GameOverTablesLayout.SideColumn.BaseCaptures,
        GameOverTablesLayout.SideColumn.Flags, GameOverTablesLayout.SideColumn.Artifacts,
        GameOverTablesLayout.SideColumn.Kills, GameOverTablesLayout.SideColumn.Deaths,
    ];

    private float DrawSideTable(float x, float y, float maxWidth, Tables tables)
    {
        const float gap = 14f;
        var widths = new float[SideHeaders.Length];
        for (int c = 0; c < SideHeaders.Length; c++)
            widths[c] = _small.MeasureString(SideHeaders[c]).X;
        foreach (var r in tables.SideRows)
        {
            Stretch(ref widths[0], r.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[1], ScoreboardLayout.AsciiSafe(r.Name), _small);
            Stretch(ref widths[2], r.BaseKills.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[3], r.BaseCaptures.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[4], r.Flags.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[5], r.Artifacts.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[6], r.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[7], r.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
        }

        var headerRects = new List<(GameOverTablesLayout.SideColumn, Rectangle)>();
        float rowH = _small.MeasureString("Mg").Y + 2f;
        float cx = x;
        for (int c = 0; c < SideHeaders.Length; c++)
        {
            bool active = SideHeaderColumns[c] == tables.SideSort.Column
                && SideHeaderColumns[c] != GameOverTablesLayout.SideColumn.Position;
            DrawSmall(SideHeaders[c], new Vector2(cx, y), active ? WinnerColor : TopAccent, 1f);
            if (active)
            {
                // Underline + direction arrow marks the active sort column.
                FillRect(cx, y + rowH - 3f, widths[c], 2f, WinnerColor);
                DrawSmall(tables.SideSort.Reverse ? "v" : "^", new Vector2(cx + widths[c] + 2f, y), WinnerColor, 0.8f);
            }
            headerRects.Add((SideHeaderColumns[c],
                new Rectangle((int)cx, (int)y, (int)(widths[c] + gap), (int)rowH)));
            cx += widths[c] + gap;
        }
        LastSideHeaderRects = headerRects;

        float rowY = y + rowH + 2f;
        foreach (var r in tables.SideRows)
        {
            cx = x;
            DrawSmall(r.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Vector2(cx, rowY), r.Position > 1 ? PositionDim : ReasonColor, 1f);
            cx += widths[0] + gap;
            // Native paints the side's name in the side's own colour
            // (gameoverscreen.cpp:277-287).
            Color nameColor = tables.SideColors.TryGetValue(r.SideId, out var sideColor) ? sideColor : ReasonColor;
            DrawSmall(ScoreboardLayout.AsciiSafe(r.Name), new Vector2(cx, rowY), nameColor, 1f);
            cx += widths[1] + gap;
            void Cell(int c, string value)
            {
                DrawSmall(value, new Vector2(cx, rowY), ReasonColor, 1f);
                cx += widths[c] + gap;
            }
            Cell(2, r.BaseKills.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(3, r.BaseCaptures.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(4, r.Flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(5, r.Artifacts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(6, r.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(7, r.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            rowY += rowH;
        }
        return rowY + 4f;
    }

    private static readonly string[] PlayerHeaders =
        ["#", "NAME", "RANK", "BK", "BC", "SCORE", "AST", "K", "D", "TIME"];
    private static readonly GameOverTablesLayout.PlayerColumn[] PlayerHeaderColumns =
    [
        GameOverTablesLayout.PlayerColumn.Position, GameOverTablesLayout.PlayerColumn.Name,
        GameOverTablesLayout.PlayerColumn.Rank, GameOverTablesLayout.PlayerColumn.BaseKills,
        GameOverTablesLayout.PlayerColumn.BaseCaptures, GameOverTablesLayout.PlayerColumn.Score,
        GameOverTablesLayout.PlayerColumn.Assists, GameOverTablesLayout.PlayerColumn.Kills,
        GameOverTablesLayout.PlayerColumn.Deaths, GameOverTablesLayout.PlayerColumn.TimePlayed,
    ];

    private float DrawPlayerTable(float x, float y, float maxWidth, Tables tables)
    {
        const float gap = 14f;
        var widths = new float[PlayerHeaders.Length];
        for (int c = 0; c < PlayerHeaders.Length; c++)
            widths[c] = _small.MeasureString(PlayerHeaders[c]).X;
        foreach (var r in tables.PlayerRows)
        {
            Stretch(ref widths[0], r.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[1], ScoreboardLayout.AsciiSafe(r.Name), _small);
            Stretch(ref widths[2], ScoreboardLayout.AsciiSafe(r.RankName), _small);
            Stretch(ref widths[3], r.BaseKills.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[4], r.BaseCaptures.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[5], r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[6], r.Assists.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[7], r.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[8], r.DeathsOrEjections.ToString(System.Globalization.CultureInfo.InvariantCulture), _small);
            Stretch(ref widths[9], r.TimePlayed, _small);
        }

        var headerRects = new List<(GameOverTablesLayout.PlayerColumn, Rectangle)>();
        float rowH = _small.MeasureString("Mg").Y + 2f;
        float cx = x;
        for (int c = 0; c < PlayerHeaders.Length; c++)
        {
            bool active = PlayerHeaderColumns[c] == tables.PlayerSort.Column
                && PlayerHeaderColumns[c] != GameOverTablesLayout.PlayerColumn.Position;
            DrawSmall(PlayerHeaders[c], new Vector2(cx, y), active ? WinnerColor : TopAccent, 1f);
            if (active)
            {
                FillRect(cx, y + rowH - 3f, widths[c], 2f, WinnerColor);
                DrawSmall(tables.PlayerSort.Reverse ? "v" : "^", new Vector2(cx + widths[c] + 2f, y), WinnerColor, 0.8f);
            }
            headerRects.Add((PlayerHeaderColumns[c],
                new Rectangle((int)cx, (int)y, (int)(widths[c] + gap), (int)rowH)));
            cx += widths[c] + gap;
        }
        LastPlayerHeaderRects = headerRects;

        float rowY = y + rowH + 2f;
        foreach (var r in tables.PlayerRows)
        {
            cx = x;
            DrawSmall(r.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Vector2(cx, rowY), r.Position > 1 ? PositionDim : ReasonColor, 1f);
            cx += widths[0] + gap;
            // Native paints the name in the player's side colour, bold for the
            // commander (gameoverscreen.cpp:117-133). The port has no bold
            // variant of the HUD font, so the commander gets the gold accent.
            Color nameColor = tables.SideColors.TryGetValue(r.SideId, out var sideColor) ? sideColor : ReasonColor;
            if (r.Commander) nameColor = CommanderColor;
            DrawSmall(ScoreboardLayout.AsciiSafe(r.Name), new Vector2(cx, rowY), nameColor, 1f);
            cx += widths[1] + gap;
            DrawSmall(ScoreboardLayout.AsciiSafe(r.RankName), new Vector2(cx, rowY), ReasonColor, 1f);
            cx += widths[2] + gap;
            void Cell(int c, string value)
            {
                DrawSmall(value, new Vector2(cx, rowY), ReasonColor, 1f);
                cx += widths[c] + gap;
            }
            Cell(3, r.BaseKills.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(4, r.BaseCaptures.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(5, r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(6, r.Assists.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(7, r.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(8, r.DeathsOrEjections.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Cell(9, r.TimePlayed);
            rowY += rowH;
        }
        return rowY + 4f;
    }

    private static void Stretch(ref float width, string text, SpriteFont font)
    {
        var sz = font.MeasureString(text);
        if (sz.X > width) width = sz.X;
    }

    private string Clip(string text, float maxWidth)
    {
        string t = ScoreboardLayout.AsciiSafe(text);
        while (t.Length > 1 && _small.MeasureString(t).X > maxWidth)
            t = t[..^1];
        return t.Length == 0 ? " " : t;
    }

    private void DrawSmall(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void DrawBig(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_big, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)System.MathF.Round(x),
            (int)System.MathF.Round(y), (int)System.MathF.Round(w),
            (int)System.MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color, float thick)
    {
        FillRect(x, y, w, thick, color);
        FillRect(x, y + h - thick, w, thick, color);
        FillRect(x, y, thick, h, color);
        FillRect(x + w - thick, y, thick, h, color);
    }
}
