using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering.Screens;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1042 (P972 slice 3F, first half) — the Jet screen chrome for the intro menu and the
/// hangar/loadout screens. Thin-vector frames, plates and keylined text in the Jet
/// palette around the SAME actions and the SAME rectangles:
///
/// <list type="bullet">
/// <item>intro: the 13 native commands at <see cref="IntroLayout"/>'s row rectangles, the
/// pilot field, the identity line and the same hint. Native text only, same keys, same
/// selection (<c>IntroScreenModel</c> owns it), same focus.</item>
/// <item>hangar: the native <c>hangar.mdl</c> artwork and the launch animation are CONTENT
/// and are drawn unchanged by the shared <see cref="HangarScreenRenderer"/>; Jet adds
/// corner-bracket frames at the pane rectangles <see cref="HangarScreen.Rect"/> gives the
/// hit test, a plate+keyline for the port-only Buy/Default verbs, and the native hover
/// string in Jet ink. Buy/Default/ACK/launch/hover/badge/animation semantics are
/// untouched.</item>
/// <item>loadout: the P1013 V2 composition keeps every leaf rectangle (draw == hit); Jet
/// re-inks its palette and frames the regions/action buttons. The legacy docked shop is
/// re-inked only. Transactions stay in <see cref="LoadoutBuyFlow"/>.</item>
/// </list>
///
/// <para>The palette is read from the published <see cref="JetPaletteSwitcher"/> variant
/// with one reference read per frame, so a palette switch at the update boundary is
/// never a half-switched screen. Both painters are built at construction (no per-frame
/// allocation, no JSON, no resource work).</para>
/// </summary>
public sealed class JetScreenSkinRenderer : IScreenSkinRenderer
{
    private readonly IJetVectorSurface _surface;
    private readonly HangarScreenRenderer? _hangar;
    private readonly DockedScreenRenderer? _docked;
    private readonly LoadoutV2Renderer? _loadoutV2;
    private readonly JetPaletteSwitcher? _palettes;
    private readonly JetVectorPainter _green;
    private readonly JetVectorPainter _amber;
    /// <summary>P1042b — the borrowed career content painter (the P808c renderer needs
    /// the SpriteBatch/fonts; Jet supplies its palette and frames the tabs/panel).</summary>
    private readonly IScreenContentRenderer? _careerContent;
    /// <summary>P1042c — the borrowed in-game content renderers (leaderboard, starmap, sector
    /// info, game-over tables). Jet supplies their palettes and frames their rectangles.</summary>
    private readonly ScreenRenderers? _extra;

    /// <summary>P1042c — the minimum clear gap the Jet career title keeps above the tab row.</summary>
    private const float CareerScreenTitleGap = 6f;
    /// <summary>P1042c — a panel control row's corner-bracket arm (short enough that the text
    /// indent below keeps the run clear of it).</summary>
    private const float PanelRowBracketArm = 5f;
    /// <summary>P1042c — the indent of a panel control row's run past its band's left edge.</summary>
    private const float PanelRowTextIndent = 8f;

    private const float PlateAlpha = JetScreenChrome.PlateAlpha;
    private const float DimPlateAlpha = JetScreenChrome.DimPlateAlpha;
    private const float BracketArm = JetScreenChrome.BracketArm;

    /// <summary>The palette a test/proof pins without a switcher (null = the published one).</summary>
    private readonly string? _paletteOverride;

    public JetScreenSkinRenderer(IJetVectorSurface surface, HangarScreenRenderer? hangar,
        DockedScreenRenderer? docked, LoadoutV2Renderer? loadoutV2, HudSettings settings,
        JetPaletteSwitcher? palettes, string? paletteOverride = null,
        IScreenContentRenderer? careerContent = null, ScreenRenderers? extra = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(settings);
        _surface = surface;
        _hangar = hangar;
        _docked = docked;
        _loadoutV2 = loadoutV2;
        _palettes = palettes;
        _paletteOverride = paletteOverride;
        _careerContent = careerContent;
        _extra = extra;
        var catalog = HudSkinResolver.Catalog;
        var green = catalog.TryGet(HudSkinIds.Jet) ?? throw new ArgumentException("The Jet skin definition is not installed.");
        var amber = catalog.TryGetVariant(HudSkinIds.Jet, JetPaletteIds.Amber) ?? green;
        _green = PainterFor(green, surface);
        _amber = PainterFor(amber, surface);
    }

    public string Id => HudSkinIds.Jet;

    public bool HangarArtAvailable => _hangar?.CanDrawNative == true;

    private JetVectorPainter Painter =>
        _paletteOverride == JetPaletteIds.Amber
        || (_paletteOverride is null && _palettes is not null && _palettes.Active.Id == JetPaletteIds.Amber)
            ? _amber : _green;

