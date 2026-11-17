using System;
using System.Collections.Generic;
using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1040 — Jet story 6/9: the console, chat/command, lifecycle and training callouts
/// (P972 slice 3D, phase 3D: HUD rows 455-499 plus the training-callout and roll rows).
/// <para><b>Presentation only.</b> Every string is the shared model's own
/// (<see cref="HudChat.Lines"/>/<see cref="HudChat.Recipient"/>, <see cref="HudConsoleText"/>,
/// <see cref="HudContext.CommandLines"/>, <see cref="HudContext.Ripcord"/>,
/// <see cref="HudFrame.OxygenSecondsRemaining"/>, <see cref="HudContext.PodReturnInstruction"/>)
/// — never a fabricated reading. The channel and sender are already part of each
/// <see cref="ChatLogFormat"/> history line, the leader flag is
/// <see cref="HudChat.LeaderLines"/> (native <c>IsFromLeader</c>), the pane height is the
/// configured <see cref="HudChat.PaneLines"/> and every compose state is the shared
/// <see cref="ComposeMachine"/> state drawn through <see cref="ComposePaneLayout"/>.
/// </para>
/// <para><b>0 B/frame.</b> Chat fitting, the mission clock and the pod endurance number are
/// cached by (source, width, scale) or by integer value, so a steady frame composes nothing.
/// </para>
/// </summary>
public sealed partial class JetHudRenderer
{
    // Console vocabulary (ASCII only: the HUD fonts bake 32..126, so the truncation marker
    // is the three-dot ASCII ellipsis rather than U+2026, which HudFormat.Printable would
    // turn into '?').
    private const string SectorWord = "SECTOR";
    private const string ViewingWord = "VIEWING";
    /// <summary>The turret row's leading run (<c>TURRET /</c>) as one constant — a row that
    /// concatenated it per frame would allocate.</summary>
    private const string TurretPrefix = "TURRET /";
    private const string OrderAcceptedWord = "ACC";
    private const string OrderQueuedWord = "QUE";
    private const string PodWord = "LIFEPOD";
    private const string PodReturnWord = "RETURN";
    private const string MissionPrefixCountdown = "MISSION T- ";
    private const string MissionPrefixElapsed = "MISSION T+ ";
    private const string OlderHint = "(OLDER - PgDn to newest)";
    private const string ChatFilterPrefix = "[";
    private const string ChatFilterSuffix = "]";
    private const string Ellipsis = "...";

    // Element names for the layout audit (one block announcement per drawn family).
    private const string ElementConsole = "console";
    private const string ElementChat = "chat";
    private const string ElementCompose = "compose";
    private const string ElementTips = "tips";
    private const string ElementVote = "vote";
    private const string ElementPod = "lifepod";
    private const string ElementCommand = "cmd";
    private const string ElementSummary = "summary";
    private const string ElementCargo = "cargo";
    private const string ElementTraining = "training";
    private const string ElementHint = "hint";

    /// <summary>The measured chat pane of the most recent frame (proof/tests).</summary>
    public RectangleF ChatPane => _chatPane;
    /// <summary>The console column's measured box of the most recent frame.</summary>
    public RectangleF ConsoleColumn => _consoleColumn;
    /// <summary>The vote banner's ink box of the most recent frame.</summary>
    public RectangleF VoteBox => _voteBox;
    /// <summary>The compose panel drawn in the most recent frame (default when hidden).</summary>
    public RectangleF ComposeBox => _composeBox;
    /// <summary>The CMD panel drawn in the most recent frame (default when hidden).</summary>
    public RectangleF CommandPanel => _commandPanel;
    /// <summary>The lifepod panel's ink block of the most recent frame.</summary>
    public RectangleF LifepodBox => _lifepodBox;
    /// <summary>P1040b — whether the lifepod block is RESERVED (and therefore drawn) this
    /// frame: the model is in a pod and the column band can hold the block. The caution panel
    /// reads this instead of the pod gate, so `POD OXY` yields only when the panel really is
    /// on screen.</summary>
    public bool LifepodDrawable => _lifepodDrawable;
    /// <summary>P1040b — the slot the controls hint took (0 below the tip stack, 1 above the
    /// chat pane, 2 under the radar scope), -1 when no hint was drawn.</summary>
    public int HintSlotIndex => _hintSlot;
    /// <summary>P1040b — the controls hint's measured box of the most recent frame
    /// (default when no hint is drawn).</summary>
    public RectangleF HintBox => _hintBox;
    /// <summary>P1040b — whether the controls hint found a slot clear of every obstacle
    /// (false means the audit must count it).</summary>
    public bool HintClear => _hintClear;
    /// <summary>P1040b — the ordinary tip stack's measured block of the most recent frame.</summary>
    public RectangleF TipBlock => _tipBlock;
    /// <summary>P1040b — the cargo line's measured ink box of the most recent frame.</summary>
    public RectangleF CargoBox => _cargoBox;
    /// <summary>P1040b — the flight-parts line's measured ink box of the most recent frame.</summary>
    public RectangleF FlightPartsBox => _flightPartsBox;

    private RectangleF _chatPane;
    private RectangleF _consoleColumn;
    private RectangleF _voteBox;
    private RectangleF _composeBox;
    private RectangleF _commandPanel;
    private RectangleF _lifepodBox;
    private bool _lifepodDrawable;
    private RectangleF _hintBox;
    private bool _hintClear;
    private int _hintSlot = -1;
    private RectangleF _tipBlock;
    private RectangleF _cargoBox;
    private RectangleF _flightPartsBox;

    /// <summary>P1040b — the obstacles a band label (the controls hint) must stay clear of
    /// this frame: the registered furniture plus the measured console boxes. One reused list,
    /// so a steady frame allocates nothing.</summary>
    private readonly List<RectangleF> _obstacles = new(64);

    // Cached composed strings: a steady frame allocates nothing.
    private string _missionText = "";
    private int _missionShown = int.MinValue;
    private bool _missionCountdownShown;
    private string _podEnduranceText = "";
    private int _podEnduranceShown = int.MinValue;
    private readonly JetConsoleFitCache _chatFit = new(512);
    private readonly JetConsoleFitCache _valueFit = new(256);

