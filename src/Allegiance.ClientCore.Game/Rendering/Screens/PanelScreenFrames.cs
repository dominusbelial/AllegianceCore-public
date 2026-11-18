using System;
using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Screens;

/// <summary>P1042c — one filled rectangle of a screen panel, in draw order. The frame
/// builder reproduces the pre-boundary drawing exactly (backdrop, plate, the four outline
/// edges, the top accent edge, dividers, selection bands), so Classic/Modern paint the same
/// pixels and a skin can see every box.</summary>
public readonly record struct ScreenFill(RectangleF Rect, Color Color, string Id);

/// <summary>
/// P1042c — one text run of a screen panel. <paramref name="Text"/> is the exact run the
/// pre-boundary drawing put at <paramref name="Position"/> (prefix/truncation included);
/// <paramref name="Rect"/> is the run's band (the control's resolved rectangle when
/// <paramref name="Control"/> is true, otherwise the measured ink box). <see cref="Role"/>
/// is what a vector skin paints with and <paramref name="ClassicInk"/> the exact colour the
/// old drawing used, so Classic/Modern cannot drift.
/// </summary>
public readonly record struct PanelTextRow(
    string Id, string Text, Vector2 Position, RectangleF Rect, bool Large, float Scale,
    Color ClassicInk, ScreenInkRole Role, bool Selected, bool Control, float MaxWidth = 0f);

/// <summary>
/// P1042c — an in-game overlay screen as a read-only model: the panel rectangle, every fill
/// in the old draw order, every text run at its resolved anchor, and (optionally) the
/// native-art block a family draws (the ready-check civilization icon). No model, cursor,
/// command or clock is reachable from here; the actions stay in the game's dispatchers.
/// </summary>
public readonly record struct ScreenPanelFrame(
    Viewport Viewport,
    string Family,
    RectangleF Panel,
    IReadOnlyList<ScreenFill> Fills,
    IReadOnlyList<PanelTextRow> Rows,
    ScreenInks Inks,
    Texture2D? Art = null,
    RectangleF ArtBox = default,
    float ArtScale = 1f)
{
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;
}

/// <summary>
/// P1042c — the frame builders of the panel-style in-game screens. Each builder reproduces
/// the arithmetic of the renderer that drew the screen before the boundary (same constants,
/// same rounding), so the Classic painter is byte-identical and the Jet chrome frames exactly
/// the boxes the pilot already sees. One builder is used by <c>Game1</c> and by the proof
/// tool, so a fixture cannot drift from the production frame.
/// </summary>
public static class PanelScreenFrames
{
    public const string SideSelect = "sideselect";
    public const string ReadyCheck = "readycheck";
    public const string Briefing = "briefing";
    public const string TeamPane = "teampane";
    public const string QuickChat = "quickchat";
    public const string KeyBind = "keybind";
    public const string ConnectionLost = "connectionlost";

    private static readonly Color SideSelectBackdrop = new(0.006f, 0.009f, 0.013f, 0.968f);
    private static readonly Color SideSelectPanel = new(0.022f, 0.034f, 0.050f, 0.952f);
    private static readonly Color SideSelectBorder = new(0.72f, 0.80f, 0.50f);
    private static readonly Color SideSelectTitle = new(0.94f, 0.96f, 0.86f);
    private static readonly Color SideSelectItem = new(0.88f, 0.92f, 0.88f);
    private static readonly Color SideSelectHighlight = new(0.60f, 1.00f, 0.66f);
    private static readonly Color SideSelectMuted = new(0.50f, 0.55f, 0.55f);
    private static readonly Color SideSelectHint = new(0.60f, 0.68f, 0.64f);

    private static readonly Color ReadyBackdrop = new(0.005f, 0.008f, 0.014f, 0.968f);
    private static readonly Color ReadyPanel = new(0.020f, 0.032f, 0.052f, 0.952f);
    private static readonly Color ReadyBorder = new(0.55f, 0.75f, 0.90f);
    private static readonly Color ReadyTitle = new(0.90f, 0.95f, 0.98f);
    private static readonly Color ReadyItem = new(0.86f, 0.90f, 0.92f);
    private static readonly Color ReadyHighlight = new(0.55f, 1.00f, 0.75f);
    private static readonly Color ReadyMuted = new(0.50f, 0.56f, 0.58f);
    private static readonly Color ReadyHint = new(0.60f, 0.72f, 0.70f);
    private static readonly Color ReadyInfo = new(0.92f, 0.85f, 0.55f);
    private static readonly Color ReadyError = new(1.00f, 0.52f, 0.42f);

    private static readonly Color BriefPanel = new(0.016f, 0.030f, 0.045f, 0.935f);
    private static readonly Color BriefBorder = new(0.16f, 0.45f, 0.44f);
    private static readonly Color BriefHeader = new(0.40f, 0.88f, 0.84f);
    private static readonly Color BriefTitle = new(0.92f, 0.96f, 0.97f);
    private static readonly Color BriefBody = new(0.84f, 0.92f, 0.92f);
    private static readonly Color BriefCountdown = new(1.00f, 0.78f, 0.30f);
    private static readonly Color BriefPreparing = new(0.52f, 0.72f, 0.70f);
    private static readonly Color BriefHint = new(0.62f, 0.74f, 0.74f);

