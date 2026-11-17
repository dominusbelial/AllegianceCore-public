using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1040 — Jet story 6/9: the console, chat/command, lifecycle and training callouts
/// (P972 slice 3D; §5.5 rows 1-18, §5.3 training callouts, §5.1 roll row, §5.6 command-view
/// summary). Headless: every assertion goes through a recording <see cref="IJetVectorSurface"/>
/// and every expected value is the shared model's own, so a second derivation cannot hide.
/// </summary>
public sealed class JetConsoleTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // ── chat history ─────────────────────────────────────────────────────
    [Fact]
    public void The_chat_history_draws_the_shared_window_with_channel_sender_and_leader_face()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudChat chat = Chat(
            new[] { "[TEAM] Pilot Alpha: defend the base", "[ALL] Enemy: incoming" },
            offset: 2, filter: "TEAM", leaders: new[] { true, false }, paneLines: 7);
        HudModel model = Model(chat);
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawConsole(model);

        // The shared formatted lines are drawn verbatim (channel + sender already in them).
        Assert.Contains(chat.Lines[0], surface.Seen);
        Assert.Contains(chat.Lines[1], surface.Seen);
        // Leader bold: the leader line is drawn in the LARGE face (Jet's bolder build).
        Assert.Contains(chat.Lines[0], surface.LargeRuns);
        Assert.DoesNotContain(chat.Lines[1], surface.LargeRuns);
        // Scrolled back: the OLDER marker and the pane's filter label.
        Assert.Contains(surface.Seen, t => t.StartsWith("(OLDER", StringComparison.Ordinal));
        Assert.Contains("TEAM", surface.Seen);
        // The pane's height is the CONFIGURED line count in the native frame, with the
        // row pitch raised to the measured line height + gap (never below the native 14 px).
        Assert.Equal(8f + MathF.Max(14f, 12f + JetConsoleLayout.RowGap) * 7f, jet.ChatPane.Height, 3);
    }

    [Fact]
    public void The_chat_pane_height_follows_the_configured_ChatLines_preference()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel nine = Model(Chat(new[] { "[TEAM] A: b" }, paneLines: 9));
        jet.Draw(nine, Frame(1920, 1080));
        jet.DrawConsole(nine);
        RectangleF tall = jet.ChatPane;
        HudModel two = Model(Chat(new[] { "[TEAM] A: b" }, paneLines: 2));
        jet.Draw(two, Frame(1920, 1080));
        jet.DrawConsole(two);
        Assert.True(tall.Height > jet.ChatPane.Height);
        Assert.Equal(8f + MathF.Max(14f, 12f + JetConsoleLayout.RowGap) * 9f, tall.Height, 3);
        Assert.Equal(8f + MathF.Max(14f, 12f + JetConsoleLayout.RowGap) * 2f, jet.ChatPane.Height, 3);
    }

    [Fact]
    public void A_long_chat_line_truncates_with_the_ascii_ellipsis_inside_the_pane()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string longLine = "[SECTOR] Pilot Charlie: " + new string('W', 200);
        HudModel model = Model(Chat(new[] { longLine }));
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawConsole(model);
        string drawn = surface.Seen.Single(t => t.StartsWith("[SECTOR]", StringComparison.Ordinal));
        Assert.EndsWith("...", drawn, StringComparison.Ordinal);
        Assert.True(drawn.Length < longLine.Length);
        // ASCII only: the fonts bake 32..126 and HudFormat.Printable would turn U+2026 into '?'.
        Assert.DoesNotContain('?', drawn);
        Assert.All(drawn, c => Assert.InRange((int)c, 32, 126));
        TextRun run = surface.RunsOf(drawn)[0];
        Assert.True(run.X >= jet.ChatPane.X - 0.5f
            && run.X + run.Width <= jet.ChatPane.X + jet.ChatPane.Width + 0.5f);
    }

    [Fact]
    public void A_hidden_chat_history_still_draws_the_active_compose_line()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            chat: Chat(new[] { "[TEAM] A: b" }, composing: true, state: NativeComposeState.Chat),
            chatHistoryVisible: false, console: new HudConsoleText { SectorName = "Alystra" });
        jet.DrawConsole(model);
        Assert.DoesNotContain("[TEAM] A: b", surface.Seen);
        Assert.Contains("|", surface.Seen);
        Assert.True(jet.ComposeBox.Width > 0f);
    }

    // ── compose states ───────────────────────────────────────────────────
    [Theory]
    [InlineData(NativeComposeState.MouseCommand)]
    [InlineData(NativeComposeState.Chat)]
    [InlineData(NativeComposeState.Command)]
    [InlineData(NativeComposeState.Shell)]
    [InlineData(NativeComposeState.Quick)]
    public void Every_compose_state_draws_the_shared_variant_with_recipient_and_caret(NativeComposeState state)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string input = state == NativeComposeState.Command ? "attack Enemy fighter" : "returning";
        HudChat chat = Chat(new[] { "[TEAM] Pilot: defend" }, composing: true, state: state, input: input);
        HudModel model = Model(chat);
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawConsole(model);

        ComposeVariant variant = ComposePaneLayout.Variant(state);
        RectangleF box = jet.ComposeBox;
        Assert.Equal(variant.PanelWidth, box.Width, 3);
        Assert.Equal(variant.PanelHeight, box.Height, 3);
        if (variant.HasRecipient) Assert.Contains(chat.Recipient, surface.Seen);
        if (!variant.HasText) return;
        Assert.Contains(input, surface.Seen);
        // The native caret (console.cpp:1290-1292): the command line has none, all others do.
        Assert.Equal(state != NativeComposeState.Command, surface.Seen.Contains("|"));
    }

    [Fact]
    public void The_compose_panel_rides_the_shared_slide_progress()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel off = Model(Chat(new[] { "[TEAM] A: b" }, composing: true,
            state: NativeComposeState.Chat, progress: 0f));
        jet.Draw(off, Frame(1920, 1080));
        jet.DrawConsole(off);
        float offTop = jet.ComposeBox.Y;
        HudModel on = Model(Chat(new[] { "[TEAM] A: b" }, composing: true,
            state: NativeComposeState.Chat, progress: 1f));
        jet.Draw(on, Frame(1920, 1080));
        jet.DrawConsole(on);
        Assert.True(jet.ComposeBox.Y < offTop);   // rises into place over the 0.5 s
        Assert.Equal(JetConsoleLayout.ComposeTravel, offTop - jet.ComposeBox.Y, 3);
    }

    [Fact]
    public void A_not_composing_chat_draws_no_compose_panel()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }));
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawConsole(model);
        Assert.Equal(0f, jet.ComposeBox.Width);
    }

    // ── the CMD console: one geometry source for draw and hit test ───────
    [Fact]
    public void Every_drawn_command_control_resolves_to_itself_through_the_jet_geometry()
    {
        // The production pick order (Game1.Commanding.TryCommandBarPick, :399-437):
        // wing, then verb, then recipient.
        int classic(int x, int y) => CommandBarLayout.InWingCombo(x, y) ? 0
            : CommandBarLayout.VerbColumn(x, y) >= 0 ? 1
            : CommandBarLayout.RecipientColumn(x, y) >= 0 ? 2 : -1;
        int checkedControls = 0;

        for (int c = 0; c < JetCommandConsole.RecipientCount; c++)
        {
            RectangleF cell = JetCommandConsole.RecipientCell(c);
            float cx = cell.X + cell.Width * 0.5f, cy = cell.Y + cell.Height * 0.5f;
            JetCommandControl hit = JetCommandConsole.HitTest(cx, cy);
            Assert.Equal(JetCommandControlKind.Recipient, hit.Kind);
            Assert.Equal(c, hit.Index);
            // Classic/Modern pick the same control from the same point…
            Assert.Equal(2, classic((int)cx, (int)cy));
            // …and the same action: the column's chat target.
            Assert.Equal(CommandBarLayout.RecipientChatTarget(c),
                CommandBarLayout.RecipientChatTarget(hit.Index));
            // The drawn cell is inside the hit column and above the verb band (so a click in it
            // cannot resolve to a verb first).
            Assert.True(cell.Y + cell.Height <= CommandBarLayout.VerbsTop);
            checkedControls++;
        }

        {
            RectangleF cell = JetCommandConsole.WingCell;
            float cx = cell.X + cell.Width * 0.5f, cy = cell.Y + cell.Height * 0.5f;
            JetCommandControl hit = JetCommandConsole.HitTest(cx, cy);
            Assert.Equal(JetCommandControlKind.Wing, hit.Kind);
            Assert.Equal(0, classic((int)cx, (int)cy));
            checkedControls++;
        }

        for (int c = 0; c < JetCommandConsole.VerbCount; c++)
        {
            RectangleF cell = JetCommandConsole.VerbCell(c);
            float cx = cell.X + cell.Width * 0.5f, cy = cell.Y + cell.Height * 0.5f;
            JetCommandControl hit = JetCommandConsole.HitTest(cx, cy);
            Assert.Equal(JetCommandControlKind.Verb, hit.Kind);
            Assert.Equal(c, hit.Index);
            Assert.Equal(1, classic((int)cx, (int)cy));
            // Both sides arm the same native CommandID (col + 1) and therefore the same verb.
            Assert.Equal(CommandBarLayout.VerbColumn((int)cx, (int)cy), hit.Index);
            Assert.Equal(CommandBarLayout.VerbCommandId(c), (short)(c + 1));
            Assert.Equal(HudConsoleFormat.Verb((short)(c + 1)),
                HudConsoleFormat.Verb(CommandBarLayout.VerbCommandId(hit.Index)));
            checkedControls++;
        }

        Assert.Equal(JetCommandConsole.RecipientCount + JetCommandConsole.VerbCount + 1, checkedControls);
        Assert.Equal("verb attack", JetCommandConsole.ActionWord(new(JetCommandControlKind.Verb, 0), 0));
        Assert.Equal("recipient Team", JetCommandConsole.ActionWord(new(JetCommandControlKind.Recipient, 2), 0));
        Assert.Equal("wing attack", JetCommandConsole.ActionWord(new(JetCommandControlKind.Wing, 0), 0));
        Assert.Equal("wing command", JetCommandConsole.ActionWord(new(JetCommandControlKind.Wing, 0), 8));
    }

    [Fact]
    public void The_cmd_console_draws_the_native_panel_at_the_shared_slide_geometry()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = CommandModel(progress: 1f, wing: 8, recipient: 3, verb: 4);
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawCommand(model);

        RectangleF panel = jet.CommandPanel;
        Assert.Equal(CommandBarLayout.PanelWidth, panel.Width, 3);
        Assert.Equal(CommandBarLayout.PanelHeight, panel.Height, 3);
        Assert.Equal(CommandBarLayout.Left(1920, 1f), panel.X, 3);
        Assert.Equal(CommandBarLayout.Top(1080, 1f), panel.Y, 3);
        foreach (string word in new[] { "ME", "SECTOR", "TEAM", "ALL", "WING", "DELTA" })
            Assert.Contains(word, surface.Seen);
        foreach (string verb in new[] { "ATK", "CAP", "DEF", "PIK", "GO", "REP", "JOIN", "MINE", "BLD" })
            Assert.Contains(verb, surface.Seen);
    }

    [Fact]
    public void The_cmd_console_is_absent_outside_the_command_view()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }));
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawCommand(model);
        Assert.Equal(0f, jet.CommandPanel.Width);
        Assert.DoesNotContain("CMD", surface.Seen);
    }

    [Fact]
    public void The_command_summary_prints_the_models_own_lines_and_nothing_more()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string[] lines = { "COMMAND SHIP: Fig Adv", "Hull 82%  Shield 70%" };
        HudModel model = CommandModel(progress: 1f, lines: lines);
        jet.Draw(model, Frame(1920, 1080));
        jet.DrawCommand(model);
        foreach (string line in lines) Assert.Contains(line, surface.Seen);
    }

    // ── console text rows ────────────────────────────────────────────────
    [Fact]
    public void Sector_turret_and_orders_read_the_shared_console_text_under_their_gates()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudConsoleText console = new()
        {
            SectorName = "Alystra",
            CarrierName = "Pilot Alpha",
            BoardState = 2,
            AcceptedOrder = "defend Garrison",
            QueuedOrder = "attack Enemy fighter",
        };
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }), console: console);
        jet.Draw(model, Frame(1920, 1080));
        Assert.Contains("SECTOR", surface.Seen);
        Assert.Contains("Alystra", surface.Seen);
        Assert.Contains("TURRET /", surface.Seen);
        Assert.Contains("Pilot Alpha", surface.Seen);
        Assert.Contains("ACC", surface.Seen);
        Assert.Contains("defend Garrison", surface.Seen);
        Assert.Contains("QUE", surface.Seen);
        Assert.Contains("attack Enemy fighter", surface.Seen);
        Assert.True(jet.ConsoleColumn.Height > 0f);

        // Non-gunner: no turret row (native BoardState == 2 only).
        var gunnerOff = new Recorder();
        using (var jet2 = Jet(gunnerOff))
            jet2.Draw(Model(Chat(new[] { "[TEAM] A: b" }), console: console with { BoardState = 1 }),
                Frame(1920, 1080));
        Assert.DoesNotContain("TURRET /", gunnerOff.Seen);
    }

    [Fact]
    public void A_long_order_line_truncates_inside_the_console_column()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string longOrder = "defend " + new string('W', 300);
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { SectorName = "Alystra", AcceptedOrder = longOrder });
        jet.Draw(model, Frame(800, 480));
        string drawn = surface.Seen.Single(t => t.StartsWith("defend", StringComparison.Ordinal));
        Assert.EndsWith("...", drawn, StringComparison.Ordinal);
        TextRun run = surface.RunsOf(drawn)[0];
        Assert.True(run.X + run.Width <= JetConsoleLayout.ColumnRight(800, ScaleFor(480)) + 0.5f);
    }

    [Fact]
    public void The_mission_clock_uses_the_shared_countdown_or_elapsed_form()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudConsoleText console = new() { SectorName = "Alystra" };
        jet.Draw(Model(Chat(new[] { "[TEAM] A: b" }), console: console,
            missionClock: 143, missionCountdown: true), Frame(1920, 1080));
        Assert.Contains("MISSION T- 02:23", surface.Seen);

        var elapsedSurface = new Recorder();
        using var elapsed = Jet(elapsedSurface);
        elapsed.Draw(Model(Chat(new[] { "[TEAM] A: b" }), console: console,
            missionClock: 65, missionCountdown: false), Frame(1920, 1080));
        Assert.Contains("MISSION T+ 01:05", elapsedSurface.Seen);
    }

    [Fact]
    public void The_lifepod_block_is_drawn_at_800x480_with_a_long_log_and_every_column_row()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        // The P1040b defect 1 scene: a 9-line chat log (with the marker row) AND every console
        // row the model carries, at the small frame. P1040 drew `SECTOR` alone — the lifepod
        // block, its oxygen endurance and the return binding were shown nowhere.
        string[] log = Enumerable.Range(0, 9)
            .Select(i => $"[TEAM] Pilot {(char)('A' + i)}: line {i} of the log").ToArray();
        HudModel model = HudModelAdapter.Create(PodFrame(), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            chat: Chat(log, offset: 3, filter: "TEAM", paneLines: 9),
            console: new HudConsoleText
            {
                SectorName = "Alystra", CarrierName = "Pilot Alpha", BoardState = 2,
                AcceptedOrder = "defend Garrison", QueuedOrder = "attack Enemy fighter",
            },
            missionClockSeconds: 125, missionCountdown: false,
            podReturnInstruction: "Shift+Z / G AUTOPILOT TO BASE");
        jet.Draw(model, Frame(800, 480));
        jet.DrawConsole(model);

        Assert.True(jet.LifepodDrawable);
        Assert.True(jet.LifepodBox.Height > 0f, "the lifepod block was not drawn");
        Assert.Contains("LIFEPOD", surface.Seen);
        Assert.Contains("OXY", surface.Seen);
        Assert.Contains("95 s", surface.Seen);
        Assert.Contains("RETURN", surface.Seen);
        // Both readings live inside the block, above the stores block's top edge.
        foreach (string word in new[] { "LIFEPOD", "OXY", "95 s" })
        {
            TextRun run = surface.RunsOf(word).First(r =>
                r.Y >= jet.LifepodBox.Y - 0.5f && r.Y <= jet.LifepodBox.Y + jet.LifepodBox.Height);
            Assert.InRange(run.Y, jet.LifepodBox.Y - 0.5f, jet.LifepodBox.Y + jet.LifepodBox.Height);
        }
        Assert.True(jet.LifepodBox.Y + jet.LifepodBox.Height
            <= 480f - JetStoresPanel.BottomInset + 0.5f);
        // The chat log yields the rows the block needs (it stays a scrolling window), and the
        // pod's own oxygen reading is printed once — the caution stack no longer repeats it.
        Assert.True(jet.ChatPane.Height < 8f + (12f + JetConsoleLayout.RowGap) * 9f);
        Assert.DoesNotContain(surface.Seen, t => t.StartsWith("POD OXY", StringComparison.Ordinal));
    }

    [Fact]
    public void The_compact_lifepod_form_keeps_two_rows_and_the_standard_form_three()
    {
        // P1040b — the stated compact contraction at 800x480: `LIFEPOD OXY ▮ nn s` on one row
        // and `RETURN ...` on the next; the standard frame keeps the native three rows.
        var compact = new Recorder();
        using var compactJet = Jet(compact);
        compactJet.Draw(PodModel(), Frame(800, 480));
        compactJet.DrawConsole(PodModel());
        float pitch = MathF.Max(14f, 12f + JetConsoleLayout.RowGap) * ScaleFor(480);
        Assert.Equal(2f * pitch, compactJet.LifepodBox.Height, 2);
        Assert.Contains("LIFEPOD", compact.Seen);
        Assert.Contains("OXY", compact.Seen);
        Assert.Contains("95 s", compact.Seen);
        Assert.Contains("RETURN", compact.Seen);

        var standard = new Recorder();
        using var standardJet = Jet(standard);
        standardJet.Draw(PodModel(), Frame(1920, 1080));
        standardJet.DrawConsole(PodModel());
        Assert.Equal(3f * (12f + JetConsoleLayout.RowGap), standardJet.LifepodBox.Height, 2);
    }

    [Fact]
    public void A_lifepod_that_cannot_be_drawn_leaves_POD_OXY_in_the_caution_stack()
    {
        // P1040b — the caution stack drops `POD OXY` only when the block was actually drawn.
        // At a frame whose column band cannot hold the block the panel is not drawn, so the
        // endurance stays in the caution stack: one display per reading, never zero.
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = HudModelAdapter.Create(PodFrame(), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            chat: Chat(new[] { "[TEAM] A: b" }, paneLines: 7),
            console: new HudConsoleText { SectorName = "Alystra" },
            podReturnInstruction: "Shift+Z / G AUTOPILOT TO BASE");
        jet.Draw(model, Frame(640, 240));
        Assert.False(jet.LifepodDrawable);
        Assert.Equal(0f, jet.LifepodBox.Height);
        Assert.DoesNotContain("LIFEPOD", surface.Seen);
        Assert.Contains(surface.Seen, t => t.StartsWith("POD OXY", StringComparison.Ordinal));
    }

    [Fact]
    public void The_vote_banner_is_drawn_whenever_the_model_carries_a_ballot()
    {
        // P1040b — the same "one display per reading, never zero" rule: the caution stack drops
        // its Vote line unconditionally, so the banner must be committed at both resolutions
        // and with a full caution stack around it.
        const string vote = "Vote: Mutiny against the commander? (Y/N)";
        foreach ((int w, int h) in new[] { (800, 480), (1920, 1080) })
        {
            var surface = new Recorder();
            using var jet = Jet(surface);
            HudModel model = Model(Chat(new[] { "[TEAM] A: b" }, paneLines: 7),
                console: new HudConsoleText
                {
                    SectorName = "Alystra", VoteText = vote,
                    CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
                });
            jet.Draw(model, Frame(w, h));
            Assert.True(jet.VoteBox.Height > 0f, $"no vote banner at {w}x{h}");
            Assert.Contains(surface.Seen, t => t.StartsWith("Vote", StringComparison.Ordinal));
            Assert.DoesNotContain(surface.Seen, t => t.StartsWith("VOTE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_lifepod_panel_prints_the_shared_oxygen_fraction_and_endurance_once()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = PodModel();
        jet.Draw(model, Frame(1920, 1080));
        Assert.Contains("LIFEPOD", surface.Seen);
        Assert.Contains("OXY", surface.Seen);
        Assert.Contains("95 s", surface.Seen);
        Assert.Contains("RETURN", surface.Seen);
        Assert.True(jet.LifepodBox.Height > 0f);
        // The caution panel no longer repeats the same OXY reading: one display per value.
        Assert.DoesNotContain(surface.Seen, t => t.StartsWith("POD OXY", StringComparison.Ordinal));
    }

    // ── tips, vote ───────────────────────────────────────────────────────
    [Fact]
    public void Three_ordinary_tip_slots_are_drawn_from_the_shared_slots()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string[] tips = { "Welcome to the sector", "Alystra: 3 miners", "Payday: $500" };
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { OrdinaryTips = tips, SideColor = new HudColor(.07f, .95f, .84f) });
        jet.Draw(model, Frame(800, 480));
        jet.DrawConsole(model);
        foreach (string tip in tips) Assert.Contains(tip, surface.Seen);
    }

    [Fact]
    public void The_bottom_lines_use_the_jet_scale_and_keep_their_reading()
    {
        // P1040b defect 3: at 800x480 the two shared lines were drawn at the raw 1.0 scale
        // (larger than the whole Jet HUD) and cut to `WEAPONS: G...` / `CARGO 1/5 ...`.
        const string parts = "WEAPONS: GROUP  BOOST: ON  [1:Plasma]";
        const string cargo = "CARGO 1/5  Ammo pack x3";
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            chat: Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { SectorName = "Alystra" });
        // The fixture needs the two lines: the model adapter carries them through `Context`.
        HudModel withLines = WithBottomLines(model, cargo, parts);
        jet.Draw(withLines, Frame(800, 480));
        jet.DrawConsole(withLines);
        Assert.Contains(parts, surface.Seen);
        Assert.Contains(cargo, surface.Seen);
        // The Jet scale, not 1.0: the run's measured height is the density-scaled line height.
        float scale = ScaleFor(480);
        TextRun run = surface.RunsOf(cargo)[0];
        Assert.Equal(12f * scale, run.Height, 2);
        // Both rows end inside the viewport gutter, below the stores block.
        Assert.True(run.X + run.Width <= 800f - JetReadoutPlacement.Gutter * scale + 0.5f);
        Assert.True(jet.CargoBox.Height > 0f && jet.FlightPartsBox.Height > 0f);
    }

    [Fact]
    public void A_tip_too_long_for_the_band_wraps_onto_a_second_row()
    {
        // P1040b — a tip gets the bottom band (not the console column's width) and a tip that
        // still does not fit wraps instead of being cut to `Welcome to the se...`.
        string longTip = "Payday: five hundred credits deposited"
            + string.Concat(Enumerable.Repeat(" and one hundred more", 16))
            + " unless the sector is secure";
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { SectorName = "Alystra", OrdinaryTips = new[] { longTip } });
        jet.Draw(model, Frame(800, 480));
        jet.DrawConsole(model);
        // The tip is split at a word boundary: the whole string is never drawn as one cut line,
        // the first row starts with the tip's own first word and is a true prefix of it, and the
        // stack is two rows tall (the second row carries the rest).
        float textHeight = 12f * ScaleFor(480);
        Assert.DoesNotContain(longTip, surface.Seen);
        string head = surface.Seen.First(t => t.StartsWith("Payday:", StringComparison.Ordinal));
        Assert.True(head.Length < longTip.Length, "the tip was not split");
        Assert.StartsWith(head, longTip, StringComparison.Ordinal);
        Assert.True(jet.TipBlock.Height > textHeight + JetConsoleLayout.TipPitch * 0.5f);
    }

    [Fact]
    public void Every_drawn_command_cell_word_stays_inside_its_cell()
    {
        // P1040b defect 4: the wing name `DELTA` spilled out of its 54x42 combo cell over the
        // panel border. Every cell word is fitted in BOTH axes and the cell is still the native
        // rectangle the production hit test resolves.
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = CommandModel(1f, wing: 8, recipient: 3, verb: 4);
        jet.Draw(model, Frame(800, 480));
        jet.DrawCommand(model);
        RectangleF panel = jet.CommandPanel;
        Assert.True(panel.Width > 0f);
        for (int c = 0; c < JetCommandConsole.RecipientCount; c++)
            AssertInside(surface, panel, JetCommandConsole.RecipientCell(c), JetCommandConsole.RecipientWord(c));
        AssertInside(surface, panel, JetCommandConsole.WingCell, "WING");
        AssertInside(surface, panel, JetCommandConsole.WingCell, "DELTA");
        for (int c = 0; c < JetCommandConsole.VerbCount; c++)
            AssertInside(surface, panel, JetCommandConsole.VerbCell(c), JetCommandConsole.VerbWord(c));
    }

    private static void AssertInside(Recorder surface, RectangleF panel, RectangleF cell, string word)
    {
        var box = new RectangleF(panel.X + cell.X, panel.Y + cell.Y, cell.Width, cell.Height);
        TextRun? run = surface.RunsOf(word).FirstOrDefault(r =>
            r.X >= box.X - 2f && r.X <= box.X + box.Width && r.Y >= box.Y - 2f && r.Y <= box.Y + box.Height);
        Assert.True(run is not null, $"{word} was not drawn in its cell");
        TextRun text = run!.Value;
        Assert.True(text.X >= box.X - 1.5f && text.X + text.Width <= box.X + box.Width + 1.5f,
            $"{word} leaves its cell horizontally ({text.X:0.#}..{text.X + text.Width:0.#} vs {box.X:0.#}..{box.X + box.Width:0.#})");
        Assert.True(text.Y >= box.Y - 1.5f && text.Y + text.Height <= box.Y + box.Height + 1.5f,
            $"{word} leaves its cell vertically ({text.Y:0.#}..{text.Y + text.Height:0.#} vs {box.Y:0.#}..{box.Y + box.Height:0.#})");
    }

    /// <summary>P1040b — the same model with the two shared bottom lines set (the fixture's
    /// `HudContext` carries them verbatim).</summary>
    private static HudModel WithBottomLines(HudModel model, string cargo, string parts)
        => HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            chat: model.Context.Chat,
            console: model.Context.Console,
            cargoLine: cargo, flightPartsLine: parts);

    [Fact]
    public void The_vote_banner_is_centred_at_the_native_offset_and_fitted_to_the_free_span()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        string vote = "Vote: Mutiny against the commander? (Y/N)";
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }, paneLines: 7),
            console: new HudConsoleText { VoteText = vote });
        jet.Draw(model, Frame(800, 480));
        RectangleF box = jet.VoteBox;
        // The native offset (85 + 12·NumChatLines) is the preferred row; at 800x480 the
        // unscaled offset would reach the ladder's top rung, so the banner rides above the
        // ladder's own top edge instead (stated compact rule) — and never reaches the column.
        float s = ScaleFor(480);
        float spanDegrees = 480f < JetStoresPanel.CompactHeight
            ? JetFlightGeometry.CompactLadderHalfSpanDegrees : JetFlightGeometry.LadderHalfSpanDegrees;
        float ladderTop = 240f - spanDegrees * JetFlightGeometry.LadderPixelsPerDegree * s;
        Assert.Equal(MathF.Min(HudConsoleFormat.VoteTop(7), ladderTop - 18f * s - 4f * s), box.Y, 2);
        Assert.True(box.Y + box.Height <= ladderTop + 0.5f);
        // Its text is the shared ballot, fitted to the span it is given.
        Assert.Contains(surface.Seen, t => t.StartsWith("Vote", StringComparison.Ordinal));
        // It never reaches the chat/console column, and it is centred inside the span it took.
        float blocked = MathF.Max(jet.ChatPane.X + jet.ChatPane.Width,
            JetConsoleLayout.ColumnRight(800, s)) + JetConsoleLayout.ColumnGap(s);
        Assert.True(box.X >= blocked - 0.5f);
        Assert.True(box.X + box.Width <= 800f - 24f * s + 0.5f);
    }

    // ── training and roll ────────────────────────────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(22)]
    public void Training_callouts_use_the_shared_table_geometry_and_text(int overlay)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(Chat(new[] { "[TEAM] A: b" }), trainingOverlay: overlay);
        jet.Draw(model, Frame(1920, 1080));
        HudTrainingCallout.Callout callout = HudTrainingCallout.For(overlay)!.Value;
        Assert.Contains(callout.Description, surface.Seen);
        if (callout.Description2 is { } second) Assert.Contains(second, surface.Seen);
    }

    [Fact]
    public void No_training_callout_is_drawn_without_an_overlay()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        jet.Draw(Model(Chat(new[] { "[TEAM] A: b" })), Frame(1920, 1080));
        Assert.DoesNotContain(surface.Seen, t => t.Contains("ROLL LEFT", StringComparison.Ordinal)
            || t.Contains("SPEED (meters", StringComparison.Ordinal));
    }

    [Fact]
    public void The_controls_hint_keeps_a_slot_clear_of_the_ladder_the_chat_pane_and_every_element()
    {
        const string hint = "Shift+F1 opens the controls";
        // P1040b — the hint is a band label placed in the first CLEAR slot (below the tip
        // stack, above the chat pane's marker row, under the radar scope). P1040 kept the
        // shared unscaled anchor, which at 800x480 landed across the ladder's +20 rung and the
        // chat pane's right edge; the rule now measures every obstacle first.
        foreach ((int w, int h) in new[] { (800, 480), (1920, 1080) })
        {
            var surface = new Recorder();
            using var jet = Jet(surface);
            HudModel model = HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
                flightReference: Reference(0f, 0f, 45f),
                chat: Chat(new[] { "[TEAM] Pilot Alpha: defend the base" }),
                console: new HudConsoleText
                {
                    SectorName = "Alystra",
                    OrdinaryTips = new[] { "Welcome to the sector" },
                },
                controlHint: hint);
            jet.Draw(model, Frame(w, h));
            Assert.Contains(hint, surface.Seen);
            TextRun run = surface.RunsOf(hint)[0];
            Assert.True(jet.HintClear, $"{w}x{h}: the hint found no clear slot");
            Assert.InRange(jet.HintSlotIndex, 0, 2);
            // Clear of the chat pane, of every registered furniture box (the ladder numerals,
            // the tapes, the heading plate, the stores block, the radar scope) and on screen.
            RectangleF box = jet.HintBox;
            Assert.False(Overlaps(box, jet.ChatPane), $"hint over the chat pane at {w}x{h}");
            foreach (RectangleF furniture in jet.FurnitureBoxes)
                Assert.False(Overlaps(box, furniture), $"hint over furniture {furniture} at {w}x{h}");
            Assert.True(box.X >= 0f && box.X + box.Width <= w + 0.5f);
            Assert.True(box.Y >= 0f && box.Y + box.Height <= h + 0.5f);
            // The drawn run is the box the renderer reported.
            Assert.Equal(box.X, run.X, 2);
            Assert.Equal(box.Y, run.Y, 2);
        }
    }

    /// <summary>Do two boxes share more than a half pixel in each axis?</summary>
    private static bool Overlaps(RectangleF a, RectangleF b)
    {
        float w = MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X);
        float h = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
        return w > 0.5f && h > 0.5f;
    }

    [Fact]
    public void The_roll_pointer_follows_the_shared_attitude_and_its_validity()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel banked = HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
            flightReference: Reference(4f, -28f, 45f));
        jet.Draw(banked, Frame(1920, 1080));
        float scale = ScaleFor(1080);
        float tipY = 540f - JetFlightGeometry.LadderHalfSpanDegrees
            * JetFlightGeometry.LadderPixelsPerDegree * scale - 10f * scale;
        Assert.Contains(surface.Segments, s => MathF.Abs(s.B.Y - (tipY + 8f * scale)) < 1.5f);

        // No attitude → no pointer (nothing invented).
        var none = new Recorder();
        using var noAttitude = Jet(none);
        noAttitude.Draw(HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit),
            Frame(1920, 1080));
        Assert.DoesNotContain(none.Segments, s => MathF.Abs(s.B.Y - (tipY + 8f * scale)) < 1.5f);
    }

    // ── allocation ───────────────────────────────────────────────────────
    [Fact]
    public void The_console_composition_allocates_zero_bytes_per_frame()
    {
        var surface = new Counter();
        var noop = new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
        using var jet = Jet(surface, noop);
        string longLine = "[SECTOR] Pilot Charlie: " + new string('W', 200);
        HudModel model = Model(Chat(new[] { longLine, "[TEAM] Pilot: x" }, composing: true,
                state: NativeComposeState.Chat, offset: 2, filter: "TEAM", input: "returning to base"),
            console: new HudConsoleText
            {
                SectorName = "Alystra", CarrierName = "Pilot Alpha", BoardState = 2,
                AcceptedOrder = "defend Garrison", QueuedOrder = "attack Enemy fighter",
                VoteText = "Vote: Mutiny against the commander? (Y/N)",
                OrdinaryTips = new[] { "Welcome", "Alystra: 3 miners", "Payday: $500" },
                CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
                SideColor = new HudColor(.07f, .95f, .84f),
            }, trainingOverlay: 17, missionClock: 125, missionCountdown: false);
        var frame = Frame(1920, 1080);
        void Draw(int count)
        {
            for (int i = 0; i < count; i++)
            {
                jet.Draw(model, frame);
                jet.DrawConsole(model);
            }
        }
        Draw(2_000);                         // warm the fit/mission/endurance caches
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(20_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1040 Jet console: {bytes} B / 20000 frames = {bytes / 20000d} B/frame");
        Assert.Equal(0, bytes);
    }

    // ── training preference restore ──────────────────────────────────────
    /// <summary>
    /// P1040 — the pre-lesson snapshot rule (Game1.TrainingIntegration.cs:10-65 records the
    /// ACTIVE <see cref="HudSettings.Skin"/> at lesson start). A lesson started under Jet
    /// therefore restores Jet, even after a cycle inside the lesson. If <c>hud.jetSkin</c> is
    /// turned OFF during the lesson, Jet is no longer selectable, <c>Request("jet")</c> is
    /// refused (HudSkinSwitcher.cs:74-80) and the restore settles on Modern — the same fallback
    /// the switcher already applies when the gate turns off; no stale Jet renderer survives.
    /// </summary>
    [Fact]
    public void A_lesson_started_under_jet_restores_jet_even_after_a_cycle_inside_it()
    {
        using var h = new TrainingHarness(initial: HudSkinIds.Jet, jetEnabled: true);
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);
        h.Call("SaveTrainingPreferences");
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);

        // The lesson runs; the pilot may switch skin inside it.
        h.Switcher.Cycle();
        h.Call("CommitHudSkinChange");
        Assert.NotEqual(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);

        h.Call("ReleaseTraining");
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);
        Assert.Equal(HudSkinIds.Jet, h.Settings.Skin);
        Assert.Equal(HudSkinIds.Jet, HudSettings.Load(h.HudPath, true).Skin);
    }

    [Fact]
    public void A_lesson_started_under_jet_restores_modern_when_the_flag_went_off_meanwhile()
    {
        using var h = new TrainingHarness(initial: HudSkinIds.Jet, jetEnabled: true);
        h.Call("SaveTrainingPreferences");
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);

        // hud.jetSkin is switched off while the lesson runs: the switcher queues Modern
        // (HudSkinSwitcher.ObserveJetAvailability), and the restore cannot get Jet back.
        h.SetJetEnabled(false);
        h.Switcher.ObserveJetAvailability(false);
        h.Call("CommitHudSkinChange");
        Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Renderer.Id);

        h.Call("ReleaseTraining");
        Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Renderer.Id);
        Assert.Equal(HudSkinIds.Modern, h.Settings.Skin);
        Assert.Equal(HudSkinIds.Modern, HudSettings.Load(h.HudPath, true).Skin);
    }

    // ── fixtures ─────────────────────────────────────────────────────────
    private static HudChat Chat(string[] lines, int offset = 0, string? filter = null,
        bool[]? leaders = null, int paneLines = ChatPaneLayout.DefaultLines, bool composing = false,
        NativeComposeState state = NativeComposeState.NotComposing, string input = "returning",
        float progress = 1f)
        => new(lines, offset, input, composing, 1, "[TEAM] ", filter, state, progress,
            new HudColor(.07f, .95f, .84f),
            lines.Select(_ => (HudColor?)new HudColor(.07f, .95f, .84f)).ToArray(),
            leaders ?? lines.Select(_ => false).ToArray(), paneLines);

    private static HudModel Model(HudChat chat, HudConsoleText? console = null,
        int trainingOverlay = 0, int? missionClock = null, bool missionCountdown = false)
        => HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f), chat: chat,
            console: console ?? new HudConsoleText { SectorName = "Alystra" },
            trainingOverlayId: trainingOverlay,
            missionClockSeconds: missionClock, missionCountdown: missionCountdown);

    private static HudModel CommandModel(float progress, int wing = 0, int recipient = 0, int verb = 0,
        string[]? lines = null)
        => HudModelAdapter.Create(Frame(260f, .6f, 1920, 1080), CameraMode.Command, commandView: true,
            flightReference: Reference(0f, 0f, 45f),
            chat: Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { SectorName = "Alystra" },
            commandPaneSlide: progress, commandWing: wing, commandRecipientColumn: recipient, commandVerb: verb,
            commandLines: lines ?? new[] { "COMMAND SHIP: Scout" });

    /// <summary>The shared telemetry of an adrift lifepod (the model marks it `PodVisible`).</summary>
    private static HudFrame PodFrame() => Frame(60f, 0f, 1920, 1080) with
    {
        IsAdriftLifepod = true, OxygenSecondsRemaining = 95, FuelFraction = .63f,
        AmmoFraction = 0f, EnergyFraction = 0f,
    };

    private static HudModel PodModel()
        => HudModelAdapter.Create(PodFrame(), CameraMode.Cockpit, flightReference: Reference(0f, 0f, 45f),
            chat: Chat(new[] { "[TEAM] A: b" }),
            console: new HudConsoleText { SectorName = "Alystra" },
            podReturnInstruction: "Shift+Z / G AUTOPILOT TO BASE");

    private static HudFrame Frame(float speed, float throttle, int width, int height) => new(
        .8f, .6f, .4f, .3f, .2f, 1f, speed, throttle, false, false, true, 0,
        false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    private static HudDrawFrame Frame(int width, int height)
        => new(new Viewport(0, 0, width, height), null, false);

    private static float ScaleFor(int height) => ModernHudScale.Core(1f, 1f, height);

    private static HudFlightReference Reference(float pitchDegrees, float rollDegrees, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float roll = rollDegrees * MathF.PI / 180f;
        var forward = new Allegiance.Core.Collision.Vector3(MathF.Cos(heading) * MathF.Cos(pitch),
            MathF.Sin(heading) * MathF.Cos(pitch), MathF.Sin(pitch));
        var levelUp = new Allegiance.Core.Collision.Vector3(-MathF.Cos(heading) * MathF.Sin(pitch),
            -MathF.Sin(heading) * MathF.Sin(pitch), MathF.Cos(pitch));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (roll != 0f) orientation.PostRotate(forward, roll);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinResources? resources = null)
        => new(surface, new HudSettings { Skin = HudSkinIds.Jet, Size = 1f },
            resources ?? NoopResources, HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!);

    private static HudSkinResources NoopResources { get; } = new(
        _ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });

    public readonly record struct TextRun(string Text, float X, float Y, float Width, float Height);

    private sealed class Recorder : IJetVectorSurface
    {
        private readonly List<TextRun> _runs = new();
        private readonly List<(Vector2 A, Vector2 B)> _segments = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _large = new(StringComparer.Ordinal);

        public IReadOnlySet<string> Seen => _seen;
        public IReadOnlySet<string> LargeRuns => _large;
        public IReadOnlyList<(Vector2 A, Vector2 B)> Segments => _segments;

        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) => _segments.Add((a, b));
        public void Fill(Vector2 min, Vector2 size, Color color) { }

        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Vector2 size = Measure(text, large, scale);
            _seen.Add(text);
            if (large) _large.Add(text);
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                TextRun run = _runs[i];
                if (run.Text == text && MathF.Abs(run.X - position.X) <= 2.5f && MathF.Abs(run.Y - position.Y) <= 2.5f)
                {
                    _runs[i] = new TextRun(text, position.X, position.Y, size.X, size.Y);
                    return;
                }
            }
            _runs.Add(new TextRun(text, position.X, position.Y, size.X, size.Y));
        }

        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);

        public IReadOnlyList<TextRun> RunsOf(string text) => _runs.FindAll(r => r.Text == text);
    }

    /// <summary>A Game1 with a real (recording-surface) Jet renderer installed, so the training
    /// snapshot/restore path runs end to end through its own code.</summary>
    private sealed class TrainingHarness : IDisposable
    {
        private readonly string? _oldHud = Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS");
        private readonly string? _oldGraphics = Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "p1040-" + Guid.NewGuid().ToString("N"));
        private readonly ConnectionSession _session = new();
        // SaveTrainingPreferences/ReleaseTraining mutate the process-wide KeyBindingStore
        // (ResetToDefaults + ApplyJson), so the harness restores it like the P988 harness does;
        // otherwise a parallel test in the same assembly can observe a reset keymap.
        private readonly string _savedMap = KeyBindingStore.Instance.Map.ToJson();
        private readonly InputLayout _savedLayout = KeyBindingStore.Instance.Map.Layout;
        private readonly Func<string, bool>? _savedFilter = KeyBindingStore.Instance.Map.ActionFilter;
        private bool _jetEnabled;
        public string HudPath => Path.Combine(_dir, "hud.json");
        public Game1 Game { get; }
        public HudSettings Settings { get; }
        public HudSkinSwitcher Switcher { get; }

        public TrainingHarness(string initial, bool jetEnabled)
        {
            _jetEnabled = jetEnabled;
            Game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
            GC.SuppressFinalize(Game);
            Settings = new() { Skin = initial };
            // The switcher disposes the installed renderers at teardown; the Jet one owns only
            // its borrowed surface, so no graphics device is needed.
            var jet = Jet(new Recorder(), NoopResources);
            Switcher = new(Settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern), jet,
                jetEnabled: () => _jetEnabled);
            Set("_hudSettings", Settings);
            Set("_hudSkins", Switcher);
            Set("_pilotProfile", new PilotInfoLayout.ProfileEditor());
            Set("_flightGc", new FlightGcLatency(false));
            Set("_upscaling", new UpscalingSettings());
            Set("_resolutionController", new DynamicResolutionController());
            var state = typeof(Game1).GetField("_state", Hidden)!;
            state.SetValue(Game, Enum.Parse(state.FieldType, "Flying"));
            Directory.CreateDirectory(_dir);
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", HudPath);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", Path.Combine(_dir, "graphics.json"));
            Set("_session", _session);
            Set("_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        }

        public void SetJetEnabled(bool enabled) => _jetEnabled = enabled;
        public void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(Game, value);
        public object? Call(string name, params object[] args)
            => typeof(Game1).GetMethod(name, Hidden)!.Invoke(Game, args);

        public void Dispose()
        {
            Switcher.Dispose();
            _session.Dispose();
            KeyBindingStore.Instance.Map.SetLayout(_savedLayout);
            KeyBindingStore.Instance.Map.ResetToDefaults();
            KeyBindingStore.Instance.Map.ApplyJson(_savedMap);
            KeyBindingStore.Instance.Map.ActionFilter = _savedFilter;
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", _oldHud);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", _oldGraphics);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private sealed class Stub(string id) : IHudSkinRenderer
        {
            public string Id { get; } = id;
            public IReadOnlySet<string>? Coverage => null;
            public void Draw(HudModel model, in HudDrawFrame frame) { }
            public void DrawCommand(HudModel model) { }
            public void DrawConsole(HudModel model) { }
            public void SetRadarIcons(RadarIconTextures? icons) { }
            public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => default;
            public void Dispose() { }
        }
    }

    private sealed class Counter : IJetVectorSurface
    {
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) { }
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);
    }
}