    /// <summary>
    /// The chat pane's box for this frame. Computed from the SAME heading furniture the
    /// flight pass drew (<see cref="_headingPlate"/>), so the pane starts below the tape at
    /// every resolution and the two can never overlap; one extra row is always reserved
    /// above the pane for the OLDER/filter markers.
    /// <para><b>P1040b — the critical-block reservation.</b> While the model is in a lifepod
    /// the console column reserves the <c>LIFEPOD</c> block's rows (<see
    /// cref="JetConsoleLayout.PodBlockHeight"/>) at the bottom of its band BEFORE the chat log
    /// takes its height, so the pane's window is shortened by exactly the rows the block needs
    /// (never below one line) and the survival-critical block can never be the element that
    /// does not fit. At 1080p the band is deep enough that the configured count is unchanged.</para>
    /// </summary>
    private RectangleF ChatPaneRect(HudModel model, float w, float scale)
    {
        int lines = model.Context.Chat is { PaneLines: > 0 } chat
            ? ChatPaneLayout.ConfiguredLines(chat.PaneLines)
            : ChatPaneLayout.DefaultLines;
        float rowPitch = ChatRowPitch(scale);
        float top = 10f * scale + rowPitch;
        if (_headingPlate.Height > 0f) top = _headingPlate.Y + _headingPlate.Height + 8f * scale;
        // The speed tape's caption owns the band above its rail; the pane starts below both
        // (at 800x480 the tape reaches y 96, so the caption is the binding edge).
        if (_speedCaptionBox.Height > 0f)
            top = MathF.Max(top, _speedCaptionBox.Y + _speedCaptionBox.Height + 8f * scale);
        // The OLDER marker and the filter label ride one row above the pane; reserve it so
        // neither can land on the tape caption.
        if (model.Context.Chat is { } markers && (markers.Offset > 0 || markers.FilterLabel is not null))
            top += rowPitch;
        // P1040b — reserve the consoled critical block (and the compose panel, which also
        // owns the band under the log) before the log takes its height. The block plus the
        // column's own first ordinary row (sector/viewing — the highest-priority row) are the
        // minimum the column keeps; the chat log yields the rest and is a scrolling window.
        if (model.PodVisible && model.Telemetry is not null)
        {
            float textHeight = _painter.Measure("X", large: false, scale).Y;
            float reserved = JetConsoleLayout.PodBlockHeight(_viewportHeight, textHeight, scale);
            if (HasSectorRow(model)) reserved += JetConsoleLayout.RowPitch(textHeight, scale);
            if (model.Context.Chat is { Composing: true } compose && compose.ComposeState != NativeComposeState.NotComposing)
                reserved += ComposePaneLayout.Variant(compose.ComposeState).PanelHeight * scale
                    + JetConsoleLayout.ChatToColumn * scale;
            float columnBottom = _storesTop > 0f ? _storesTop - JetConsoleLayout.RowGap * scale
                : _viewportHeight - JetStoresPanel.BottomInset;
            lines = JetConsoleLayout.ChatLinesWithReserve(lines, top, columnBottom, rowPitch, scale, reserved);
        }
        RectangleF pane = JetConsoleLayout.ChatPane(scale, top, lines, rowPitch, w, JetStoresPanel.LadderColumnLeft(w, scale));
        float edge = ConsoleLeftEdge(scale);
        float rightLimit = JetStoresPanel.LadderColumnLeft(w, scale) - JetConsoleLayout.ColumnToLadder * scale;
        return pane with { X = edge, Width = MathF.Min(pane.Width, MathF.Max(0f, rightLimit - edge)) };
    }

    /// <summary>The console family's left edge: the authored edge, pushed right of the
    /// measured speed readout plate so no console row can strike the tape's numeric box.</summary>
    private float ConsoleLeftEdge(float scale)
    {
        float left = JetConsoleLayout.ConsoleLeft * scale;
        if (_speedPlate.Width > 0f) left = MathF.Max(left, _speedPlate.X + _speedPlate.Width + 6f * scale);
        return left;
    }

    private float ChatRowPitch(float scale)
    {
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        return MathF.Max(JetConsoleLayout.ChatRowHeight * scale, textHeight + JetConsoleLayout.RowGap * scale);
    }

    /// <summary>
    /// The Jet console composition (P972 §5.5 rows 6, 7, 12, 24): the chat history pane with
    /// the native window/scroll/channels, the five compose states and the two shared
    /// bottom-left lines. Replaces the Classic <c>Chat</c>/<c>Cargo</c>/<c>FlightParts</c>
    /// forwards the skin used before P1040.
    /// </summary>
    public void DrawConsoleJet(HudModel model)
    {
        if (!model.Visible) return;
        float scale = ModernHudScale.Core(_displayScale, _settings.Size, _viewportHeight);
        _painter.Scale = scale;
        _chatPane = ChatPaneRect(model, _viewportWidth, scale);
        _lifepodDrawable = LifepodReserved(model, scale);
        _surface.Begin();
        try
        {
            DrawChatPane(model, scale);
            DrawBottomLines(model, scale);
        }
        finally
        {
            _surface.End();
        }
    }

    /// <summary>The chat history pane: the shared formatted window (channel + sender already
    /// in each line), the leader lines in the bolder face, the OLDER/filter markers, and the
    /// configured pane height (native <c>ChatListPane</c>, <c>dialog.mdl:180</c>).</summary>
    private void DrawChatPane(HudModel model, float scale)
    {
        if (model.Context.Chat is not { } chat) return;
        bool composing = chat.Composing && chat.ComposeState != NativeComposeState.NotComposing;
        if (!model.ChatHistoryVisible && !composing) return;

        // Native history font roles: the pane draws largeFont, leader lines largeBoldFont
        // (chatpane.cpp:34,93). Jet owns two baked faces, so a leader line is drawn in the
        // LARGE face scaled to the row's own height — a visibly heavier run that cannot
        // change the row pitch.
        float rowPitch = ChatRowPitch(scale);
        float smallHeight = _painter.Measure("X", large: false, scale).Y;
        float largeHeight = _painter.Measure("X", large: true, scale).Y;
        float leaderScale = largeHeight > 0f ? scale * (smallHeight / largeHeight) : scale;

        if (model.ChatHistoryVisible && chat.Lines.Count > 0)
        {
            RectangleF pane = _chatPane;
            _painter.Element(ElementChat);
            // Pane backing + keyline first: every run below is inside it and no stroke is
            // drawn after the text (the audit's stroke-vs-text rule).
            _painter.FillPlate(pane, 0.62f);
            float inner = JetConsoleLayout.ChatPad * scale;
            float width = pane.Width - inner * 2f;

            if (chat.Offset > 0)
            {
                _painter.Text(_chatFit.Fit(_painter, OlderHint, scale, false, width),
                    new(pane.X + inner, pane.Y - rowPitch), large: false, scale, _painter.Dim);
            }
            if (chat.FilterLabel is { Length: > 0 } filter)
            {
                float prefixWidth = _painter.Measure(ChatFilterPrefix, large: false, scale).X;
                float suffixWidth = _painter.Measure(ChatFilterSuffix, large: false, scale).X;
                Vector2 fs = _painter.Measure(filter, large: false, scale);
                float fx = pane.X + pane.Width - inner - fs.X - prefixWidth - suffixWidth;
                float fy = pane.Y - rowPitch;
                _painter.Text(ChatFilterPrefix, new(fx, fy), large: false, scale, _painter.Dim);
                _painter.Text(filter, new(fx + prefixWidth, fy), large: false, scale, _painter.Dim);
                _painter.Text(ChatFilterSuffix, new(fx + prefixWidth + fs.X, fy), large: false, scale, _painter.Dim);
            }

            int take = Math.Min(chat.Lines.Count, ChatPaneLayout.DefaultLines);
            for (int i = 0; i < take; i++)
            {
                float y = pane.Y + inner + i * rowPitch;
                if (y + smallHeight > pane.Y + pane.Height) break;
                string line = _chatFit.Fit(_painter, chat.Lines[i], scale, false, width);
                bool leader = chat.LeaderLines is { } leaders && i < leaders.Count && leaders[i];
                _painter.Text(line, new(pane.X + inner, y), leader, leader ? leaderScale : scale,
                    leader ? _painter.Primary : _painter.Dim);
            }
        }

        if (composing) DrawComposePane(chat, scale);
        else _composeBox = default;
    }