    /// <summary>Optional audit channel: the proof's recording surface.</summary>
    private IScreenRectAudit? Audit => _surface as IScreenRectAudit;

    private static JetVectorPainter PainterFor(HudSkinDefinition definition, IJetVectorSurface surface)
    {
        var palette = definition.Palette;
        return new JetVectorPainter(surface,
            Parse(palette?.Primary, new Color(0x79, 0xFF, 0xC0)),
            Parse(palette?.Dim, new Color(0x3D, 0xA8, 0x7B)),
            Parse(palette?.Hot, new Color(0xC9, 0xFF, 0xE3)),
            Parse(palette?.Plate, new Color(0x06, 0x11, 0x0C)),
            Parse(palette?.Keyline, new Color(0x08, 0x0B, 0x09)));
    }

    private static Color Parse(string? hex, Color fallback)
        => HudSkinColor.TryParseHex(hex, out var color) ? new Color(color.R, color.G, color.B) : fallback;

    // ── Intro ──────────────────────────────────────────────────────────────
    /// <summary>Drawn inside the caller's open SpriteBatch session.</summary>
    public void DrawIntro(in IntroMainFrame frame)
    {
        var painter = Painter;
        float w = frame.Width;
        // Title/subtitle: keylined, same anchors as the classic title.
        var t = painter.Measure(frame.Title, true, 1f);
        painter.Element(ScreenActions.IntroTitle);
        DrawCentered(painter, frame.Title, w, 28f, large: true, FitWidth(t, w, 1f),
            painter.Primary);
        painter.Drawn(ScreenActions.IntroTitle);
        var subtitleSize = frame.Subtitle.Length > 0 ? painter.Measure(frame.Subtitle, false, 1f) : Vector2.Zero;
        if (frame.Subtitle.Length > 0)
        {
            painter.Element(ScreenActions.IntroSubtitle);
            DrawCentered(painter, frame.Subtitle, w, 82f, large: false, FitWidth(subtitleSize, w, 1f),
                painter.Dim);
            painter.Drawn(ScreenActions.IntroSubtitle);
        }

        for (int i = 0; i < frame.Labels.Count; i++)
        {
            var row = frame.Layout.Rows[i];
            bool sel = i == frame.Selected;
            string element = ScreenActions.IntroElement(i);
            string label = frame.Labels[i];
            var box = new RectangleF(row.X, row.Y, row.Width, row.Height);
            painter.Element(element);
            painter.FillPlate(box, sel ? PlateAlpha : DimPlateAlpha);
            painter.CornerBrackets(box, BracketArm, sel ? painter.Stroke : painter.Hairline,
                sel ? painter.Hot : painter.Dim);
            float scale = FitInside(label, box, frame.Layout.RowTextScale, painter);
            painter.Text(label, new Vector2(box.X + box.Width / 2f, box.Y + box.Height / 2f),
                false, scale, sel ? painter.Hot : painter.Primary, centered: true);
            painter.Drawn(element);
            Audit?.Control(ScreenActions.Intro, ScreenActions.IntroRow(i), box);
            Audit?.Plate(ScreenActions.Intro, ScreenActions.IntroRow(i), box);
        }

        // Pilot field: the same resolved box as the classic highlight.
        var field = frame.PilotField;
        var fieldBox = new RectangleF(field.X, field.Y, field.Width, field.Height);
        painter.FillPlate(fieldBox, PlateAlpha);
        painter.CornerBrackets(fieldBox, BracketArm, frame.PilotFocused ? painter.Stroke : painter.Hairline,
            frame.PilotFocused ? painter.Hot : painter.Dim);
        Audit?.Plate(ScreenActions.Intro, ScreenActions.IntroPilot, fieldBox);
        var nameSize = painter.Measure(frame.PilotLabel, false, 1f);
        painter.Element(ScreenActions.IntroPilot);
        painter.Text(frame.PilotLabel,
            new Vector2(w / 2f, field.CenterY), false, 1f,
            frame.PilotFocused ? painter.Hot : painter.Dim, centered: true);
        painter.Drawn(ScreenActions.IntroPilot);
        if (frame.PilotFocused)
        {
            // The caret follows the typed characters exactly where the classic field puts
            // it: after "Pilot: " + the typed text, inside the field box.
            float caretX = w / 2f - nameSize.X / 2f + frame.PilotCaretOffset;
            painter.Surface.Fill(new Vector2(caretX, field.Y + 3f), new Vector2(2f, field.Height - 6f), painter.Hot);
        }

        // Identity status line and the one-line key hint: the same strings/anchors.
        // P1042 — the status line keeps the layout's anchor but is fitted inside the
        // band it really owns: on the compressed 800×480 column the classic anchor sits
        // close under the subtitle and just above the first row plate, so the chrome
        // shrinks the line instead of letting it touch either.
        var identityInk = painter.Measure(frame.IdentityLine, false, 1f);
        float identityTop = frame.Layout.IdentityY;
        if (frame.Subtitle.Length > 0)
            identityTop = MathF.Max(identityTop, 82f + subtitleSize.Y + 2f);
        float identityBand = (frame.Layout.Rows.Length > 0 && identityTop < frame.Layout.Rows[0].Y
                ? frame.Layout.Rows[0].Y : frame.Layout.PilotY) - 2f - identityTop;
        float identityScale = FitWidth(identityInk, w, frame.IdentityScale);
        if (identityInk.Y > 0f && identityBand > 0f)
            identityScale = MathF.Min(identityScale, identityBand / identityInk.Y);
        painter.Element(ScreenActions.IntroIdentity);
        DrawCentered(painter, frame.IdentityLine, w, identityTop, large: false, identityScale, painter.Dim);
        painter.Drawn(ScreenActions.IntroIdentity);

        var hintInk = painter.Measure(frame.Hint, false, 1f);
        painter.Element(ScreenActions.IntroHint);
        DrawCentered(painter, frame.Hint, w, frame.Layout.FooterY, large: false,
            FitWidth(hintInk, w, frame.HintScale), painter.Dim);
        painter.Drawn(ScreenActions.IntroHint);
    }