    private static readonly Color TeamPanel = new(0.012f, 0.024f, 0.032f, TeamPaneRenderer.PanelAlpha);
    private static readonly Color TeamBorder = new(0.55f, 0.78f, 0.60f);
    private static readonly Color TeamTitle = new(0.95f, 0.96f, 0.80f);
    private static readonly Color TeamSide = new(0.75f, 0.85f, 0.60f);
    private static readonly Color TeamPilot = new(0.88f, 0.92f, 0.88f);
    private static readonly Color TeamHighlight = new(0.62f, 1.00f, 0.66f);
    private static readonly Color TeamMoney = new(1.00f, 0.90f, 0.50f);
    private static readonly Color TeamBallot = new(0.60f, 0.80f, 1.00f);
    private static readonly Color TeamNote = new(0.52f, 0.58f, 0.58f);
    private static readonly Color TeamHint = new(0.60f, 0.68f, 0.64f);

    private static readonly Color ChatPanel = new(0.010f, 0.035f, 0.036f, 0.72f);
    private static readonly Color ChatAccent = new(0.09f, 0.92f, 0.83f);
    private static readonly Color ChatTextMain = new(0.88f, 0.96f, 0.95f);
    private static readonly Color ChatTextDim = new(0.48f, 0.64f, 0.62f);
    private static readonly Color ChatLeaf = new(0.96f, 0.96f, 0.90f);

    private static readonly Color BindPanel = new(0.012f, 0.028f, 0.038f, 0.96f);
    private static readonly Color BindBorder = new(0.16f, 0.42f, 0.40f);
    private static readonly Color BindHeader = new(0.42f, 0.94f, 0.88f);
    private static readonly Color BindGroup = new(0.50f, 0.90f, 0.78f);
    private static readonly Color BindKey = new(1.00f, 0.90f, 0.55f);
    private static readonly Color BindDesc = new(0.85f, 0.93f, 0.92f);
    private static readonly Color BindSel = new(0.06f, 0.30f, 0.28f, 0.55f);
    private static readonly Color BindWarn = new(1.00f, 0.62f, 0.30f);
    private static readonly Color BindNote = new(0.58f, 0.74f, 0.72f);

    private static readonly Color LostPanel = new(0.06f, 0.012f, 0.010f, 0.965f);
    private static readonly Color LostBorder = new(0.98f, 0.34f, 0.20f);
    private static readonly Color LostTop = new(1.00f, 0.55f, 0.24f);
    private static readonly Color LostTitle = new(1.00f, 0.38f, 0.30f);
    private static readonly Color LostBody = new(0.94f, 0.97f, 0.96f);
    private static readonly Color LostSub = new(0.72f, 0.78f, 0.76f);

    private static ScreenInks Inks(Color title, Color body, Color dim)
        => new(title, body, dim, new Color(1.00f, 0.45f, 0.42f));

    private static void AddPanel(List<ScreenFill> fills, float x, float y, float w, float h,
        Color fill, Color border, float borderThickness, float topAccent, Color? accent = null)
    {
        fills.Add(new(new RectangleF(x, y, w, h), fill, "panel"));
        if (borderThickness > 0f)
        {
            fills.Add(new(new RectangleF(x, y, w, borderThickness), border, "border.top"));
            fills.Add(new(new RectangleF(x, y + h - borderThickness, w, borderThickness), border, "border.bottom"));
            fills.Add(new(new RectangleF(x, y, borderThickness, h), border, "border.left"));
            fills.Add(new(new RectangleF(x + w - borderThickness, y, borderThickness, h), border, "border.right"));
            if (topAccent > 0f)
                fills.Add(new(new RectangleF(x, y, w, topAccent), accent ?? border, "accent.top"));
        }
    }

    private static Vector2 Measure(SpriteFont font, string text) => font.MeasureString(text);