    /// <summary>
    /// The five native compose states (P972 §5.5 row 7) as Jet vector panes: the shared
    /// <see cref="ComposePaneLayout"/> variant (panel width/height and every inner box),
    /// the resolved recipient, the typed text with its native caret (console.cpp:1290-1292)
    /// and the 0.5 s slide progress (<see cref="HudChat.ComposeProgress"/>, the shared
    /// <see cref="PaneSlideTracker"/> value). Focus and typing stay untouched: this method
    /// only draws the model.
    /// </summary>
    private void DrawComposePane(HudChat chat, float scale)
    {
        ComposeVariant variant = ComposePaneLayout.Variant(chat.ComposeState);
        float progress = Math.Clamp(chat.ComposeProgress, 0f, 1f);
        float s = scale;
        // Jet hangs the composer directly under its chat pane (the native anchor is the
        // bottom-centre band Jet's target block owns) and rides the same 0.5 s progress:
        // the pane rises into place from ComposeTravel below its home.
        float left = _chatPane.X + JetConsoleLayout.ChatPad * s;
        float top = _chatPane.Y + _chatPane.Height + 2f * s
            + (1f - progress) * JetConsoleLayout.ComposeTravel * s;
        var panel = new RectangleF(left, top, variant.PanelWidth * s, variant.PanelHeight * s);
        _composeBox = panel;
        _painter.Element(ElementCompose);
        _painter.FillPlate(panel, 0.80f);
        // A caret tick on the panel's left edge marks the active input (shape, not hue).
        _painter.Line(new(panel.X, panel.Y + 3f * s), new(panel.X, panel.Y + panel.Height - 3f * s),
            _painter.Stroke, _painter.Primary);

        // The native inner boxes are 17 px high (StringPane Point(...,17)) and the substitute
        // font is taller, so the inner runs are drawn at the largest scale that fits the box
        // (never below half) instead of spilling past the panel.
        float lineHeight = _painter.Measure("X", large: false, s).Y;
        float boxHeight = ComposeVariant.InnerTextHeight * s;
        float innerScale = lineHeight <= boxHeight - 2f * s
            ? s : MathF.Max(0.5f, s * (boxHeight - 2f * s) / lineHeight);
        float inner = 2f * s;
        if (variant.HasRecipient)
        {
            float boxWidth = variant.RecipientWidth * s - inner;
            string recipient = _valueFit.Fit(_painter, chat.Recipient, innerScale, false, boxWidth);
            _painter.Text(recipient, new(panel.X + variant.RecipientLeft * s + inner * 0.5f,
                panel.Y + variant.RecipientTop * s + inner * 0.5f), large: false, innerScale, _painter.Primary);
        }

        if (variant.HasVerbIcon)
        {
            // Native draws the verb's own icon here (console.cpp:2334-2355 maps the typed
            // verb to a CommandID). Jet draws a corner mark and nothing invented.
            float gx = panel.X + variant.VerbLeft * s, gy = panel.Y + variant.VerbTop * s;
            float size = 10f * s;
            _painter.Line(new(gx, gy), new(gx + size, gy), _painter.Hairline, _painter.Dim);
            _painter.Line(new(gx, gy), new(gx, gy + size), _painter.Hairline, _painter.Dim);
            _painter.Line(new(gx + size, gy + size), new(gx + size, gy + size * 0.45f), _painter.Hairline, _painter.Dim);
            _painter.Line(new(gx + size, gy + size), new(gx + size * 0.45f, gy + size), _painter.Hairline, _painter.Dim);
        }

        if (variant.HasText)
        {
            // Native renders the typed text plus "|" in every state except the command line
            // (UpdateComposedChat, console.cpp:1290-1292); the caret is the native glyph.
            float textWidth = variant.TextWidth * s;
            float textTop = panel.Y + variant.TextTop * s + inner * 0.5f;
            float textX = panel.X + variant.TextLeft * s;
            if (chat.ComposeState == NativeComposeState.Command)
            {
                _painter.Text(_valueFit.Fit(_painter, chat.Input, innerScale, false, textWidth), new(textX, textTop),
                    large: false, innerScale, _painter.Primary);
            }
            else
            {
                Vector2 caret = _painter.Measure("|", large: false, innerScale);
                float room = MathF.Max(0f, textWidth - caret.X);
                string typed = _valueFit.Fit(_painter, chat.Input, innerScale, false, room);
                Vector2 typedInk = _painter.Measure(typed, large: false, innerScale);
                _painter.Text(typed, new(textX, textTop), large: false, innerScale, _painter.Primary);
                _painter.Text("|", new(textX + typedInk.X, textTop), large: false, innerScale, _painter.Hot);
            }
        }
    }

    /// <summary>The two shared bottom-left lines at their native fixed offsets
    /// (<c>Game1.InFlightUtility.cs:169</c>, <c>Game1.FlightParts.cs:324</c>), drawn in Jet
    /// chrome instead of the Classic pass, plus the three ordinary tip slots.
    /// <para><b>P1040b — the bottom band.</b> These rows keep their native offsets but are
    /// drawn in the JET HUD's own text role and density scale (<c>large: false</c>, the
    /// <see cref="ModernHudScale.Core"/> scale every other Jet row uses — P1040 drew them at
    /// the raw 1.0 scale, which at 800×480 made them larger than the whole HUD and, with the
    /// console column's 152 px bound, cut them to `WEAPONS: G...`). The band they occupy is
    /// measured: the console edge to the viewport gutter, trimmed only by fixed furniture
    /// that the row's own ink band would cross (<see cref="JetConsoleLayout.BandRight"/>) — so
    /// the weapon group/selected weapon and the cargo count/item stay readable — and, in the
    /// command view, to the space left of the CMD panel.</para>
    /// </summary>
    private void DrawBottomLines(HudModel model, float scale)
    {
        float h = _viewportHeight;
        float left = ConsoleLeftEdge(scale);
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float commandLeft = CommandBandLeft(model, scale);
        _cargoBox = default;
        _flightPartsBox = default;
        _painter.Element(ElementCargo);
        if (model.Context.CargoLine is { Length: > 0 } cargo)
            _cargoBox = BottomText(cargo, left, JetConsoleLayout.CargoTop(h), textHeight, scale,
                commandLeft, _painter.Dim);
        if (model.Context.FlightPartsLine is { Length: > 0 } parts)
            _flightPartsBox = BottomText(parts, left, JetConsoleLayout.FlightPartsTop(h), textHeight, scale,
                commandLeft, _painter.Dim);

        DrawTips(model, scale, draw: true);
    }

    /// <summary>One bottom-band run: fitted to the measured band, drawn at the Jet scale, and
    /// returned as its ink box (the hint's obstacle set reads it too).</summary>
    private RectangleF BottomText(string text, float left, float top, float textHeight, float scale,
        float commandLeft, Color ink)
    {
        float right = JetConsoleLayout.BandRight(Furniture, left, top, textHeight, _viewportWidth, scale);
        if (commandLeft < float.MaxValue) right = MathF.Min(right, commandLeft);
        float width = MathF.Max(0f, right - left);
        if (width < 8f * scale) return default;
        string fitted = _chatFit.Fit(_painter, text, scale, false, width);
        _painter.Text(fitted, new(left, top), large: false, scale, ink);
        return new RectangleF(left, top, _painter.Measure(fitted, large: false, scale).X, textHeight);
    }

    /// <summary>In the command view the CMD panel owns the bottom-centre band, so the bottom
    /// rows stop at its left edge; elsewhere there is no such bound.</summary>
    private float CommandBandLeft(HudModel model, float scale)
        => model.Context.CommandView && model.Context.CommandPaneSlide > 0f
            ? JetCommandConsole.PanelLeft(_viewportWidth, model.Context.CommandPaneSlide) - 8f * scale
            : float.MaxValue;