    // ── Intro sub-views ────────────────────────────────────────────────────
    /// <summary>
    /// P1042b — Jet chrome for the pre-game sub-views: title/subtitle keylined at the classic
    /// anchors, every selectable row framed and plated at its own <see cref="ScreenRow.Rect"/>
    /// (the rectangle the drawing code resolved), the selection marked by shape (full stroke +
    /// brackets + hot ink), every other run keylined in the five-role palette. Native strings
    /// and anchors only; no command, key or focus rule is reachable from the frame.
    /// </summary>
    public void DrawIntroView(in IntroViewFrame frame)
    {
        var painter = Painter;
        float w = frame.Width, h = frame.Height;
        // Drawn inside the caller's open SpriteBatch session (like DrawIntro).

        if (frame.Title.Length > 0)
        {
            var t = painter.Measure(frame.Title, true, 1f);
            painter.Element(ScreenActions.ViewTitle);
            DrawCentered(painter, frame.Title, w, 28f, large: true, FitWidth(t, w, 1f), painter.Primary);
            painter.Drawn(ScreenActions.ViewTitle);
        }
        if (frame.Subtitle.Length > 0)
        {
            var s = painter.Measure(frame.Subtitle, false, 1f);
            painter.Element(ScreenActions.ViewSubtitle);
            DrawCentered(painter, frame.Subtitle, w, 82f, large: false, FitWidth(s, w, 1f),
                painter.Dim);
            painter.Drawn(ScreenActions.ViewSubtitle);
        }

        // Selection/focus plates that are NOT a row's own band (a focused text field).
        for (int i = 0; i < frame.Plates.Count; i++)
        {
            var plate = frame.Plates[i];
            bool rowOwns = false;
            for (int r = 0; r < frame.Rows.Count; r++)
                if (SameRect(frame.Rows[r].Rect, plate.Rect)) { rowOwns = true; break; }
            if (rowOwns) continue;
            // A focused text field: the plate + its keyline only (a corner bracket would
            // reach into the first glyph — the field keeps the classic 6 px text inset).
            var box = new RectangleF(plate.Rect.X, plate.Rect.Y, plate.Rect.Width, plate.Rect.Height);
            painter.FillPlate(box, plate.Role == ScreenPlateRole.Selection ? PlateAlpha : DimPlateAlpha);
        }

        // Rows: the band is the frame's resolved rectangle (draw == hit).
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            var row = frame.Rows[i];
            var box = new RectangleF(row.Rect.X, row.Rect.Y, row.Rect.Width, row.Rect.Height);
            if (box.Width <= 0f || box.Height <= 0f) continue;
            bool sel = row.Selected;
            string id = ScreenActions.ViewRow(i);
            painter.Element(id);
            painter.FillPlate(box, sel ? PlateAlpha : DimPlateAlpha);
            painter.CornerBrackets(box, BracketArm, sel ? painter.Stroke : painter.Hairline,
                sel ? painter.Hot : painter.Dim);
            float scale = FitInside(row.Label, box, MathF.Min(1f, row.Scale), painter);
            painter.Text(row.Label, new Vector2(box.X + box.Width / 2f, box.Y + box.Height / 2f),
                false, scale, sel ? painter.Hot : JetInk(painter, row.Role), centered: true);
            painter.Drawn(id);
            Audit?.Control(ScreenActions.ViewBody, id, box);
            Audit?.Plate(ScreenActions.ViewBody, id, box);
        }