    // ── side select ──────────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="SideSelectOverlayRenderer"/>'s arithmetic exactly
    /// (padding 24, line gap 3, backdrop, 1 px outline, 3 px top accent).</summary>
    public static ScreenPanelFrame SideSelectFrame(IReadOnlyList<SideSelectLayout.Row> rows,
        Viewport viewport, SpriteFont small, SpriteFont big)
    {
        float w = viewport.Width, h = viewport.Height;
        const float padding = 24f, lineGap = 3f;
        float maxLine = 0f, totalH = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Kind == SideSelectLayout.Kind.Blank) { totalH += lineGap; continue; }
            var f = r.Kind == SideSelectLayout.Kind.Title ? big : small;
            totalH += f.LineSpacing + lineGap;
            maxLine = MathF.Max(maxLine, Measure(f, r.Text).X);
        }
        if (totalH <= 0f) totalH = 1f;

        float panelW = MathF.Min(maxLine + 2f * padding, w * 0.96f);
        float panelH = totalH + 2f * padding - lineGap;
        float panelX = MathF.Max(12f, (w - panelW) / 2f);
        float panelY = MathF.Max(12f, (h - panelH) / 2f);

        var fills = new List<ScreenFill>();
        fills.Add(new(new RectangleF(0f, 0f, w, h), SideSelectBackdrop, "backdrop"));
        AddPanel(fills, panelX, panelY, panelW, panelH, SideSelectPanel, SideSelectBorder, 1f, 3f);

        var text = new List<PanelTextRow>();
        float y = panelY + padding;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Kind == SideSelectLayout.Kind.Blank) { y += lineGap; continue; }
            bool isTitle = r.Kind == SideSelectLayout.Kind.Title;
            var f = isTitle ? big : small;
            var ink = ColorFor(r);
            var size = Measure(f, r.Text);
            var inkBox = new RectangleF(panelX + padding, y, size.X, size.Y);
            bool item = r.Kind == SideSelectLayout.Kind.Item;
            text.Add(new(item ? "sideselect.row" : "", r.Text, new Vector2(panelX + padding, y),
                item ? new RectangleF(panelX + padding, y, panelW - 2f * padding, f.LineSpacing) : inkBox,
                isTitle, 1f, ink, RoleFor(r), r.Highlight, item, panelW - 2f * padding));
            y += f.LineSpacing + lineGap;
        }
        return new ScreenPanelFrame(viewport, SideSelect, new RectangleF(panelX, panelY, panelW, panelH),
            fills, text, Inks(SideSelectTitle, SideSelectItem, SideSelectHint));
    }

    private static Color ColorFor(SideSelectLayout.Row r) => r.Kind switch
    {
        SideSelectLayout.Kind.Title => SideSelectTitle,
        SideSelectLayout.Kind.Hint => SideSelectHint,
        SideSelectLayout.Kind.Item => r.Highlight ? SideSelectHighlight : SideSelectItem,
        SideSelectLayout.Kind.Muted => SideSelectMuted,
        _ => SideSelectItem,
    };

    private static ScreenInkRole RoleFor(SideSelectLayout.Row r) => r.Kind switch
    {
        SideSelectLayout.Kind.Title => ScreenInkRole.Title,
        SideSelectLayout.Kind.Hint => ScreenInkRole.Dim,
        SideSelectLayout.Kind.Muted => ScreenInkRole.Dim,
        _ => r.Highlight ? ScreenInkRole.Highlight : ScreenInkRole.Body,
    };

    // ── ready check ──────────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="ReadyCheckOverlayRenderer"/> (padding 24, gap 3, the
    /// optional 88 px reservation for the civilization art, 1 px outline, 3 px top accent).</summary>
    public static ScreenPanelFrame ReadyCheckFrame(IReadOnlyList<ReadyCheckLayout.Row> rows,
        Viewport viewport, SpriteFont small, SpriteFont big, Texture2D? civIcon = null)
    {
        float w = viewport.Width, h = viewport.Height;
        const float padding = 24f, lineGap = 3f;
        float maxLine = 0f, totalH = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Kind == ReadyCheckLayout.Kind.Blank) { totalH += lineGap; continue; }
            var f = r.Kind == ReadyCheckLayout.Kind.Title ? big : small;
            totalH += f.LineSpacing + lineGap;
            maxLine = MathF.Max(maxLine, Measure(f, r.Text).X);
        }
        if (totalH <= 0f) totalH = 1f;
        if (civIcon is not null) maxLine += 88f;

        float panelW = MathF.Min(maxLine + 2f * padding, w * 0.96f);
        float panelH = totalH + 2f * padding - lineGap;
        float panelX = MathF.Max(12f, (w - panelW) / 2f);
        float panelY = MathF.Max(12f, (h - panelH) / 2f);

        var fills = new List<ScreenFill>();
        fills.Add(new(new RectangleF(0f, 0f, w, h), ReadyBackdrop, "backdrop"));
        AddPanel(fills, panelX, panelY, panelW, panelH, ReadyPanel, ReadyBorder, 1f, 3f);

        var text = new List<PanelTextRow>();
        float y = panelY + padding;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Kind == ReadyCheckLayout.Kind.Blank) { y += lineGap; continue; }
            bool isTitle = r.Kind == ReadyCheckLayout.Kind.Title;
            var f = isTitle ? big : small;
            var ink = ReadyColorFor(r);
            var size = Measure(f, r.Text);
            var inkBox = new RectangleF(panelX + padding, y, size.X, size.Y);
            bool item = r.Kind == ReadyCheckLayout.Kind.Item;
            text.Add(new(item ? "readycheck.row" : "", r.Text, new Vector2(panelX + padding, y),
                item ? new RectangleF(panelX + padding, y, panelW - 2f * padding, f.LineSpacing) : inkBox,
                isTitle, 1f, ink, ReadyRoleFor(r), r.Highlight, item, panelW - 2f * padding));
            y += f.LineSpacing + lineGap;
        }

        // P802 — the side's civilization art inside the panel's top-right corner, scaled into
        // a 72 px box and never past its native aspect.
        if (civIcon is not null)
        {
            const float box = 72f;
            float scale = MathF.Min(1f, MathF.Min(box / civIcon.Width, box / civIcon.Height));
            float iw = civIcon.Width * scale;
            return new ScreenPanelFrame(viewport, ReadyCheck, new RectangleF(panelX, panelY, panelW, panelH),
                fills, text, Inks(ReadyTitle, ReadyItem, ReadyHint), civIcon,
                new RectangleF(panelX + panelW - padding - iw, panelY + padding, iw, civIcon.Height * scale),
                scale);
        }
        return new ScreenPanelFrame(viewport, ReadyCheck, new RectangleF(panelX, panelY, panelW, panelH),
            fills, text, Inks(ReadyTitle, ReadyItem, ReadyHint));
    }

    private static Color ReadyColorFor(ReadyCheckLayout.Row r) => r.Kind switch
    {
        ReadyCheckLayout.Kind.Title => ReadyTitle,
        ReadyCheckLayout.Kind.Hint => ReadyHint,
        ReadyCheckLayout.Kind.Info => ReadyInfo,
        ReadyCheckLayout.Kind.Item => r.Highlight ? ReadyHighlight : ReadyItem,
        ReadyCheckLayout.Kind.Muted => ReadyMuted,
        ReadyCheckLayout.Kind.Error => ReadyError,
        _ => ReadyItem,
    };

    private static ScreenInkRole ReadyRoleFor(ReadyCheckLayout.Row r) => r.Kind switch
    {
        ReadyCheckLayout.Kind.Title => ScreenInkRole.Title,
        ReadyCheckLayout.Kind.Hint => ScreenInkRole.Dim,
        ReadyCheckLayout.Kind.Muted => ScreenInkRole.Dim,
        ReadyCheckLayout.Kind.Info => ScreenInkRole.Body,
        ReadyCheckLayout.Kind.Error => ScreenInkRole.Warning,
        _ => r.Highlight ? ScreenInkRole.Highlight : ScreenInkRole.Body,
    };

    // ── mission briefing ─────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="MissionBriefingRenderer"/>, including its greedy wrap
    /// (<see cref="MissionBriefingLayout.WrapWords"/>) and block stacking.</summary>
    public static ScreenPanelFrame BriefingFrame(string title, string body, int? remainingSeconds,
        string objective, Viewport viewport, SpriteFont small, SpriteFont big)
    {
        const string header = "MISSION BRIEFING";
        const string hint = "(PRESS ENTER OR ESC TO DISMISS)";
        float w = viewport.Width, h = viewport.Height;

        string safeTitle = ScoreboardLayout.AsciiSafe(title);
        string safeBody = string.Join('\n', body.Split('\n').SelectAll(ScoreboardLayout.AsciiSafe));
        string safeObjective = ScoreboardLayout.AsciiSafe(objective);
        string countdown = remainingSeconds is { } rem
            ? $"MISSION STARTS IN {rem}s..."
            : "MISSION PREPARING...";

        float headerW = Measure(small, header).X;
        float titleW = Measure(big, safeTitle).X;
        float objectiveW = safeObjective.Length > 0 ? Measure(small, safeObjective).X : 0f;
        float countdownW = Measure(small, countdown).X;
        float hintW = Measure(small, hint).X;
        float bodyOneLine = Measure(small, safeBody).X;

        float maxLine = MathF.Max(MathF.Max(MathF.Max(headerW, titleW), objectiveW),
            MathF.Max(MathF.Max(countdownW, hintW), bodyOneLine));
        float viewportLimit = w * MissionBriefingLayout.MaxPanelWidthFraction - 2f * MissionBriefingLayout.Padding;
        float contentW = MathF.Min(maxLine, viewportLimit);
        if (contentW <= 0f) contentW = 1f;

        float spaceW = Measure(small, " ").X;
        IReadOnlyList<string> bodyLines =
            MissionBriefingLayout.WrapWords(safeBody, s => Measure(small, s).X, spaceW, contentW);

        float actualContentW = contentW;
        for (int i = 0; i < bodyLines.Count; i++)
            actualContentW = MathF.Max(actualContentW, Measure(small, bodyLines[i]).X);

        float panelW = actualContentW + 2f * MissionBriefingLayout.Padding;
        float titleH = Measure(big, "M").Y;
        float smallH = Measure(small, "M").Y;
        float objectiveH = safeObjective.Length > 0 ? smallH : 0f;
        float panelH = MissionBriefingLayout.Padding + smallH + MissionBriefingLayout.BlockGap + titleH
                     + objectiveH + MissionBriefingLayout.BlockGap
                     + bodyLines.Count * MissionBriefingLayout.LineHeight + MissionBriefingLayout.BlockGap
                     + smallH + MissionBriefingLayout.BlockGap + smallH + MissionBriefingLayout.Padding;

        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        float cx = w / 2f;

        float headerY = panelY + MissionBriefingLayout.Padding;
        float titleY = headerY + smallH + MissionBriefingLayout.BlockGap;
        float bodyY = titleY + titleH + objectiveH + MissionBriefingLayout.BlockGap;
        float countdownY = bodyY + bodyLines.Count * MissionBriefingLayout.LineHeight + MissionBriefingLayout.BlockGap;
        float hintY = countdownY + smallH + MissionBriefingLayout.BlockGap;

        var fills = new List<ScreenFill>();
        AddPanel(fills, panelX, panelY, panelW, panelH, BriefPanel, BriefBorder, 1f, 2f);

        var rows = new List<PanelTextRow>(bodyLines.Count + 5);
        AddCentered(small, rows, header, cx, headerY, BriefHeader, ScreenInkRole.Title, large: false);
        AddCentered(big, rows, safeTitle, cx, titleY, BriefTitle, ScreenInkRole.Title, large: true);
        if (safeObjective.Length > 0)
            AddCentered(small, rows, safeObjective, cx, titleY + titleH, BriefBody, ScreenInkRole.Body, large: false);
        for (int i = 0; i < bodyLines.Count; i++)
            AddCentered(small, rows, bodyLines[i], cx, bodyY + i * MissionBriefingLayout.LineHeight,
                BriefBody, ScreenInkRole.Body, large: false);
        AddCentered(small, rows, countdown, cx, countdownY,
            remainingSeconds is { } ? BriefCountdown : BriefPreparing, ScreenInkRole.Warning, large: false);
        AddCentered(small, rows, hint, cx, hintY, BriefHint, ScreenInkRole.Dim, large: false);

        return new ScreenPanelFrame(viewport, Briefing, new RectangleF(panelX, panelY, panelW, panelH),
            fills, rows, Inks(BriefTitle, BriefBody, BriefHint));
    }

    private static void AddCentered(SpriteFont font, List<PanelTextRow> rows, string text, float cx, float y,
        Color ink, ScreenInkRole role, bool large)
    {
        var size = Measure(font, text);
        rows.Add(new("", text, new Vector2(cx - size.X / 2f, y), new RectangleF(cx - size.X / 2f, y, size.X, size.Y),
            large, 1f, ink, role, false, false));
    }

    // ── team pane (console team page) ────────────────────────────────────────
    /// <summary>Mirrors <see cref="TeamPaneRenderer"/> (padding 22, gap 2, blank rows 8 px,
    /// the fit-to-panel truncation, 1 px outline, 3 px top accent, compact placement).</summary>
    public static ScreenPanelFrame TeamPaneFrame(IReadOnlyList<TeamPaneLayout.Line> lines,
        Viewport viewport, SpriteFont small, SpriteFont big, bool compact = false)
    {
        float w = viewport.Width, h = viewport.Height;
        const float padding = 22f, lineGap = 2f;
        float maxLine = 0f, totalH = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (ln.Kind == TeamPaneLayout.LineKind.Blank) { totalH += lineGap + 6f; continue; }
            var f = ln.Kind == TeamPaneLayout.LineKind.Title ? big : small;
            totalH += f.LineSpacing + lineGap;
            maxLine = MathF.Max(maxLine, Measure(f, ln.Text).X);
        }
        if (totalH <= 0f) totalH = 1f;

        float panelW = MathF.Min(maxLine + 2f * padding, w * 0.985f);
        float panelH = MathF.Min(totalH + 2f * padding - lineGap, h * 0.94f);
        float panelX = MathF.Max(8f, compact ? w - panelW - 8 : (w - panelW) / 2f);
        float panelY = MathF.Max(8f, compact ? 80 : (h - panelH) / 2f);

        var fills = new List<ScreenFill>();
        AddPanel(fills, panelX, panelY, panelW, panelH, TeamPanel, TeamBorder, 1f, 3f);

        var rows = new List<PanelTextRow>();
        float y = panelY + padding;
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (ln.Kind == TeamPaneLayout.LineKind.Blank) { y += lineGap + 6f; continue; }
            bool isTitle = ln.Kind == TeamPaneLayout.LineKind.Title;
            var f = isTitle ? big : small;
            string fitted = TeamPaneRenderer.Fit(ln.Text, f, panelW - 2f * padding);
            var size = Measure(f, fitted);
            rows.Add(new("", fitted, new Vector2(panelX + padding, y), new RectangleF(panelX + padding, y, size.X, size.Y),
                isTitle, 1f, TeamColorFor(ln), TeamRoleFor(ln), ln.Highlight, false, panelW - 2f * padding));
            y += f.LineSpacing + lineGap;
        }
        return new ScreenPanelFrame(viewport, TeamPane, new RectangleF(panelX, panelY, panelW, panelH),
            fills, rows, Inks(TeamTitle, TeamPilot, TeamHint));
    }

    private static Color TeamColorFor(TeamPaneLayout.Line l) => l.Kind switch
    {
        TeamPaneLayout.LineKind.Title => TeamTitle,
        TeamPaneLayout.LineKind.SideHeader => TeamSide,
        TeamPaneLayout.LineKind.Pilot => l.Highlight ? TeamHighlight : TeamPilot,
        TeamPaneLayout.LineKind.Money => TeamMoney,
        TeamPaneLayout.LineKind.Ballot => TeamBallot,
        TeamPaneLayout.LineKind.Note => TeamNote,
        TeamPaneLayout.LineKind.Hint => TeamHint,
        _ => TeamPilot,
    };

    private static ScreenInkRole TeamRoleFor(TeamPaneLayout.Line l) => l.Kind switch
    {
        TeamPaneLayout.LineKind.Title => ScreenInkRole.Title,
        TeamPaneLayout.LineKind.Note => ScreenInkRole.Dim,
        TeamPaneLayout.LineKind.Hint => ScreenInkRole.Dim,
        TeamPaneLayout.LineKind.Pilot => l.Highlight ? ScreenInkRole.Highlight : ScreenInkRole.Body,
        _ => ScreenInkRole.Body,
    };

    // ── quick chat ───────────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="QuickChatMenuRenderer"/> (margin 30, row height 26, title
    /// 34, 2 px top and left accent bars, no outline).</summary>
    public static ScreenPanelFrame QuickChatFrame(QuickChatMenu menu, IReadOnlyList<QuickChatMenu> crumbTrail,
        Viewport viewport, SpriteFont font)
    {
        float w = viewport.Width, h = viewport.Height;
        const float margin = 30f, rowHeight = 26f, titleH = 34f;
        float panelW = Math.Min(560f, w - 2f * margin);
        float panelH = titleH + menu.Choices.Count * rowHeight + 26f;
        float x = (w - panelW) / 2f;
        float y = (h - panelH) / 2f;

        string title = string.Join(" > ", crumbTrail.SelectAll(m => m.Name.Length == 0 ? "(menu)" : m.Name));

        var fills = new List<ScreenFill>
        {
            new(new RectangleF(x, y, panelW, panelH), ChatPanel, "panel"),
            new(new RectangleF(x, y, panelW, 2f), ChatAccent, "accent.top"),
            new(new RectangleF(x, y, 2f, panelH), ChatAccent, "accent.left"),
        };

        var rows = new List<PanelTextRow>(menu.Choices.Count + 2);
        var tsz = Measure(font, title);
        rows.Add(new("", title, new Vector2(x + 14f, y + 8f), new RectangleF(x + 14f, y + 8f, tsz.X, tsz.Y),
            false, 1f, ChatAccent, ScreenInkRole.Title, false, false));
        var hsz = Measure(font, "KEY   VOICE COMMAND");
        // The column legend is port chrome (native quickchat.mdl has no such row); the id lets
        // a vector skin own it, because at the classic 8 px row offset its ink box overlaps the
        // first choice's (a pre-existing tightness the Jet chrome does not reproduce).
        rows.Add(new("quickchat.legend", "KEY   VOICE COMMAND", new Vector2(x + 14f, y + titleH),
            new RectangleF(x + 14f, y + titleH, hsz.X, hsz.Y), false, 1f, ChatTextDim, ScreenInkRole.Dim, false, false));

        for (int i = 0; i < menu.Choices.Count; i++)
        {
            var ch = menu.Choices[i];
            float ry = y + titleH + 8f + i * rowHeight;
            bool isSub = ch.Submenu is not null;
            string keyStr = ch.Key.ToString();
            string caption = isSub ? ch.Label.TrimEnd() + "..." : ch.Label;
            var color = isSub ? ChatAccent : ChatLeaf;
            var ksz = Measure(font, keyStr);
            rows.Add(new("quickchat.row", keyStr, new Vector2(x + 14f, ry),
                new RectangleF(x + 14f, ry, panelW - 28f, rowHeight), false, 1f,
                isSub ? ChatAccent : ChatTextDim, ScreenInkRole.Dim, false, false, 40f));
            string cap = ChatLogFormat.AsciiSafe(caption);
            var csz = Measure(font, cap);
            rows.Add(new("quickchat.row", cap, new Vector2(x + 58f, ry),
                new RectangleF(x + 58f, ry, MathF.Max(csz.X, ksz.X + 44f), csz.Y), false, 1f, color,
                isSub ? ScreenInkRole.Title : ScreenInkRole.Body, false, false, panelW - 72f));
        }

        string hint = crumbTrail.Count > 1 ? "ESC = back one level" : "ESC = close";
        var hintSz = Measure(font, hint);
        rows.Add(new("", hint, new Vector2(x + panelW - hintSz.X - 14f, y + panelH - 22f),
            new RectangleF(x + panelW - hintSz.X - 14f, y + panelH - 22f, hintSz.X, hintSz.Y),
            false, 1f, ChatTextDim, ScreenInkRole.Dim, false, false));

        return new ScreenPanelFrame(viewport, QuickChat, new RectangleF(x, y, panelW, panelH),
            fills, rows, Inks(ChatAccent, ChatTextMain, ChatTextDim));
    }

    // ── key bindings ─────────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="KeyBindingRenderer"/> (panel 72 % × 90 %, padding 18,
    /// row height 20, header at +8/+28, footer lines, selection band).</summary>
    public static ScreenPanelFrame KeyBindFrame(KeyBindingController ctl, Viewport viewport, SpriteFont small)
    {
        float w = viewport.Width, h = viewport.Height;
        const float padding = 18f;
        const float rowHeight = 20f;
        float panelW = w * 0.72f;
        float panelH = h * 0.90f;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        float innerX = panelX + padding;
        float innerTop = panelY + 52f;
        float innerBottom = panelY + panelH - 46f;

        var fills = new List<ScreenFill>();
        AddPanel(fills, panelX, panelY, panelW, panelH, BindPanel, BindBorder, 1f, 2f);

        var rows = new List<PanelTextRow>();

        string title = ctl.Phase switch
        {
            KeyBindPhase.Browse => "CONTROLS - pick a command, press Enter to rebind (Esc closes)",
            KeyBindPhase.Capture => $"REBIND {ctl.CapturingAction} - press the new key (Esc cancels)",
            KeyBindPhase.Conflict => "CONFLICT - that key is taken",
            KeyBindPhase.ConfirmReset => "RESET KEY BINDINGS",
            _ => "CONTROLS",
        };
        var hs = Measure(small, title);
        rows.Add(new("", title, new Vector2((w - hs.X) / 2f, panelY + 8f),
            new RectangleF((w - hs.X) / 2f, panelY + 8f, hs.X, hs.Y), false, 1f, BindHeader,
            ScreenInkRole.Title, false, false));
        string preset = "PRESET: " + ctl.LayoutName.ToUpperInvariant();
        var ps = Measure(small, preset);
        rows.Add(new("", preset, new Vector2((w - ps.X) / 2f, panelY + 28f),
            new RectangleF((w - ps.X) / 2f, panelY + 28f, ps.X, ps.Y), false, 1f, BindHeader,
            ScreenInkRole.Title, false, false));

        var list = new List<(bool Header, string? Action)>();
        string? lastCategory = null;
        var actions = ctl.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            string action = actions[i];
            string cat = ctl.DefinitionOf(action).Category;
            if (cat != lastCategory) { list.Add((true, cat)); lastCategory = cat; }
            list.Add((false, action));
        }
        int selRow = -1;
        for (int i = 0; i < list.Count; i++)
            if (!list[i].Header && list[i].Action == ctl.CurrentAction) { selRow = i; break; }

        int visRows = Math.Max(1, (int)MathF.Floor((innerBottom - innerTop) / rowHeight));
        int firstRow = Math.Max(0, selRow - visRows / 2);
        for (int i = firstRow; i < Math.Min(firstRow + visRows, list.Count); i++)
        {
            float ry = innerTop + (i - firstRow) * rowHeight;
            if (list[i].Header)
            {
                string cat = list[i].Action!;
                var csz = Measure(small, cat);
                rows.Add(new("", cat, new Vector2(innerX, ry), new RectangleF(innerX, ry, csz.X, csz.Y),
                    false, 1f, BindGroup, ScreenInkRole.Title, false, false));
                continue;
            }
            string action = list[i].Action!;
            string labelText = action.Replace("TK_", "");
            if (i == selRow)
                fills.Add(new(new RectangleF(innerX - 4f, ry, panelW - 2f * padding + 8f, rowHeight - 2f),
                    BindSel, "selection"));
            var asz = Measure(small, labelText);
            // The control band is the COMMAND column only (330 px): its corner brackets must
            // not reach the key/description columns of the same line.
            rows.Add(new("keybind.row", labelText, new Vector2(innerX, ry),
                new RectangleF(innerX, ry, 316f, rowHeight), false, 1f, BindDesc,
                ScreenInkRole.Body, i == selRow, true, 316f));
            string label = ctl.Map.LabelOf(action);
            var kl = Measure(small, label);
            rows.Add(new("", label, new Vector2(innerX + 330f, ry),
                new RectangleF(innerX + 330f, ry, kl.X, kl.Y), false, 1f, BindKey,
                ScreenInkRole.Highlight, false, false, 120f));
            string desc = ctl.DefinitionOf(action).Description;
            var dsz = Measure(small, desc) * 0.82f;
            rows.Add(new("", desc, new Vector2(innerX + 330f + kl.X + 16f, ry),
                new RectangleF(innerX + 330f + kl.X + 16f, ry, dsz.X, dsz.Y), false, 0.82f, BindNote,
                ScreenInkRole.Dim, false, false, panelX + panelW - padding - (innerX + 330f + kl.X + 16f)));
        }

        string msg = ctl.Message;
        float my = panelY + panelH - 20f;
        var msz = Measure(small, msg);
        bool warn = ctl.Phase is KeyBindPhase.Conflict or KeyBindPhase.ConfirmReset;
        rows.Add(new("", msg, new Vector2((w - msz.X) / 2f, my),
            new RectangleF((w - msz.X) / 2f, my, msz.X, msz.Y), false, 1f, warn ? BindWarn : BindNote,
            warn ? ScreenInkRole.Warning : ScreenInkRole.Dim, false, false));
        string hint = "Up/Down = move   Enter = pick   P = layout preset   R = reset to defaults   Space = OK (save)   Esc = cancel (discard)";
        // The classic footer centres the run on its UNSCALED width and then draws it at 0.9.
        var hz = Measure(small, hint);
        rows.Add(new("", hint, new Vector2((w - hz.X) / 2f, panelY + panelH - 40f),
            new RectangleF((w - hz.X) / 2f, panelY + panelH - 40f, hz.X * 0.9f, hz.Y * 0.9f), false, 0.9f,
            BindNote, ScreenInkRole.Dim, false, false));

        return new ScreenPanelFrame(viewport, KeyBind, new RectangleF(panelX, panelY, panelW, panelH),
            fills, rows, Inks(BindHeader, BindDesc, BindNote));
    }

    // ── connection lost ──────────────────────────────────────────────────────
    /// <summary>Mirrors <see cref="ConnectionLostScreenRenderer"/> (panel 88 %/62 % capped at
    /// 1000×440, 2 px outline, 4 px top edge, divider, footer).</summary>
    public static ScreenPanelFrame ConnectionLostFrame(Viewport viewport, SpriteFont small, SpriteFont big)
    {
        float w = viewport.Width, h = viewport.Height;
        float panelW = MathF.Min(w * 0.88f, 1000f);
        float panelH = MathF.Min(h * 0.62f, 440f);
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float pad = 34f;

        string title = "CONNECTION LOST";
        var tsz = Measure(big, title);
        float titleScale = MathF.Min(1f, (panelW - 2f * pad) / tsz.X);
        float titleX = panelX + (panelW - tsz.X * titleScale) / 2f;

        float cursorY = panelY + 26f;
        float bigRow = Measure(big, "Mg").Y * titleScale;

        var fills = new List<ScreenFill>();
        AddPanel(fills, panelX, panelY, panelW, panelH, LostPanel, LostBorder, 2f, 4f, LostTop);

        var rows = new List<PanelTextRow>
        {
            new("connectionlost.title", title, new Vector2(titleX, cursorY),
                new RectangleF(titleX, cursorY, tsz.X * titleScale, bigRow), true, titleScale,
                LostTitle, ScreenInkRole.Warning, false, false),
        };
        cursorY += bigRow + 26f;

        string[] body =
        [
            "The connection to the game server was lost.",
            "The world has stopped updating and your",
            "controls are disabled.",
            "",
            "If this was not expected, the server may have",
            "crashed or your network connection dropped.",
        ];
        foreach (string line in body)
        {
            // The classic body centres on the UNSCALED width and then draws at 1.25.
            var wsz = Measure(small, line);
            rows.Add(new("", line, new Vector2(panelX + (panelW - wsz.X) / 2f, cursorY),
                new RectangleF(panelX + (panelW - wsz.X) / 2f, cursorY, wsz.X * 1.25f, wsz.Y * 1.25f),
                false, 1.25f, LostBody, ScreenInkRole.Body, false, false));
            cursorY += Measure(small, "Mg").Y * 1.25f + 8f;
        }

        fills.Add(new(new RectangleF(panelX + pad, cursorY + 4f, panelW - 2f * pad, 1f),
            new Color(LostBorder.R, LostBorder.G, LostBorder.B, 0.55f), "divider"));

        string foot = "-- PRESS ESC TO QUIT --";
        var fsz = Measure(small, foot);
        rows.Add(new("connectionlost.footer", foot,
            new Vector2(panelX + (panelW - fsz.X) / 2f, panelY + panelH - 28f),
            new RectangleF(panelX + (panelW - fsz.X) / 2f, panelY + panelH - 28f, fsz.X, fsz.Y),
            false, 1f, LostSub, ScreenInkRole.Dim, false, false));

        return new ScreenPanelFrame(viewport, ConnectionLost, new RectangleF(panelX, panelY, panelW, panelH),
            fills, rows, Inks(LostTitle, LostBody, LostSub));
    }
}

/// <summary>P1042c — small LINQ-free helpers so the builders never allocate an enumerator
/// through the interface (the frames are built every frame on the production path).</summary>
internal static class PanelFrameEnumerable
{
    public static string[] SelectAll(this string[] items, Func<string, string> select)
    {
        var result = new string[items.Length];
        for (int i = 0; i < items.Length; i++) result[i] = select(items[i]);
        return result;
    }

    public static List<string> SelectAll<T>(this IReadOnlyList<T> items, Func<T, string> select)
    {
        var result = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++) result.Add(select(items[i]));
        return result;
    }
}