    /// <summary>
    /// The ordinary tip rows (native's three retained slots, dialog.mdl:5347-5349), newest
    /// first, side colour as ink brightness.
    /// <para><b>P1040b — the wrap rule.</b> A tip gets the full bottom band, not the console
    /// column's width, and a tip that still does not fit is WRAPPED onto a second row at the
    /// same pitch instead of being cut to `Welcome to the se...` (P1040's defect). Only a
    /// single unbreakable word is clipped, and then with the ASCII ellipsis. The stack is
    /// measured into <see cref="_tipBlock"/> whether or not it is drawn, because the controls
    /// hint's slot search needs the block before the console pass runs.</para>
    /// </summary>
    private void DrawTips(HudModel model, float scale, bool draw)
    {
        _tipBlock = default;
        if (model.Context.Console is not { } console || console.OrdinaryTips.Count == 0) return;
        if (draw) _painter.Element(ElementTips);
        Color ink = console.SideColor is { } side ? new Color(side.R, side.G, side.B, side.A) : _painter.Primary;
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float pitch = MathF.Max(JetConsoleLayout.TipPitch, textHeight + JetConsoleLayout.RowGap * scale);
        float h = _viewportHeight;
        float left = ConsoleLeftEdge(scale);
        float limitBottom = h - JetReadoutPlacement.Gutter * scale;
        int count = Math.Min(console.OrdinaryTips.Count, HudConsoleFormat.OrdinaryTipSlots);
        float y = h - JetConsoleLayout.TipFirstInset;
        float top = y, bottom = y, widest = 0f;
        for (int i = 0; i < count; i++)
        {
            if (y + textHeight > limitBottom) break;
            float right = JetConsoleLayout.BandRight(Furniture, left, y, textHeight, _viewportWidth, scale);
            float width = MathF.Max(0f, right - left);
            if (width < 8f * scale) break;
            (string first, string second) = _tipWrap.Wrap(_painter, console.OrdinaryTips[i], scale, width);
            bool wrap = second.Length > 0 && y + pitch + textHeight <= limitBottom;
            Color rowInk = i == 0 ? ink : ink * 0.75f;
            if (draw)
                _painter.Text(first, new(left, y), large: false, scale, rowInk);
            float rowRight = left + _painter.Measure(first, large: false, scale).X;
            if (rowRight > widest) widest = rowRight;
            y += pitch;
            if (wrap)
            {
                string tail = _valueFit.Fit(_painter, second, scale, false, width);
                if (draw) _painter.Text(tail, new(left, y), large: false, scale, rowInk * 0.9f);
                rowRight = left + _painter.Measure(tail, large: false, scale).X;
                if (rowRight > widest) widest = rowRight;
                y += pitch;
            }
            bottom = y - pitch + textHeight;
        }
        _tipBlock = bottom > top
            ? new RectangleF(left, top, MathF.Max(JetConsoleLayout.ColumnRight(_viewportWidth, scale), widest) - left,
                bottom - top)
            : default;
    }

    /// <summary>An ordinary tip split into at most two rows, cached by (text, width, scale):
    /// a steady frame composes nothing.</summary>
    private readonly JetTipWrapCache _tipWrap = new(64);

    private sealed class JetTipWrapCache(int limit)
    {
        private readonly record struct Key(string Text, int WidthQuarter, int ScaleMilli);
        private readonly Dictionary<Key, (string First, string Second)> _cache = new(limit);

        public (string First, string Second) Wrap(JetVectorPainter painter, string text, float scale, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return (text, "");
            var key = new Key(text, (int)MathF.Round(maxWidth * 4f), (int)MathF.Round(scale * 1000f));
            if (_cache.TryGetValue(key, out (string First, string Second) cached)) return cached;
            string safe = HudFormat.Printable(text);
            (string First, string Second) result = (safe, "");
            if (painter.Measure(safe, large: false, scale).X > maxWidth)
            {
                int split = -1;
                for (int i = safe.Length - 1; i > 0; i--)
                {
                    if (safe[i] != ' ') continue;
                    if (painter.Measure(safe[..i], large: false, scale).X <= maxWidth) { split = i; break; }
                }
                // A break at a space keeps both halves whole; a single unbreakable word is
                // left to the caller's ellipsis fit on one row.
                if (split > 0) result = (safe[..split], safe[(split + 1)..].TrimStart());
            }
            if (_cache.Count >= limit) _cache.Clear();
            _cache[key] = result;
            return result;
        }
    }

    /// <summary>
    /// The console text column (P972 §5.5 rows 1-5, 15, 17, 18): sector (or the docked
    /// "Currently viewing" state), turret carrier, the accepted/queued order lines, the
    /// mission clock and the lifepod panel — each read straight from the shared model and
    /// placed in the left column under the chat pane.
    /// <para><b>P1040b — priority order.</b> The <c>LIFEPOD</c> block is survival-critical and
    /// is placed FIRST, anchored to the bottom edge of the column band, whatever the rest of
    /// the column does. The other rows then fill the band ABOVE it in their native order
    /// (sector, turret, queued, accepted, mission) and are dropped from the bottom of that
    /// list when the band is short — so at 800×480 the pod is drawn and a lower row gives way,
    /// never the reverse (P1040 drew `SECTOR` alone and the pod nowhere).</para>
    /// </summary>
    private void DrawConsoleColumn(HudModel model, float w, float h, float scale)
    {
        if (model.Context.Console is not { } console)
        {
            _consoleColumn = default;
            _lifepodBox = default;
            return;
        }
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float pitch = JetConsoleLayout.RowPitch(textHeight, scale);
        float left = ConsoleLeftEdge(scale);
        float limit = JetConsoleLayout.ColumnRight(w, scale);
        // The column never runs into the stores block: its lowest row stops above the
        // measured stores top (the block owns the lower-left band).
        float bottom = _storesTop > 0f ? _storesTop - JetConsoleLayout.RowGap * scale : h - 166f;
        float start = _chatPane.Y + _chatPane.Height + JetConsoleLayout.ChatToColumn * scale;
        // A composing chat panel occupies the space directly under the log, so the column
        // starts below it rather than sharing its band. The height comes from the shared
        // variant (the panel itself is drawn later, in the console pass).
        if (model.Context.Chat is { Composing: true } chat && chat.ComposeState != NativeComposeState.NotComposing)
        {
            float panelHeight = ComposePaneLayout.Variant(chat.ComposeState).PanelHeight * scale;
            start = MathF.Max(start, _chatPane.Y + _chatPane.Height + panelHeight + 2f * scale
                + JetConsoleLayout.ChatToColumn * scale);
        }
        // P1040b — the reserved critical block: `_lifepodDrawable` says the block fits the
        // band (it is what the caution panel reads for `POD OXY`), and the ordinary rows may
        // use only the space ABOVE it, so the block is always drawn and a lower row is the
        // element that gives way.
        _lifepodDrawable = LifepodReserved(model, scale);
        float podHeight = _lifepodDrawable ? JetConsoleLayout.PodBlockHeight(h, textHeight, scale) : 0f;
        int rowsBudget = podHeight > 0f
            ? Math.Max(0, (int)MathF.Floor((bottom - start - podHeight + 0.01f) / pitch)) : int.MaxValue;
        int row = 0;
        float y = start;
        bool Room() => row < rowsBudget && y + textHeight <= bottom;

        _painter.Element(ElementConsole);

        // 1. Sector location: in flight the cluster name (console.cpp:870); docked in the
        // command view the native "Currently viewing:" block's name (dialog.mdl:1429-1471).
        if (model.FlightVisible && console.SectorName is { Length: > 0 } sector && Room())
        {
            y = ConsoleRow(SectorWord, sector, y, scale, left, limit, pitch, _painter.Primary);
            row++;
        }
        else if (!model.FlightVisible && model.Context.Docked && model.Context.CommandView
                 && console.ViewedSectorName is { Length: > 0 } viewed && Room())
        {
            y = ConsoleRow(ViewingWord, viewed, y, scale, left, limit, pitch, _painter.Primary);
            row++;
        }

        // 2. Turret carrier identity: of the SOURCE ship, only for a turret gunner
        // (console.cpp:797, dialog.mdl:1508-1543).
        if (model.FlightVisible && console.BoardState == 2 && console.CarrierName is { Length: > 0 } carrier
            && Room())
        {
            y = ConsoleRow(TurretPrefix, carrier, y, scale, left, limit, pitch, _painter.Primary);
            row++;
        }

        // 3/4. Accepted and queued order text (console.cpp:875-908; dialog.mdl:1605-1648).
        if (console.QueuedOrder is { Length: > 0 } queued && Room())
        {
            y = ConsoleRow(OrderQueuedWord, queued, y, scale, left, limit, pitch, _painter.Hot);
            row++;
        }
        if (console.AcceptedOrder is { Length: > 0 } accepted && Room())
        {
            y = ConsoleRow(OrderAcceptedWord, accepted, y, scale, left, limit, pitch, _painter.Primary);
            row++;
        }

        // 5. Mission clock: the shared MissionClock value (WinTrek.cpp:8128-8143) as
        // MISSION T-/T+ mm:ss, drawn only when the model carries a clock.
        if (model.Context.MissionClockSeconds is { } seconds && Room())
        {
            _painter.Text(MissionText(seconds, model.Context.MissionCountdown), new(left, y),
                large: false, scale, _painter.Dim);
            y += pitch;
            row++;
        }

        // 6. Lifepod lifecycle panel (P972 §5.5 rows 17-18): the block the column reserved,
        // drawn directly under the rows that fitted (its native position), whatever they were.
        _lifepodBox = default;
        if (_lifepodDrawable && model.Telemetry is { } pod)
            DrawLifepodBlock(model, pod, scale, left, limit, y, pitch, textHeight);

        _consoleColumn = _lifepodBox.Height > 0f || y > start
            ? new RectangleF(left, start, limit - left,
                (_lifepodBox.Height > 0f ? _lifepodBox.Y + _lifepodBox.Height : y) - start)
            : default;
    }