        // Non-selectable runs keep their classic anchors and strings, keylined.
        for (int i = 0; i < frame.Lines.Count; i++)
        {
            var line = frame.Lines[i];
            if (line.Text.Length == 0) continue;
            var ink = painter.Measure(line.Text, false, 1f);
            float available = MathF.Max(1f, w - line.Position.X - 20f);
            float scale = ink.X > 0f ? MathF.Min(line.Scale, available / ink.X) : line.Scale;
            painter.Element(ScreenActions.ViewBody);
            painter.Text(line.Text, line.Position, false, scale, JetInk(painter, line.Role));
            painter.Drawn(ScreenActions.ViewBody);
        }
    }

    /// <summary>P1042b — career/squads/events: the unchanged P808c composition re-inked
    /// through the Jet palette, then the five mode tabs and the table panel framed at the
    /// rectangles <see cref="CareerScreenRenderer.TabRect"/>/<see cref="CareerScreenRenderer.PanelRect"/>
    /// resolve (the same source the tabs are drawn from).</summary>
    public void DrawCareer(in CareerScreenFrame frame)
    {
        var painter = Painter;
        // P1042c — the content composition skips its title; Jet draws it fitted into the
        // band above the tab row (item 0b: the classic big title touches the tab row in Jet).
        _careerContent?.DrawCareer(frame, JetCareerPalette(painter), drawTitle: false);
        string title = CareerScreenRenderer.TitleFor(frame.BoardMode, frame.SquadsMode, frame.EventsMode);
        var titleInk = painter.Measure(title, true, 1f);
        float band = CareerScreenRenderer.TabRowTop - CareerScreenTitleGap - 1f - CareerScreenRenderer.TitleTop;
        float titleScale = titleInk.Y > 0f && band > 0f
            ? MathF.Min(1f, MathF.Min(band / titleInk.Y, (frame.Width - 40f) / MathF.Max(1f, titleInk.X)))
            : 1f;
        painter.Element(ScreenActions.CareerTitle);
        painter.Text(title, new Vector2(frame.Width / 2f,
            CareerScreenRenderer.TitleTop + titleInk.Y * titleScale / 2f), true, titleScale,
            painter.Primary, centered: true);
        painter.Drawn(ScreenActions.CareerTitle);
        var panel = CareerScreenRenderer.PanelRect(frame.Width, frame.Height);
        painter.Element(ScreenActions.CareerPanel);
        painter.CornerBrackets(new RectangleF(panel.X, panel.Y, panel.Width, panel.Height),
            BracketArm, painter.Hairline, painter.Dim);
        painter.Drawn(ScreenActions.CareerPanel);
        // The tabs use the renderer's own measured rectangles: one rectangle for the drawn
        // tab and the chrome that frames it.
        for (int i = 0; i < CareerScreenRenderer.TabLabels.Length; i++)
        {
            var rect = CareerScreenRenderer.TabRect(
                painter.Measure(CareerScreenRenderer.TabLabels[i], false, 1f), i, frame.Width);
            var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            string id = ScreenActions.CareerTab(i);
            painter.Element(id);
            painter.CornerBrackets(box, 6f, i == 0 ? painter.Stroke : painter.Hairline,
                i == 0 ? painter.Hot : painter.Primary);
            painter.Drawn(id);
            Audit?.Control(ScreenActions.Career, id, box);
        }
    }

    /// <summary>The Jet palette mapped onto the career screen's inks.</summary>
    private static CareerScreenPalette JetCareerPalette(JetVectorPainter painter) => new(
        painter.Primary, painter.Primary, painter.Dim, painter.Plate * 0.92f,
        painter.Plate * 0.75f, painter.Hot);

    // ── P1042c: the in-game panel families ──────────────────────────────────
    /// <summary>
    /// P1042c — Jet chrome for the panel-style in-game screens (side select, ready check,
    /// mission briefing, team pane, quick chat, key bindings, connection lost). The frame is
    /// the model's: every fill is the pre-boundary fill and every run sits at its resolved
    /// anchor. Jet never repaints a fill; it draws corner-bracket frames at the panel and the
    /// control bands, plates the selectable rows, and re-inks every run with keylined text in
    /// the five-role palette. Selection carries shape (full stroke bracket + denser plate) AND
    /// ink (Hot), never hue alone.
    /// </summary>
    public void DrawPanel(in ScreenPanelFrame frame)
    {
        var painter = Painter;
        float w = frame.Width, h = frame.Height;
        var panel = frame.Panel;
        _surface.Begin();
        try
        {
            // The screen frame: corner brackets OUTSIDE the panel box (a 3 px outset, short
            // arms), so a bracket arm can never cross the panel's own title/row text.
            var outer = new RectangleF(panel.X - 3f, panel.Y - 3f, panel.Width + 6f, panel.Height + 6f);
            painter.CornerBrackets(outer, MathF.Min(BracketArm, MathF.Min(outer.Width, outer.Height) * 0.2f),
                painter.Hairline, painter.Dim);
            painter.Line(new Vector2(outer.X, outer.Y), new Vector2(outer.X + outer.Width, outer.Y),
                painter.Hairline, painter.Dim);

            string family = frame.Family;
            bool panelFamily = family == ScreenActions.PanelSideSelect
                || family == ScreenActions.PanelReadyCheck
                || family == ScreenActions.PanelQuickChat
                || family == ScreenActions.PanelKeyBind;
            int controlIndex = 0;
            var rows = frame.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Text.Length == 0) continue;
                // The quick-chat column legend is chrome the vector skin owns (its classic
                // anchor overlaps the first choice's ink at every resolution).
                if (row.Id == ScreenActions.QuickChatLegend) continue;
                // A run the classic screen places entirely outside the viewport is invisible
                // there; the vector chrome does not draw it either (audit: text-off-screen 0).
                var runInk = painter.Measure(row.Text, row.Large, row.Scale);
                if (row.Position.Y + runInk.Y < 0f || row.Position.Y > h) continue;
                var ink = row.Selected ? painter.Hot : JetInk(painter, row.Role);
                if (row.Control && panelFamily)
                {
                    painter.Element(ScreenActions.PanelLine);
                    // A selectable row: plate + brackets at exactly the frame's band.
                    var box = row.Rect;
                    if (box.Width <= 0f || box.Height <= 0f) { controlIndex++; continue; }
                    string id = ScreenActions.PanelRow(family, controlIndex);
                    painter.Element(id);
                    painter.FillPlate(box, row.Selected ? PlateAlpha : DimPlateAlpha);
                    painter.CornerBrackets(box, PanelRowBracketArm,
                        row.Selected ? painter.Stroke : painter.Hairline, ink);
                    // Left-aligned and indented past the corner-bracket arm, vertically centred
                    // in the band, fitted to the run's own column width and INCLUDING the
                    // keyline under-stroke the painter adds: the run stays strictly inside its
                    // plate, clear of the brackets, and never reaches the next column.
                    var rowInk = painter.Measure(row.Text, row.Large, 1f);
                    float widthRoom = MathF.Max(1f, (row.MaxWidth > 0f ? row.MaxWidth : box.Width)
                        - PanelRowTextIndent - 2f * (JetScreenChrome.Band(painter) + painter.KeylineWidth));
                    float heightRoom = MathF.Max(1f, box.Height
                        - 2f * (JetScreenChrome.Band(painter) + painter.KeylineWidth));
                    float scale = MathF.Min(row.Scale, MathF.Min(
                        rowInk.X > 0f ? widthRoom / rowInk.X : row.Scale,
                        rowInk.Y > 0f ? heightRoom / rowInk.Y : row.Scale));
                    scale = MathF.Max(JetScreenChrome.MinimumTextScale, scale);
                    painter.Text(row.Text,
                        new Vector2(box.X + PanelRowTextIndent, box.Y + (box.Height - rowInk.Y * scale) / 2f),
                        row.Large, scale, ink);
                    painter.Drawn(id);
                    Audit?.Control(family, ScreenActions.PanelRow(family, controlIndex), box);
                    Audit?.Plate(family, ScreenActions.PanelRow(family, controlIndex), box);
                    controlIndex++;
                    continue;
                }

                // A non-selectable run: the model's anchor, wide enough for its own line.
                painter.Element(ScreenActions.PanelLine);
                float available = row.MaxWidth > 0f
                    ? row.MaxWidth
                    : MathF.Max(1f, w - 16f - MathF.Max(0f, row.Position.X));
                var inkSize = painter.Measure(row.Text, row.Large, 1f);
                float runScale = inkSize.X > 0f ? MathF.Min(row.Scale, available / inkSize.X) : row.Scale;
                float runH = inkSize.Y * runScale;
                if (row.Position.Y + runH < 0f || row.Position.Y > h) continue;
                // A run the classic screen centres wider than the window (the key-binding
                // footer at 800x480) is re-centred at a fitted scale so it stays on screen.
                float runW = inkSize.X * runScale;
                float runX = row.Position.X;
                if (runX < 0f || runX + runW > w - 8f) runX = MathF.Max(8f, (w - runW) / 2f);
                painter.Text(row.Text, new Vector2(runX, row.Position.Y), row.Large, runScale, ink);
            }
        }
        finally { _surface.End(); }
    }

    /// <summary>
    /// P1042c — the in-mission leaderboard: the P639 roster cells are CONTENT and are drawn
    /// unchanged except for the Jet ink palette; Jet frames the panel, the header band and
    /// every row band the renderer's own geometry resolved.
    /// </summary>
    public void DrawScoreboard(in ScoreboardFrame frame)
    {
        var painter = Painter;
        _extra?.Scoreboard?.Draw(frame.Viewport, frame.Rows, frame.SelfName, JetScoreboardPalette(painter));
        _surface.Begin();
        try
        {
            painter.CornerBrackets(frame.Panel,
                MathF.Min(BracketArm * 2f, MathF.Min(frame.Panel.Width, frame.Panel.Height) * 0.2f),
                painter.Hairline, painter.Dim);
            var header = frame.HeaderBand;
            if (header.Width > 0f && header.Height > 0f)
            {
                painter.Element(ScreenActions.ScoreboardHeader);
                painter.CornerBrackets(header, BracketArm, painter.Hairline, painter.Primary);
                painter.Drawn(ScreenActions.ScoreboardHeader);
                Audit?.Control(ScreenActions.Scoreboard, ScreenActions.ScoreboardHeader, header);
            }
            var bands = frame.RowBands;
            for (int i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                if (band.Width <= 0f || band.Height <= 0f) continue;
                string id = ScreenActions.ScoreboardRow(i);
                painter.Element(id);
                painter.CornerBrackets(band, BracketArm, painter.Hairline, painter.Dim);
                painter.Drawn(id);
                Audit?.Control(ScreenActions.Scoreboard, id, band);
            }
        }
        finally { _surface.End(); }
    }

    /// <summary>
    /// P1042c — the starmap: the map itself (sectors, alephs, icons, warp edges) and the info
    /// pane's text are native CONTENT drawn unchanged; Jet frames the map panel, the sector
    /// info pane and its expand button at the rectangles the frame resolved.
    /// </summary>
    public void DrawStarmap(in StarmapScreenFrame frame)
    {
        _extra?.Starmap?.Draw(frame.Sites, frame.Edges, frame.CurrentSector, frame.SelectedSector,
            frame.Warnings, frame.FlashOn, frame.Viewport, frame.PanelW, frame.PanelH,
            frame.Owners, frame.SideColor, frame.KnownEnemySectors, frame.HighlightedClusters,
            frame.ChatCluster);
        if (frame.InfoPane is { } info)
            _extra?.SectorInfo?.Draw(info, frame.Viewport,
                frame.Panel.X, frame.Panel.Y, frame.Panel.Width, frame.Panel.Height,
                frame.OwnSideColor, frame.Expanded);

        var painter = Painter;
        _surface.Begin();
        try
        {
            painter.CornerBrackets(frame.Panel,
                MathF.Min(BracketArm * 2f, MathF.Min(frame.Panel.Width, frame.Panel.Height) * 0.08f),
                painter.Hairline, painter.Dim);
            var infoBox = frame.InfoBox;
            if (infoBox.Width > 0f && infoBox.Height > 0f)
            {
                painter.Element(ScreenActions.StarmapInfo);
                painter.CornerBrackets(infoBox, BracketArm, painter.Hairline, painter.Primary);
                painter.Drawn(ScreenActions.StarmapInfo);
                Audit?.Control(ScreenActions.Starmap, ScreenActions.StarmapInfo, infoBox);
            }
            var expand = frame.InfoExpandButton;
            if (expand.Width > 0f && expand.Height > 0f)
            {
                painter.Element(ScreenActions.StarmapExpand);
                painter.CornerBrackets(expand, 4f, painter.Stroke, painter.Hot);
                painter.Drawn(ScreenActions.StarmapExpand);
                Audit?.Control(ScreenActions.Starmap, ScreenActions.StarmapExpand, expand);
            }
        }
        finally { _surface.End(); }
    }

