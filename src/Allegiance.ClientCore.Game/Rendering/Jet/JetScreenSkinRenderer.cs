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

    private const float PlateAlpha = JetScreenChrome.PlateAlpha;
    private const float DimPlateAlpha = JetScreenChrome.DimPlateAlpha;
    private const float BracketArm = JetScreenChrome.BracketArm;

    /// <summary>The palette a test/proof pins without a switcher (null = the published one).</summary>
    private readonly string? _paletteOverride;

    public JetScreenSkinRenderer(IJetVectorSurface surface, HangarScreenRenderer? hangar,
        DockedScreenRenderer? docked, LoadoutV2Renderer? loadoutV2, HudSettings settings,
        JetPaletteSwitcher? palettes, string? paletteOverride = null,
        IScreenContentRenderer? careerContent = null)
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
        _careerContent?.DrawCareer(frame, JetCareerPalette(Painter));
        var painter = Painter;
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