    /// <summary>P1040b — does the lifepod block fit the console band this frame? The model
    /// must be in a pod and the band between the column's start and the stores block must hold
    /// the block's rows; the ordinary rows are then limited so the block is never squeezed out.
    /// Pure geometry from the same measured inputs the column uses, so the caution pass and the
    /// drawing pass cannot disagree.</summary>
    private bool LifepodReserved(HudModel model, float scale)
    {
        if (!model.PodVisible || model.Telemetry is null) return false;
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float bottom = _storesTop > 0f ? _storesTop - JetConsoleLayout.RowGap * scale
            : _viewportHeight - JetStoresPanel.BottomInset;
        float start = _chatPane.Y + _chatPane.Height + JetConsoleLayout.ChatToColumn * scale;
        if (model.Context.Chat is { Composing: true } chat && chat.ComposeState != NativeComposeState.NotComposing)
            start = MathF.Max(start, _chatPane.Y + _chatPane.Height
                + ComposePaneLayout.Variant(chat.ComposeState).PanelHeight * scale + 2f * scale
                + JetConsoleLayout.ChatToColumn * scale);
        return bottom - start >= JetConsoleLayout.PodBlockHeight(_viewportHeight, textHeight, scale) - 0.5f;
    }

    /// <summary>The sector/viewing row's gate — the column's first ordinary row, which shares
    /// the tight band with the reserved lifepod block.</summary>
    private static bool HasSectorRow(HudModel model)
        => model.Context.Console is { } console
            && ((model.FlightVisible && console.SectorName is { Length: > 0 })
                || (!model.FlightVisible && model.Context.Docked && model.Context.CommandView
                    && console.ViewedSectorName is { Length: > 0 }));

    /// <summary>
    /// P1040b — the lifepod block's ink. Standard form (three rows): <c>LIFEPOD</c>,
    /// <c>OXY ▮ nn s</c>, <c>RETURN …</c>. Compact form at 800×480 (two rows, the stated
    /// contraction): <c>LIFEPOD OXY ▮ nn s</c> on one row and <c>RETURN …</c> on the next. The
    /// numeric endurance is right-aligned at the column edge and the segmented bar (the shared
    /// fuel gauge fraction, <c>HudModel</c> relabels it OXY) shrinks into the room between the
    /// word and that number — the same reading, drawn once.
    /// </summary>
    private void DrawLifepodBlock(HudModel model, HudFrame pod, float scale, float left,
        float limit, float top, float pitch, float textHeight)
    {
        bool compact = _viewportHeight < JetStoresPanel.CompactHeight;
        _painter.Element(ElementPod);
        float y = top;
        string endurance = pod.OxygenSecondsRemaining is { } oxygen ? PodEndurance(oxygen) : "";
        HudGauge? fuel = GaugeOf(model, GaugeKind.Fuel);

        _painter.Text(PodWord, new(left, y), large: false, scale, _painter.Hot);
        float cursor = left;
        if (compact)
        {
            // One row: identity, OXY, the endurance bar and the numeric endurance.
            cursor += _painter.Measure(PodWord, large: false, scale).X + JetConsoleLayout.WordGap * scale;
            _painter.Text("OXY", new(cursor, y), large: false, scale, _painter.Primary);
            cursor += _painter.Measure("OXY", large: false, scale).X + JetConsoleLayout.WordGap * scale;
            float endWidth = endurance.Length > 0 ? _painter.Measure(endurance, large: false, scale).X : 0f;
            float endX = limit - endWidth;
            float barRoom = MathF.Max(0f, endX - JetConsoleLayout.WordGap * scale - cursor);
            if (fuel is { Visible: true } podFuel && barRoom >= 6f * scale)
                SegmentedBar(cursor, y + textHeight * 0.35f,
                    MathF.Min(JetStoresPanel.BarWidth * scale, barRoom), podFuel.Fraction, _painter.Hot);
            if (endWidth > 0f && endX > cursor)
                _painter.Text(endurance, new(endX, y), large: false, scale, _painter.Dim);
            y += pitch;
        }
        else
        {
            y += pitch;
            _painter.Text("OXY", new(left, y), large: false, scale, _painter.Primary);
            float oxyWidth = _painter.Measure("OXY", large: false, scale).X;
            float barX = left + oxyWidth + JetConsoleLayout.WordGap * scale;
            float barWidth = MathF.Min(JetStoresPanel.BarWidth * scale, MathF.Max(0f, limit - barX - 40f * scale));
            if (fuel is { Visible: true } podFuel && barWidth > 4f)
                SegmentedBar(barX, y + textHeight * 0.35f, barWidth, podFuel.Fraction, _painter.Hot);
            if (endurance.Length > 0)
                _painter.Text(endurance, new(barX + MathF.Max(barWidth, 0f) + 6f * scale, y),
                    large: false, scale, _painter.Dim);
            y += pitch;
        }
        if (model.Context.PodReturnInstruction is { Length: > 0 } instruction && y + textHeight <= top + pitch * JetConsoleLayout.PodRows(_viewportHeight) + 0.5f)
            y = ConsoleRow(PodReturnWord, instruction, y, scale, left, limit, pitch, _painter.Dim);
        _lifepodBox = new RectangleF(left, top, limit - left, y - top);
        _painter.Drawn("lifepod");
    }

    /// <summary>One console row: the leading word at the column edge, then the value fitted
    /// to the remaining width. Returns the next row's top.</summary>
    private float ConsoleRow(string word, string value, float y, float scale, float left, float limit,
        float pitch, Color ink)
    {
        _painter.Text(word, new(left, y), large: false, scale, ink);
        float cursor = left + _painter.Measure(word, large: false, scale).X + JetConsoleLayout.WordGap * scale;
        float room = MathF.Max(0f, limit - cursor);
        if (room > 0f)
            _painter.Text(_valueFit.Fit(_painter, value, scale, false, room), new(cursor, y),
                large: false, scale, _painter.Dim);
        return y + pitch;
    }

    /// <summary>The cached <c>MISSION T-/T+ mm:ss</c> line (shared rounding/format).</summary>
    private string MissionText(int seconds, bool countdown)
    {
        if (seconds != _missionShown || countdown != _missionCountdownShown)
        {
            _missionShown = seconds;
            _missionCountdownShown = countdown;
            _missionText = (countdown ? MissionPrefixCountdown : MissionPrefixElapsed) + MissionClock.Format(seconds);
        }
        return _missionText;
    }