#if !BROWSER
    /// <summary>
    /// P1042c — the game-over result screen: the two tables (and the winner/reason panel text)
    /// are CONTENT drawn by the unchanged P752 renderer; Jet frames the panel and the two
    /// table header bands at the rectangles the renderer reports (the ones its pointer uses).
    /// </summary>
    public void DrawGameOver(in GameOverScreenFrame frame)
    {
        if (_extra?.GameOver is not { } gameOver) return;
        gameOver.Draw(frame.Viewport, frame.Info, frame.Tables, frame.NextMatchSeconds, frame.Perf);

        var painter = Painter;
        _surface.Begin();
        try
        {
            painter.CornerBrackets(frame.Panel,
                MathF.Min(BracketArm * 2f, MathF.Min(frame.Panel.Width, frame.Panel.Height) * 0.1f),
                painter.Hairline, painter.Dim);
            var side = gameOver.LastSideHeaderRects;
            for (int i = 0; i < side.Count; i++)
            {
                var rect = side[i].Rect;
                var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
                if (box.Width <= 0f || box.Height <= 0f) continue;
                string id = ScreenActions.GameOverSideHeader(i);
                painter.Element(id);
                painter.CornerBrackets(box, 4f, painter.Hairline, painter.Primary);
                painter.Drawn(id);
                Audit?.Control(ScreenActions.GameOver, id, box);
            }
            var players = gameOver.LastPlayerHeaderRects;
            for (int i = 0; i < players.Count; i++)
            {
                var rect = players[i].Rect;
                var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
                if (box.Width <= 0f || box.Height <= 0f) continue;
                string id = ScreenActions.GameOverPlayerHeader(i);
                painter.Element(id);
                painter.CornerBrackets(box, 4f, painter.Hairline, painter.Primary);
                painter.Drawn(id);
                Audit?.Control(ScreenActions.GameOver, id, box);
            }
        }
        finally { _surface.End(); }
    }