    /// <summary>The cached pod endurance number (<c>nn s</c>) — the same reading the shared
    /// caution line uses, printed once, in the lifepod panel.</summary>
    private string PodEndurance(int seconds)
    {
        if (seconds != _podEnduranceShown)
        {
            _podEnduranceShown = seconds;
            _podEnduranceText = seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
        return _podEnduranceText;
    }

    /// <summary>
    /// The one-time fresh-profile controls hint (P949) in Jet chrome.
    /// <para><b>P1040b — the hint's slot.</b> P1040 kept the shared renderer's unscaled anchor
    /// (<c>ChatPaneLayout.BottomFromTop + 6</c>) and at 800×480 that row landed across the
    /// ladder's <c>+20</c> rung and the chat pane's right edge. The hint is a one-line tutorial
    /// band, so it now takes the first CLEAR slot of <see cref="JetConsoleLayout.HintSlot"/>
    /// (below the tip stack, above the chat pane's marker row, under the radar scope), tested
    /// against every obstacle this frame registered — the furniture, the chat pane, the console
    /// column, the vote banner, the tip stack, the bottom rows, the compose/CMD panels and the
    /// tag. The line is fitted to the slot's width; a hint that fits no slot is drawn at the
    /// least-covered one, and the audit counts that placement as a failure (never silently laid
    /// over the ladder). Presentation only — the ControlHint rules own the 60 s/once gate.</para>
    /// </summary>
    private void DrawControlHint(HudModel model, float w, float scale)
    {
        _hintBox = default;
        _hintSlot = -1;
        if (!model.FlightVisible || model.Context.ControlHint is not { Length: > 0 } hint) return;
        float h = _viewportHeight;
        float room = w - 2f * JetReadoutPlacement.Gutter * scale;
        float measured = _painter.Measure(hint, large: false, scale).X;
        float fit = measured <= room ? scale : MathF.Max(0.65f, scale * room / measured);
        string line = _valueFit.Fit(_painter, hint, fit, false, room);
        Vector2 ink = _painter.Measure(line, large: false, fit);

        // The obstacles the hint must keep clear of, measured for THIS frame.
        DrawTips(model, scale, draw: false);       // the tip stack (measured, not drawn yet)
        _obstacles.Clear();
        for (int i = 0; i < _furnitureCount; i++) _obstacles.Add(_furniture[i]);
        AddObstacle(_chatPane);
        AddObstacle(_consoleColumn);
        AddObstacle(_voteBox);
        AddObstacle(_tipBlock);
        AddObstacle(_cargoBox);
        AddObstacle(_flightPartsBox);
        AddObstacle(_composeBox);
        AddObstacle(_commandPanel);
        float tagWidth = _painter.Measure(TagText, large: false, scale).X;
        float tagHeight = _painter.Measure(TagText, large: false, scale).Y;
        AddObstacle(new RectangleF(w * 0.5f - tagWidth * 0.5f, h - 12f * scale - tagHeight * 0.5f,
            tagWidth, tagHeight));

        float tipBottom = _tipBlock.Height > 0f ? _tipBlock.Y + _tipBlock.Height : h - JetConsoleLayout.TipFirstInset;
        float rowPitch = ChatRowPitch(scale);
        RectangleF best = default;
        float bestCover = float.MaxValue;
        int bestSlot = -1;
        for (int slot = 0; slot < 3; slot++)
        {
            RectangleF candidate = JetConsoleLayout.HintSlot(slot, w, h, scale, ink.X, ink.Y,
                _chatPane, tipBottom, rowPitch, _radarScope);
            float cover = Cover(candidate);
            if (cover <= 0f) { best = candidate; bestCover = 0f; bestSlot = slot; break; }
            if (cover < bestCover) { best = candidate; bestCover = cover; bestSlot = slot; }
        }
        if (best.Width <= 0f || best.Height <= 0f) return;
        _painter.Element(ElementHint);
        _painter.Text(line, new(best.X, best.Y), large: false, fit, _painter.Primary);
        _hintBox = new RectangleF(best.X, best.Y, ink.X, ink.Y);
        _hintClear = bestCover <= 0f;
        _hintSlot = bestSlot;
    }

    private void AddObstacle(RectangleF box)
    {
        if (box.Width > 0f && box.Height > 0f) _obstacles.Add(box);
    }

    private float Cover(RectangleF box)
        => JetConsoleLayout.Cover(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_obstacles), box);

    /// <summary>
    /// The vote banner (P972 §5.5 row 11): the shared ballot text, centred on the native
    /// offset <c>85 + 12·NumChatLines</c> px below the top (dialog.mdl:5174), fitted to the
    /// free centre span so it can never reach the console column.
    /// </summary>
    private void DrawVote(HudModel model, float w, float scale)
    {
        _voteBox = default;
        if (!model.Visible || model.Context.Console is not { } console) return;
        if (console.VoteText is not { Length: > 0 } vote) return;
        int chatLines = HudConsoleFormat.BannerChatLines(model.Context.Chat);
        Vector2 ink = _painter.Measure(vote, large: false, scale);
        float y = HudConsoleFormat.VoteTop(chatLines);
        // The native offset is unscaled, so at a small window it can reach the ladder's top
        // rung; the banner then rides above the ladder's own top edge.
        bool compactHeight = _viewportHeight < CompactHeight;
        float spanDegrees = compactHeight ? JetFlightGeometry.CompactLadderHalfSpanDegrees
            : JetFlightGeometry.LadderHalfSpanDegrees;
        if (_attitude.Current.Available)
        {
            float ladderTop = _viewportHeight * 0.5f - spanDegrees * JetFlightGeometry.LadderPixelsPerDegree * scale;
            // The top rung's numeral is centred on the ladder's top edge, so the banner ends
            // above that numeral's own ink box (1.5 line heights + a gap clear of it).
            y = MathF.Min(y, ladderTop - ink.Y * 1.5f - 4f * scale);
        }
        float gutter = 24f * scale;
        float blocked = MathF.Max(_chatPane.X + _chatPane.Width,
            JetConsoleLayout.ColumnRight(w, scale)) + JetConsoleLayout.ColumnGap(scale);
        float right = w - gutter;
        bool rowClearOfConsole = y + ink.Y <= _chatPane.Y - 4f * scale || y >= _chatPane.Y + _chatPane.Height + 4f * scale
            || w * 0.5f - ink.X * 0.5f >= blocked;
        float spanLeft = rowClearOfConsole ? gutter : blocked;
        string fitted = _valueFit.Fit(_painter, vote, scale, false, MathF.Max(0f, right - spanLeft));
        ink = _painter.Measure(fitted, large: false, scale);
        _painter.Element(ElementVote);
        float x = rowClearOfConsole ? w * 0.5f - ink.X * 0.5f
            : (spanLeft + right) * 0.5f - ink.X * 0.5f;
        x = MathF.Max(x, spanLeft);
        _painter.Text(fitted, new(x, y), large: false, scale, _painter.Hot);
        _voteBox = new RectangleF(x, y, ink.X, ink.Y);
        // P1040b — the ballot was COMMITTED this frame: the "one display per reading, never
        // zero" rule the caution stack depends on (it drops its Vote line unconditionally).
        _painter.Drawn("vote");
    }