#endif

    /// <summary>P1042c — the Jet palette mapped onto the leaderboard's inks.</summary>
    private static ScoreboardPalette JetScoreboardPalette(JetVectorPainter painter) => new(
        painter.Plate * 0.92f, painter.Dim, painter.Primary, painter.Primary,
        painter.Primary, painter.Hot, painter.Dim);

    /// <summary>The Jet role a classic screen role takes.</summary>
    private static Color JetInk(JetVectorPainter painter, ScreenInkRole role) => role switch
    {
        ScreenInkRole.Dim => painter.Dim,
        ScreenInkRole.Warning => painter.Hot,
        ScreenInkRole.Highlight => painter.Hot,
        _ => painter.Primary,
    };

    private static bool SameRect(RectangleF a, RectangleF b) =>
        MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Y - b.Y) < 0.01f
        && MathF.Abs(a.Width - b.Width) < 0.01f && MathF.Abs(a.Height - b.Height) < 0.01f;

    // ── Hangar ─────────────────────────────────────────────────────────────
    public void DrawHangar(in HangarScreenFrame frame)
    {
        // Content first: the native 800x600 screen, artwork and launch animation are
        // unchanged (HangarScreenRenderer's own SpriteBatch session). P1042b — the
        // port-verb cells and the native hover string are suppressed there because Jet
        // paints plate + keylined text at those same rectangles below: exactly one label
        // is visible (the doubled Buy/Def defect).
        _hangar?.Draw(frame.Viewport, frame.Hover, frame.Launch, frame.BoardState, frame.Developments,
            drawPortChrome: false);

        var painter = Painter;
        _surface.Begin();
        // Indexed, not enumerated: HangarScreen.Controls is an IReadOnlyList, and a
        // foreach over the interface boxes an enumerator every frame (P1042 alloc gate).
        var controls = HangarScreen.Controls;
        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            if (control.Action == HangarAction.Invest && !frame.Developments) continue;
            var rect = HangarScreen.Rect(frame.Width, frame.Height, control);
            var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            bool hovered = frame.Hover == control.Action;
            string action = ScreenActions.HangarElement(control.Action);
            painter.Element(action);
            painter.CornerBrackets(box, MathF.Min(BracketArm * 1.6f, MathF.Min(box.Width, box.Height) * 0.3f),
                hovered ? painter.Stroke : painter.Hairline, hovered ? painter.Hot : painter.Dim);
            painter.Drawn(action);
            Audit?.Control(ScreenActions.Hangar, ScreenActions.HangarElement(control.Action), box);
        }

        // Port-only verbs (native's hangar has no Buy/Default pane): plate + keylined
        // label at the very rectangle the pointer resolves, so both stay reachable.
        DrawPortVerb(painter, frame, HangarAction.Buy, "Buy");
        DrawPortVerb(painter, frame, HangarAction.Default, "Def");

        // The native hover string keeps its text and anchor; Jet keylines it in palette ink.
        string hover = HangarControls.Feedback(frame.Hover);
        if (hover.Length > 0)
        {
            var pane = HangarScreen.HoverTextRect(frame.Width, frame.Height);
            var box = new RectangleF(pane.X, pane.Y, pane.Width, pane.Height);
            float scale = FitInside(hover, box, 1f, painter);
            painter.FillPlate(box, DimPlateAlpha);
            Audit?.Plate(ScreenActions.Hangar, ScreenActions.HangarHover, box);
            painter.Element(ScreenActions.HangarHover);
            painter.Text(hover, new Vector2(box.X + box.Width / 2f, box.Y + box.Height / 2f), false, scale,
                frame.Hover == HangarAction.None ? painter.Dim : painter.Primary, centered: true);
            painter.Drawn(ScreenActions.HangarHover);
        }
        _surface.End();
    }

    private void DrawPortVerb(JetVectorPainter painter, in HangarScreenFrame frame, HangarAction action, string label)
    {
        var rect = HangarScreen.PortVerbRect(frame.Width, frame.Height, action == HangarAction.Default);
        var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
        bool hovered = frame.Hover == action;
        string id = ScreenActions.HangarElement(action);
        painter.Element(id);
        painter.FillPlate(box, hovered ? PlateAlpha : DimPlateAlpha);
        painter.CornerBrackets(box, 5f, hovered ? painter.Stroke : painter.Hairline,
            hovered ? painter.Hot : painter.Dim);
        float scale = FitInside(label, box, 1f, painter);
        painter.Text(label, new Vector2(box.X + box.Width / 2f, box.Y + box.Height / 2f), false, scale,
            hovered ? painter.Hot : painter.Primary, centered: true);
        painter.Drawn(id);
        Audit?.Control(ScreenActions.Hangar, id, box);
        Audit?.Plate(ScreenActions.Hangar, id, box);
    }

    // ── Loadout ────────────────────────────────────────────────────────────
    public void DrawLoadout(in LoadoutScreenFrame frame)
    {
        if (frame.V2Layout is { } layout && frame.V2Frame is { } presentation)
        {
            _loadoutV2?.Draw(presentation, layout, frame.Style, frame.Hover, frame.Marker,
                JetLoadoutPalette(Painter));
            var painter = Painter;
            _surface.Begin();
            FrameRegion(painter, layout.Header);
            FrameRegion(painter, layout.HullList);
            FrameRegion(painter, layout.ShipPreview);
            FrameRegion(painter, layout.SlotList);
            FrameRegion(painter, layout.StatsBar);
            FrameRegion(painter, layout.PriceBox);
            FrameRegion(painter, layout.ButtonRow);
            foreach (var element in layout.Elements)
            {
                if (element.Kind is not (LoadoutV2Layout.Kind.Buy or LoadoutV2Layout.Kind.Launch
                    or LoadoutV2Layout.Kind.Default or LoadoutV2Layout.Kind.Revert
                    or LoadoutV2Layout.Kind.Back or LoadoutV2Layout.Kind.Undock)) continue;
                var box = new RectangleF(element.Bounds.X, element.Bounds.Y, element.Bounds.Width, element.Bounds.Height);
                bool hovered = frame.Hover == element;
                string id = ScreenActions.LoadoutElement(element.Kind);
                painter.Element(id);
                painter.CornerBrackets(box, 6f, hovered ? painter.Stroke : painter.Hairline,
                    hovered ? painter.Hot : painter.Primary);
                Audit?.Control(ScreenActions.Loadout, id, box);
            }
            _surface.End();
            return;
        }
        // Legacy native-layout docked shop: re-ink the panel/rows in the Jet palette.
        _docked?.Draw(frame.Viewport, frame.Rows, frame.DetailRows, frame.Money,
            frame.BackdropAlpha, frame.FooterHeight, frame.SplitDetailPanels,
            frame.CreditBox, frame.NativeMoneyPanel, JetDockedPalette(Painter));
    }

    /// <summary>P1042 — the Jet palette mapped onto the V2 renderer's row inks.</summary>
    private static LoadoutV2Palette JetLoadoutPalette(JetVectorPainter painter) => new(
        painter.Hot, painter.Dim, painter.Primary, painter.Dim, painter.Primary,
        painter.Plate, painter.Keyline);

    /// <summary>P1042 — the Jet palette mapped onto the legacy docked panel inks.</summary>
    private static DockedScreenPalette JetDockedPalette(JetVectorPainter painter) => new(
        painter.Plate, painter.Keyline, painter.Primary, painter.Primary, painter.Primary,
        painter.Primary, painter.Hot, painter.Dim, painter.Dim, painter.Primary, painter.Hot);

    /// <summary>A centred, keylined run that keeps <paramref name="scale"/> and starts at
    /// <paramref name="top"/> (the painter's anchor is the block centre).</summary>
    private static void DrawCentered(JetVectorPainter painter, string text, float width, float top,
        bool large, float scale, Color color)
        => painter.Text(text, new Vector2(width / 2f, top + painter.Measure(text, large, scale).Y / 2f),
            large, scale, color, centered: true);

    /// <summary>The scale that keeps a run inside the viewport's 20 px side gutter.</summary>
    private static float FitWidth(Vector2 ink, float width, float scale)
    {
        if (ink.X <= 0f) return scale;
        float available = MathF.Max(1f, width - 40f);
        return MathF.Min(scale, available / ink.X);
    }

    /// <summary>One thin-vector region frame (the loadout's chrome).</summary>
    private static void FrameRegion(JetVectorPainter painter, TouchRect region)
        => painter.CornerBrackets(new RectangleF(region.X, region.Y, region.Width, region.Height),
            BracketArm, painter.Hairline, painter.Dim);

    /// <summary>Text scale that keeps <paramref name="text"/> inside <paramref name="box"/>
    /// (stroke + keyline included) at or below <paramref name="cap"/>.</summary>
    private static float FitInside(string text, RectangleF box, float cap, JetVectorPainter painter)
        => JetScreenChrome.FitScale(painter.Measure(text, false, 1f), box, cap, JetScreenChrome.Band(painter));

    /// <summary>Borrowed content renderers: the game owns their lifetime.</summary>
    public void Dispose() { }

}