    /// <summary>
    /// The CMD console (P972 §5.5 row 8): the native recipient/verb/wing bands at their own
    /// rectangles (<see cref="JetCommandConsole"/>) with the same 0.5 s slide
    /// (<see cref="HudContext.CommandPaneSlide"/>), plus the command-view ship summary with
    /// the model's identical lines and nothing invented.
    /// </summary>
    private void DrawCommandConsole(HudModel model)
    {
        if (!model.Visible) return;
        float scale = ModernHudScale.Core(_displayScale, _settings.Size, _viewportHeight);
        _painter.Scale = scale;
        float w = _viewportWidth, h = _viewportHeight;
        float progress = Math.Clamp(model.Context.CommandPaneSlide, 0f, 1f);
        _surface.Begin();
        try
        {
            if (progress > 0f && model.Context.CommandView) DrawCommandBar(model, w, h, progress);
            else _commandPanel = default;
            DrawCommandSummary(model, w, h, scale);
        }
        finally
        {
            _surface.End();
        }
    }

    private void DrawCommandBar(HudModel model, float w, float h, float progress)
    {
        float left = JetCommandConsole.PanelLeft(w, progress);
        float top = JetCommandConsole.PanelTop(h, progress);
        RectangleF panel = JetCommandConsole.Panel(left, top);
        _commandPanel = panel;
        _painter.Element(ElementCommand);
        // The native bar art is unscaled (dialog.mdl:73-87), so the Jet cells draw at scale
        // 1 and every drawn cell is exactly the rectangle the hit test resolves. The caption
        // sits above the panel; the console's bottom lines are fitted clear of it.
        const float scale = 1f;
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        _painter.Text("CMD", new(left, top - textHeight - 3f), large: false, scale, _painter.Primary);

        _painter.FillPlate(panel, 0.72f);
        int selectedRecipient = model.Context.CommandRecipientColumn;
        int selectedVerb = model.Context.CommandVerb;
        int wing = model.Context.CommandWing;

        // Pass 1 — every cell frame (all strokes precede all runs, so no frame can later
        // cross a glyph's ink band).
        for (int c = 0; c < JetCommandConsole.RecipientCount; c++)
        {
            RectangleF cell = JetCommandConsole.RecipientCell(c);
            Color ink = c == selectedRecipient ? _painter.Hot : _painter.Dim;
            FrameCell(new(left + cell.X, top + cell.Y), cell.Width, cell.Height, ink);
        }
        {
            RectangleF cell = JetCommandConsole.WingCell;
            FrameCell(new(left + cell.X, top + cell.Y), cell.Width, cell.Height, _painter.Primary);
        }
        for (int c = 0; c < JetCommandConsole.VerbCount; c++)
        {
            RectangleF cell = JetCommandConsole.VerbCell(c);
            Color ink = c + 1 == selectedVerb ? _painter.Hot : _painter.Dim;
            FrameCell(new(left + cell.X, top + cell.Y), cell.Width, cell.Height, ink);
        }

        // Pass 2 — the cell words, each fitted to its own cell (the native art is unscaled
        // and the substitute font is wider than the native smallFont, so a word that cannot
        // fit is drawn at a measured fit scale rather than spilling into its neighbour).
        for (int c = 0; c < JetCommandConsole.RecipientCount; c++)
        {
            RectangleF cell = JetCommandConsole.RecipientCell(c);
            Color ink = c == selectedRecipient ? _painter.Hot : _painter.Dim;
            CellWord(JetCommandConsole.RecipientWord(c),
                new(left + cell.X, top + cell.Y), cell.Width, cell.Height, ink);
        }
        {
            // P1040b — the wing combo is a TWO-LINE cell (`WING` caption above the name). P1040
            // fitted each line to the cell's WIDTH only and stacked them from the cell's top,
            // so at 800×480 the name's ink ran past the cell's bottom edge (the `DEL`/`TA`
            // broken across the panel border). Both axes are now fitted together, the block is
            // centred in the cell, and a word the 0.5 floor still cannot fit is abbreviated with
            // the ASCII ellipsis — every cell word stays inside its cell, and the cell is still
            // exactly the native <c>WingCombo</c> hit rectangle.
            RectangleF cell = JetCommandConsole.WingCell;
            string name = CommandBarLayout.WingNames[Math.Clamp(wing, 0, CommandBarLayout.WingNames.Length - 1)]
                .ToUpperInvariant();
            float pad = 2f;
            CellBlock("WING", name, new(left + cell.X + pad, top + cell.Y + pad),
                cell.Width - pad * 2f, cell.Height - pad * 2f, _painter.Dim, _painter.Primary);
        }
        for (int c = 0; c < JetCommandConsole.VerbCount; c++)
        {
            RectangleF cell = JetCommandConsole.VerbCell(c);
            Color ink = c + 1 == selectedVerb ? _painter.Hot : _painter.Dim;
            CellWord(JetCommandConsole.VerbWord(c), new(left + cell.X, top + cell.Y),
                cell.Width, cell.Height, ink);
        }
    }

    /// <summary>One control cell's top + right edge (the panel plate owns the other two).</summary>
    private void FrameCell(Vector2 at, float width, float height, Color ink)
    {
        _painter.Line(new(at.X, at.Y), new(at.X + width, at.Y), _painter.Hairline, ink);
        _painter.Line(new(at.X + width, at.Y), new(at.X + width, at.Y + height), _painter.Hairline, ink);
    }

    /// <summary>The largest scale ≤ 1 at which <paramref name="text"/> fits
    /// <paramref name="width"/> and <paramref name="height"/> (never below the 0.5
    /// readability floor, which is why the caller then abbreviates).</summary>
    private float FitScale(string text, float width, float height)
    {
        Vector2 atOne = _painter.Measure(text, large: false, 1f);
        if (atOne.X <= 0f || atOne.Y <= 0f) return 1f;
        float widthFit = atOne.X <= width ? 1f : width / atOne.X;
        float heightFit = atOne.Y <= height ? 1f : height / atOne.Y;
        return MathF.Max(0.5f, MathF.Min(1f, MathF.Min(widthFit, heightFit)));
    }

    /// <summary>The largest scale ≤ 1 at which <paramref name="text"/> fits a width
    /// (never below the 0.5 readability floor).</summary>
    private float FitScale(string text, float width) => FitScale(text, width, float.MaxValue);

    /// <summary>A cell's word: fitted to the cell in BOTH axes and centred in it.</summary>
    private void CellWord(string text, Vector2 at, float width, float height, Color ink)
    {
        float fit = FitScale(text, width - 3f, height - 2f);
        Vector2 size = _painter.Measure(text, large: false, fit);
        // The floor is a readability rule, not a licence to spill: a word that still cannot
        // fit inside the cell at 0.5 is abbreviated with the ASCII ellipsis.
        string drawn = size.X <= width - 3f ? text : _valueFit.Fit(_painter, text, fit, false, width - 3f);
        size = _painter.Measure(drawn, large: false, fit);
        _painter.Text(drawn, new(at.X + (width - size.X) * 0.5f, at.Y + (height - size.Y) * 0.5f),
            large: false, fit, ink);
    }

    /// <summary>
    /// P1040b — a two-line cell block (`WING` caption above the wing name): one fit scale for
    /// both lines from the cell's inner box, the block centred vertically and each line centred
    /// horizontally, so no word can leave the cell the hit test resolves.
    /// </summary>
    private void CellBlock(string caption, string value, Vector2 at, float width, float height,
        Color captionInk, Color valueInk)
    {
        Vector2 capOne = _painter.Measure(caption, large: false, 1f);
        Vector2 valOne = _painter.Measure(value, large: false, 1f);
        float lineHeight = MathF.Max(capOne.Y, valOne.Y);
        float widest = MathF.Max(capOne.X, valOne.X);
        float widthFit = widest <= width || widest <= 0f ? 1f : width / widest;
        float heightFit = lineHeight * 2f <= height || lineHeight <= 0f ? 1f : height / (lineHeight * 2f);
        float fit = MathF.Max(0.5f, MathF.Min(1f, MathF.Min(widthFit, heightFit)));
        Vector2 cap = _painter.Measure(caption, large: false, fit);
        Vector2 val = _painter.Measure(value, large: false, fit);
        string shown = val.X <= width ? value : _valueFit.Fit(_painter, value, fit, false, width);
        val = _painter.Measure(shown, large: false, fit);
        float blockHeight = cap.Y + val.Y;
        float top = at.Y + MathF.Max(0f, (height - blockHeight) * 0.5f);
        _painter.Text(caption, new(at.X + (width - cap.X) * 0.5f, top), large: false, fit, captionInk);
        _painter.Text(shown, new(at.X + (width - val.X) * 0.5f, top + cap.Y), large: false, fit, valueInk);
    }

    /// <summary>The command-view ship summary: the model's own lines (identical fields,
    /// <c>Game1.Commanding.cs:320-340</c>), centred in the free span and truncated only if a
    /// single line cannot fit — no invented field is added.</summary>
    private void DrawCommandSummary(HudModel model, float w, float h, float scale)
    {
        var lines = model.Context.CommandLines;
        if (lines.Count == 0) return;
        _painter.Element(ElementSummary);
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float pitch = JetConsoleLayout.RowPitch(textHeight, scale);
        float y = 24f * scale;
        // The command view hides the ladder and the tapes, so this block may use the full
        // width; it keeps the viewport centre unless a line would reach the chat pane (at
        // 800x480 the log sits at the top-left), in which case it centres in the free span.
        float gutter = 24f * scale;
        float right = w - gutter;
        float widest = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            Vector2 ink = _painter.Measure(lines[i], large: false, scale);
            if (ink.X > widest) widest = ink.X;
        }
        float blocked = MathF.Max(_chatPane.X + _chatPane.Width,
            JetConsoleLayout.ColumnRight(w, scale)) + JetConsoleLayout.ColumnGap(scale);
        float centreX = w * 0.5f;
        bool chatInTheWay = y < _chatPane.Y + _chatPane.Height + 4f * scale
            && y + lines.Count * pitch > _chatPane.Y - 4f * scale
            && centreX - widest * 0.5f < _chatPane.X + _chatPane.Width;
        if (chatInTheWay) centreX = (blocked + right) * 0.5f;
        float span = chatInTheWay ? MathF.Max(0f, right - blocked) : MathF.Max(0f, w - 32f * scale);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = _valueFit.Fit(_painter, lines[i], scale, false, span);
            Vector2 ink = _painter.Measure(line, large: false, scale);
            _painter.Text(line, new(centreX - ink.X * 0.5f, y), large: false, scale, _painter.Primary);
            y += pitch;
        }
    }

    /// <summary>
    /// The training HUD callouts (P972 §5.3 row 10) in Jet chrome: the SAME
    /// <see cref="HudTrainingCallout"/> geometry/anchor/text as the shared renderer
    /// (indicator.cpp:461-701) drawn with thin vector strokes, and the same visibility gate
    /// (<see cref="HudModel.FlightVisible"/> + an overlay id). The training control flow is
    /// not touched — this only reads <see cref="HudContext.TrainingOverlayId"/>.
    /// </summary>
    private void DrawTrainingCallout(HudModel model, float w, float h, float scale)
    {
        if (!model.FlightVisible || model.Context.TrainingOverlayId is not > 0) return;
        if (HudTrainingCallout.For(model.Context.TrainingOverlayId) is not { } c) return;
        _painter.Element(ElementTraining);
        // The shared table is already in port screen space relative to the centre and the
        // shared renderer uses it unscaled (HudRenderer.DrawTrainingCallout), so Jet keeps
        // the identical anchors and scales only the glyphs.
        float cx = w * 0.5f, cy = h * 0.5f;
        var a = new Vector2(cx + c.AnchorX, cy + c.AnchorY);
        var b = new Vector2(a.X + c.SecondX, a.Y + c.SecondY);
        Vector2 size = _painter.Measure(c.Description, large: false, scale);
        var pointC = new Vector2(b.X + (c.SecondX >= 0 ? size.X : -size.X), b.Y);
        Color ink = _painter.Hot;
        _painter.Line(a, b, _painter.Stroke, ink);        // indicator.cpp:721 SetLineWidth(2.0f)
        _painter.Line(b, pointC, _painter.Stroke, ink);
        float textX = c.SecondX >= 0 ? b.X : pointC.X;
        float textY = c.SecondY >= 0 ? b.Y + 2f : b.Y - size.Y - 2f;
        _painter.Text(c.Description, new(textX, textY), large: false, scale, ink);
        if (c.Description2 is { } second)
            _painter.Text(second, new(textX, textY - size.Y - 4f), large: false, scale, ink);
        float dot = 6f;
        _painter.Surface.Fill(new(a.X - dot * 0.5f, a.Y - dot * 0.5f), new(dot, dot), ink);
    }

    /// <summary>
    /// The ladder roll pointer (P972 §5.1 "Roll tutorial cue"): a bank index above the
    /// ladder's top rung showing the shared attitude roll. It is the ladder's own reference,
    /// not a new instrument reading — the value is <see cref="HudFlightReference"/>'s, the
    /// same one the ladder rotation uses, and the mark is drawn only while that value is
    /// valid.
    /// </summary>
    private void DrawRollPointer(float w, float h, float scale)
    {
        if (!_attitude.Current.Available || !_attitude.Current.RollValid) return;
        float roll = _attitude.Current.RollDegrees;
        bool compact = h < CompactHeight;
        float spanDegrees = compact ? JetFlightGeometry.CompactLadderHalfSpanDegrees
            : JetFlightGeometry.LadderHalfSpanDegrees;
        float halfWidth = (compact ? 28f : 46f) * scale;
        float cx = w * 0.5f;
        float y = h * 0.5f - spanDegrees * JetFlightGeometry.LadderPixelsPerDegree * scale - 10f * scale;
        float x = cx + Math.Clamp(roll / 60f, -1f, 1f) * halfWidth;
        _painter.Element("ladder");
        _painter.Line(new(x, y), new(x, y + 8f * scale), _painter.Stroke,
            MathF.Abs(roll) < 1f ? _painter.Dim : _painter.Primary);
        _painter.Line(new(x - 3f * scale, y), new(x, y - 4f * scale), _painter.Hairline, _painter.Dim);
        _painter.Line(new(x + 3f * scale, y), new(x, y - 4f * scale), _painter.Hairline, _painter.Dim);
    }

    /// <summary>A tiny (source, width, scale, face) → fitted-string cache. A steady frame's
    /// lookup is a struct key comparison and allocates nothing; only a new or re-widthed
    /// string composes its ASCII-ellipsis form once.</summary>
    private sealed class JetConsoleFitCache(int limit)
    {
        private readonly record struct Key(string Text, int WidthQuarter, int ScaleMilli, bool Large);
        private readonly Dictionary<Key, string> _cache = new(limit);

        public string Fit(JetVectorPainter painter, string text, float scale, bool large, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text;
            var key = new Key(text, (int)MathF.Round(maxWidth * 4f), (int)MathF.Round(scale * 1000f), large);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            string safe = HudFormat.Printable(text);
            string fitted = painter.Measure(safe, large, scale).X <= maxWidth
                ? safe
                : Clip(painter, safe, large, scale, maxWidth);
            if (_cache.Count >= limit) _cache.Clear();
            _cache[key] = fitted;
            return fitted;
        }

        private static string Clip(JetVectorPainter painter, string text, bool large, float scale, float maxWidth)
        {
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (painter.Measure(text[..mid] + Ellipsis, large, scale).X <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return text[..lo] + Ellipsis;
        }
    }
}
