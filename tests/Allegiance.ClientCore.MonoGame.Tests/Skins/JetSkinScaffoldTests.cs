using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1035 — Jet story 1/9 scaffold. Pins the N-skin registry (Classic, Modern, Jet
/// in install order), the <c>hud.jetSkin</c> gate (default OFF, Jet skipped by the
/// cycle and refused by Request while off, runtime fallback to Modern), the Jet
/// definition's palette validation (all-or-nothing) and the Jet renderer's
/// allocation and draw-smoke contract.
/// <para>Everything here is headless: the Jet renderer draws through a counting
/// <see cref="IJetVectorSurface"/>, so geometry, smoke and allocation gates run
/// without a graphics device (the real-window capture is the report's PNG pair).</para>
/// </summary>
public sealed class JetSkinScaffoldTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Microsoft.Xna.Framework.Input.KeyboardState CycleKeys =
        new(Microsoft.Xna.Framework.Input.Keys.LeftShift, Microsoft.Xna.Framework.Input.Keys.F12);

    // ── definition / palette ─────────────────────────────────────────────
    [Fact]
    public void Built_in_jet_definition_carries_the_P972_green_palette_and_modern_defaults()
    {
        var definition = HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet);
        Assert.NotNull(definition);
        Assert.Equal("Jet", definition!.Name);
        Assert.NotNull(definition.Palette);
        Assert.Equal("#79FFC0", definition.Palette!.Primary);
        Assert.Equal("#3DA87B", definition.Palette.Dim);
        Assert.Equal("#C9FFE3", definition.Palette.Hot);
        Assert.True(HudSkinColor.TryParseHex(definition.Palette.Plate, out var plate));
        Assert.True(plate.R < 40 && plate.G < 48 && plate.B < 40);           // near-black green
        Assert.True(HudSkinColor.TryParseHex(definition.Palette.Keyline, out var keyline));
        Assert.True(keyline.R < 24 && keyline.G < 24 && keyline.B < 24);     // near-black
        // Jet follows Modern's defaults: selection bracket/declutter on, Tactical
        // level (and Style maps to Modern, so the `0` level cycle stays available).
        Assert.True(definition.Defaults.SelectionBracket);
        Assert.True(definition.Defaults.RadarDeclutter);
        Assert.Equal(HudLevel.Tactical, definition.Defaults.Level);
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        Assert.Equal(HudStyle.Modern, settings.Style);
        Assert.True(settings.SelectionBracketOn);
        Assert.True(settings.RadarDeclutterOn);
    }

    [Fact]
    public void Classic_and_modern_definitions_carry_no_palette_block()
    {
        Assert.Null(HudSkinResolver.Catalog.TryGet(HudSkinIds.Classic)!.Palette);
        Assert.Null(HudSkinResolver.Catalog.TryGet(HudSkinIds.Modern)!.Palette);
    }

    [Theory]
    [InlineData("Primary")]
    [InlineData("Dim")]
    [InlineData("Hot")]
    [InlineData("Plate")]
    [InlineData("Keyline")]
    public void A_bad_palette_colour_rejects_the_whole_definition_and_keeps_the_prior_skin(string role)
    {
        var catalog = new HudSkinCatalog();
        var good = JetJson();
        Assert.True(catalog.TryAdd(good));
        string before = catalog.TryGet(HudSkinIds.Jet)!.Palette!.Primary;
        string bad = JetJson(overrides: role + ": \"green\"");
        Assert.Throws<HudSkinLoadException>(() => HudSkinLoader.Load(bad));
        Assert.False(catalog.TryAdd(bad));
        Assert.Equal(before, catalog.TryGet(HudSkinIds.Jet)!.Palette!.Primary);   // prior stays
        // The definition is rejected in FULL: it must not half-load with defaults.
        Assert.Equal("#79FFC0", catalog.TryGet(HudSkinIds.Jet)!.Palette!.Primary);
        Assert.Equal("#3DA87B", catalog.TryGet(HudSkinIds.Jet)!.Palette!.Dim);
    }

    [Fact]
    public void A_palette_missing_one_role_is_rejected_all_or_nothing()
    {
        string json = JetJson(overrides: "Dim: \"\"");
        var exception = Assert.Throws<HudSkinLoadException>(() => HudSkinLoader.Load(json));
        Assert.Contains("Palette.Dim", exception.Message);
        // No palette block at all stays valid (Classic/Modern packaging).
        Assert.NotNull(HudSkinLoader.Load(ClassicJson()));
    }

    [Theory]
    [InlineData("#79FFC0", 0x79, 0xFF, 0xC0)]
    [InlineData("#3da87b", 0x3D, 0xA8, 0x7B)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#ffffff", 0xFF, 0xFF, 0xFF)]
    public void Palette_hex_parser_accepts_six_digit_hex(string text, int r, int g, int b)
    {
        Assert.True(HudSkinColor.TryParseHex(text, out var color));
        Assert.Equal(new HudSkinColor((byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("79FFC0")]
    [InlineData("#79FC0")]      // five digits
    [InlineData("#79FFC0FF")]   // eight digits
    [InlineData("green")]
    [InlineData("#79FFCG")]
    public void Palette_hex_parser_rejects_everything_else(string? text)
        => Assert.False(HudSkinColor.TryParseHex(text, out _));

    [Fact]
    public void The_jet_flag_is_a_visual_toggle_that_starts_off()
    {
        var definition = VisualFlags.Find(VisualFlags.JetSkinKey);
        Assert.NotNull(definition);
        Assert.Equal("Jet HUD", definition!.Label);
        Assert.Equal("military vector HUD — in development", definition.Description);
        Assert.Equal(VisualFlagKind.Toggle, definition.Kind);
        Assert.False(definition.DefaultOn);
        Assert.False(new VisualOptions().JetSkin);
        var on = VisualOptions.Parse("hud.jetSkin=on", out var warnings);
        Assert.Empty(warnings);
        Assert.True(on.JetSkin);
    }

    // ── N-skin cycle ─────────────────────────────────────────────────────
    [Fact]
    public void Flag_on_cycles_classic_modern_jet_and_wraps_both_ways()
    {
        using var h = new RegistryHarness(jetEnabled: true, initial: HudSkinIds.Classic);
        Assert.Equal(new[] { HudSkinIds.Classic, HudSkinIds.Modern, HudSkinIds.Jet },
            h.Switcher.Installed.Select(s => s.Definition.Id));
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Definition.Id);
        h.Switcher.Cycle(); Assert.Equal(HudSkinIds.Modern, h.Next());
        h.Switcher.Cycle(); Assert.Equal(HudSkinIds.Jet, h.Next());
        h.Switcher.Cycle(); Assert.Equal(HudSkinIds.Classic, h.Next());   // wrap forward
        h.Switcher.Cycle(-1); Assert.Equal(HudSkinIds.Jet, h.Next());     // wrap backward
        h.Switcher.Cycle(-1); Assert.Equal(HudSkinIds.Modern, h.Next());
        h.Switcher.Cycle(-1); Assert.Equal(HudSkinIds.Classic, h.Next());
    }

    [Fact]
    public void Flag_off_keeps_exactly_the_old_two_skin_cycle_and_refuses_jet()
    {
        using var h = new RegistryHarness(jetEnabled: false, initial: HudSkinIds.Classic);
        Assert.False(h.Switcher.Request(HudSkinIds.Jet));
        Assert.False(h.Switcher.Request(" JET "));
        Assert.Null(h.Next());
        for (int i = 0; i < 6; i++)
        {
            h.Switcher.Cycle();
            Assert.NotEqual(HudSkinIds.Jet, h.Next());
        }
        Assert.Equal(3, h.Switcher.Installed.Count);   // Jet installed, skipped by the cycle
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Definition.Id);
        Assert.True(h.Switcher.Request(HudSkinIds.Modern));
        Assert.Equal(HudSkinIds.Modern, h.Next());
    }

    [Fact]
    public void Flag_on_accepts_request_jet_and_applies_it()
    {
        using var h = new RegistryHarness(jetEnabled: true, initial: HudSkinIds.Classic);
        Assert.True(h.Switcher.Request("JET"));
        Assert.Equal(HudSkinIds.Classic, h.Switcher.Active.Definition.Id);   // atomic
        Assert.True(h.Switcher.ApplyPending());
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Definition.Id);
        Assert.Equal(HudSkinIds.Jet, h.Settings.Skin);
        Assert.Contains("Jet", Game1.HudStyleRowLabel(h.Switcher.Active.Definition.Name));
    }

    // ── runtime gate fallback + persistence ──────────────────────────────
    [Fact]
    public void Turning_the_flag_off_while_jet_is_active_falls_back_to_modern_at_the_boundary()
    {
        using var h = new RegistryHarness(jetEnabled: true, initial: HudSkinIds.Jet);
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Definition.Id);
        h.SetJetEnabled(false);
        h.Switcher.ObserveJetAvailability(false);
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Definition.Id);      // still atomic
        Assert.True(h.Switcher.ApplyPending());
        Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Definition.Id);
        Assert.Equal(HudSkinIds.Modern, h.Settings.Skin);
        // Idempotent: a second observation at the same value queues nothing.
        h.Switcher.ObserveJetAvailability(false);
        Assert.False(h.Switcher.ApplyPending());
    }

    [Fact]
    public void Turning_the_flag_off_while_jet_is_only_queued_never_activates_it()
    {
        using var h = new RegistryHarness(jetEnabled: true, initial: HudSkinIds.Modern);
        h.Switcher.Cycle();                                                   // queues Jet
        Assert.Equal(HudSkinIds.Jet, h.Next());
        h.SetJetEnabled(false);
        h.Switcher.ObserveJetAvailability(false);
        h.Switcher.ApplyPending();
        Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Definition.Id);
    }

    [Fact]
    public void Persisted_jet_survives_restart_when_the_flag_is_on()
    {
        string path = TempHudPath();
        try
        {
            var saved = new HudSettings { Skin = HudSkinIds.Jet };
            saved.Save(path);
            var loaded = HudSettings.Load(path, true);
            Assert.Equal(HudSkinIds.Jet, loaded.Skin);
            using var h = new RegistryHarness(jetEnabled: true, settings: loaded);
            Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Definition.Id);
            Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Persisted_jet_displays_modern_with_the_flag_off_without_rewriting_the_saved_value()
    {
        string path = TempHudPath();
        try
        {
            var saved = new HudSettings { Skin = HudSkinIds.Jet };
            saved.Save(path);
            var loaded = HudSettings.Load(path, true);
            Assert.Equal(HudSkinIds.Jet, loaded.Skin);        // the file's value is kept
            using var h = new RegistryHarness(jetEnabled: false, settings: loaded);
            Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Definition.Id);
            Assert.Equal(HudSkinIds.Modern, h.Switcher.Active.Renderer.Id);
            Assert.Equal(HudSkinIds.Jet, loaded.Skin);        // not rewritten in memory…
            loaded.Save(path);
            Assert.Contains("\"Skin\": \"jet\"", File.ReadAllText(path));   // …nor on disk
            Assert.False(h.Switcher.Request(HudSkinIds.Jet));
            // Modern's option defaults apply while the gate hides Jet.
            Assert.True(loaded.SelectionBracketOn);
            Assert.Equal(HudStyle.Modern, loaded.Style);
        }
        finally { File.Delete(path); }
    }

    // ── allocation ───────────────────────────────────────────────────────
    [Fact]
    public void Selection_and_dispatch_allocate_zero_bytes_per_frame_with_three_skins()
    {
        using var h = new RegistryHarness(jetEnabled: true, initial: HudSkinIds.Classic);
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        typeof(Game1).GetField("_hudSkins", Hidden)!.SetValue(game, h.Switcher);
        // The fields UpdateHudSkinInput reads outside the gate list (the same
        // minimum Harness set HudSkinSwitchingTests uses).
        typeof(Game1).GetField("_pilotProfile", Hidden)!.SetValue(game, new PilotInfoLayout.ProfileEditor());
        typeof(Game1).GetField("_flightGc", Hidden)!.SetValue(game, new FlightGcLatency(false));
        typeof(Game1).GetField("_upscaling", Hidden)!.SetValue(game, new UpscalingSettings());
        typeof(Game1).GetField("_resolutionController", Hidden)!.SetValue(game, new DynamicResolutionController());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        var frame = new HudDrawFrame(new Viewport(0, 0, 800, 480), null, false);
        var model = HudModelAdapter.Create(null, CameraMode.Command);
        var select = typeof(Game1).GetProperty("ActiveHud", Hidden)!.GetMethod!
            .CreateDelegate<Func<Game1, IHudSkinRenderer>>();
        var input = typeof(Game1).GetMethod("UpdateHudSkinInput", Hidden)!
            .CreateDelegate<Action<Game1, Microsoft.Xna.Framework.Input.KeyboardState, bool>>();
        void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                input(game, (i & 3) == 0 ? CycleKeys : default, true);
                h.Switcher.ApplyPending();
                var active = select(game);
                active.Draw(model, frame);
                active.DrawCommand(model);
                active.DrawConsole(model);
            }
        }
        Frames(10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Frames(100_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1035 3-skin input/selection/dispatch: {bytes} B / 100000 frames = {bytes / 100000d} B/frame");
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Jet_draw_allocates_zero_bytes_per_frame_on_a_fixed_model()
    {
        var surface = new CountingJetSurface();
        var noop = new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
        using var jet = Jet(surface, noop);
        var model = HudModelAdapter.Create(FlightFrame(speed: 123f, throttle: .5f), CameraMode.Cockpit,
            contacts: Contacts(), velocityMarker: new(900, 500), trainingOverlayId: 2,
            notices: new[] { new HudNotice("TIP: welcome", false, 1f) });
        var frame = new HudDrawFrame(new Viewport(0, 0, 1920, 1080), null, true);
        void Draw(int count)
        {
            for (int i = 0; i < count; i++) jet.Draw(model, frame);
        }
        // P1049b — fixed inputs repeat a one-frame steady state; cache capacity
        // is not a frame cycle. Shared window and source audit: JetAllocationWindow.
        Draw(JetAllocationWindow.WarmupFrames);
        Assert.True(surface.Lines > 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(JetAllocationWindow.MeasuredFrames);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1035 JetHudRenderer.Draw: {bytes} B / {JetAllocationWindow.MeasuredFrames} frames = {bytes / (double)JetAllocationWindow.MeasuredFrames} B/frame");
        Assert.Equal(0, bytes);
    }

    // ── draw smoke, every HudModel state ─────────────────────────────────
    public static TheoryData<string> States => new()
    {
        "cockpit", "chase", "command", "docked", "lifepod", "dead", "ghost", "turret", "training",
        "hidden", "no-telemetry", "empty-telemetry", "notices",
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Jet_draws_every_HudModel_state_without_throwing_or_inventing_values(string state)
    {
        var surface = new CountingJetSurface();
        var resources = new CountingResources();
        using var jet = Jet(surface, resources.Resources);
        var model = State(state);
        var frame = new HudDrawFrame(new Viewport(0, 0, 1280, 720), null, state == "turret");
        jet.Draw(model, frame);
        jet.DrawCommand(model);
        jet.DrawConsole(model);
        // The shared passes: radar stays Jet's own (P1039), tags and pod/death are the
        // shared host passes, and P1040 moved chat/cargo/flight-parts and the command
        // composition into the skin — the host now supplies only the command-camera chrome.
        Assert.Equal(0, resources.Radar);
        Assert.Equal(1, resources.Tags);
        Assert.Equal(1, resources.PodDeath);
        Assert.Equal(0, resources.Chat);
        Assert.Equal(0, resources.Cargo);
        Assert.Equal(0, resources.Parts);
        Assert.Equal(0, resources.Command);
        Assert.Equal(1, resources.Chrome);
        Assert.False(resources.TagsWereModern);
        // Nothing is drawn while the HUD is hidden; the WIP tag otherwise.
        if (state == "hidden")
        {
            Assert.Equal(0, surface.Fills + surface.Lines + surface.Texts);
            Assert.Empty(surface.Seen);
        }
        else
        {
            bool instruments = model.InstrumentsVisible && model.Telemetry is not null;
            if (instruments) Assert.True(surface.Fills > 0, "Jet draws a plate per instrument state");
            Assert.Contains(JetHudRenderer.TagText, surface.Seen);
            // Speed/throttle only when the flight telemetry gates allow them; never
            // a fabricated zero for a state the model says has no ship.
            Assert.Equal(instruments, surface.Seen.Contains("SPD / MPS"));
            Assert.Equal(instruments, surface.Seen.Contains("THR"));
            if (instruments && model.Telemetry is { } own)
            {
                Assert.Contains(((int)MathF.Round(own.Speed)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    surface.Seen);
                int percent = (int)MathF.Round((Math.Clamp(own.Throttle, -1f, 1f) + 1f) * 0.5f * 100f);
                Assert.Contains(percent + "%", surface.Seen);
            }
        }
        // Coverage is the elements Jet itself draws (story 2 adds the flight-path
        // marker and the ladder's roll reference; P1037 adds the whole target, lock,
        // lead/aim and order-steering families; P1038 adds the stores block, the
        // master-caution panel and the F4 inventory pane), not a native claim.
        Assert.Equal(new[]
        {
            "Accepted and current order turn arrows", "Aim hit-probability predictor",
            "Ammunition reserve", "Autopilot status", "Ballistic lead marker",
            "Boundary and crowd warnings", "Carried artifact", "Carried flag",
            "Chase weapon aim without target", "Cloak active", "Cloak strength and drained state",
            "Cockpit centre reticle", "Critical tip text lines", "Damage flash timer",
            "Death banner", "Detection eye", "Energy reserve",
            "Equipment afterburner endurance", "Equipment afterburner top speed",
            "Equipment burst rate", "Equipment cloak endurance", "Equipment empty slot affordances",
            "Equipment mounting progress", "Equipment part identity and key",
            "Equipment quantity and empty warning", "Equipment selected and active state",
            "Equipment shield recharge seconds", "Equipment shield strength",
            "Equipment shot damage", "Equipment weapon range", "Flight equipment inventory pane",
            "Fuel reserve", "Hull integrity", "Incoming missile warning", "Inventory cargo slots",
            "Inventory own ship name", "Inventory turret gunner identity", "Kill bonus",
            "Launcher arming progress", "Lifepod oxygen endurance", "Lives remaining",
            "Missile lock flower", "Mission kills", "Roll tutorial cue", "Selected target bracket",
            "Shield charge", "Signature percentage", "Speed",
            "Target hull", "Target in weapon range", "Target kill bonus", "Target name",
            "Target ore", "Target range or sector", "Target shield", "Target side identity",
            "Target speed", "Target time to intercept", "Target type description",
            "Thrust setting", "Vector lock status", "Velocity direction marker", "Vote banner",
            "Ripcord countdown",
            // P1040 — the console/chat/command/lifecycle/training family.
            "Accepted order text", "Chat composition and recipient", "Chat history",
            "Command recipient verb and wing controls", "Command view ship summary",
            "Eject pod identity panel", "Mission countdown", "Queued order text",
            "Sector location", "Tip text lines", "Training HUD callouts", "Turret carrier identity",
        }.OrderBy(id => id, StringComparer.Ordinal), jet.Coverage!.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Jet_coverage_matches_the_elements_the_scaffold_draws()
    {
        using var jet = Jet(new CountingJetSurface());
        Assert.Equal(76, jet.Coverage!.Count);
        Assert.Contains("Cockpit centre reticle", jet.Coverage);
        Assert.Contains("Speed", jet.Coverage);
        Assert.Contains("Thrust setting", jet.Coverage);
        // P1036 — the flight-path marker and the ladder's roll reference.
        Assert.Contains("Velocity direction marker", jet.Coverage);
        Assert.Contains("Roll tutorial cue", jet.Coverage);
        // P1037 — target designator/pane readouts, lock, lead/aim/chase, order arrows.
        Assert.Contains("Selected target bracket", jet.Coverage);
        Assert.Contains("Missile lock flower", jet.Coverage);
        Assert.Contains("Ballistic lead marker", jet.Coverage);
        Assert.Contains("Aim hit-probability predictor", jet.Coverage);
        Assert.Contains("Chase weapon aim without target", jet.Coverage);
        Assert.Contains("Accepted and current order turn arrows", jet.Coverage);
        // P1038 — the stores block, the caution panel and the F4 inventory pane.
        Assert.Contains("Hull integrity", jet.Coverage);
        Assert.Contains("Ammunition reserve", jet.Coverage);
        Assert.Contains("Damage flash timer", jet.Coverage);
        Assert.Contains("Flight equipment inventory pane", jet.Coverage);
        Assert.Contains("Equipment weapon range", jet.Coverage);
        Assert.Equal(HudSkinIds.Jet, jet.Id);
    }

    [Fact]
    public void Jet_console_pass_draws_its_own_chat_and_command_composition()
    {
        var surface = new CountingJetSurface();
        var resources = new CountingResources();
        using var jet = Jet(surface, resources.Resources);
        var model = HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit,
            chat: new HudChat(new[] { "[TEAM] Pilot: defend the base" }, 0, "returning", true, 1, "[TEAM] "),
            console: new HudConsoleText { SectorName = "Alystra" });
        // P1040 — the skin owns chat/cargo/flight-parts and the command composition: the host
        // supplies neither the Classic chat pass nor the command bar/summary any more.
        jet.DrawConsole(model);
        Assert.Empty(resources.Order);
        Assert.True(surface.Texts > 0);
        Assert.Contains("[TEAM] Pilot: defend the base", surface.Seen);
        resources.Order.Clear();
        jet.DrawCommand(model);
        Assert.Equal(new[] { "chrome" }, resources.Order);   // camera chrome only
        Assert.False(resources.CommandWasClassicComposition);
        resources.Order.Clear();
        jet.Draw(model, new(new(0, 0, 800, 480), null, false));
        // P1039 — the shared Classic radar pass is gone from the Jet composition: Jet draws
        // its own scope inside the HUD pass, so the order is tags → its own pass → pod/death.
        Assert.Equal(new[] { "tags", "pod/death" }, resources.Order);
        Assert.False(resources.TagsWereModern);
    }

    [Fact]
    public void Jet_vector_primitives_paint_a_black_under_stroke_and_never_go_below_one_pixel()
    {
        var surface = new CountingJetSurface();
        var painter = new JetVectorPainter(surface, Color.Red, Color.Yellow, Color.White, Color.Black, Color.Black);
        painter.Scale = .25f;   // would be 0.375 px unscaled
        Assert.Equal(JetVectorPainter.MinimumLineStroke, painter.Stroke);   // P1036 ink floor
        Assert.True(painter.Stroke >= 1f);                                  // never below one physical pixel
        Assert.Equal(1f, painter.Hairline);
        painter.Line(new(0, 0), new(10, 0), painter.Hairline, Color.Red);
        // Keyline first, ink second: two submissions, the first dark.
        Assert.Equal(2, surface.Lines);
        Assert.Equal(Color.Black, surface.FirstLineColor);
        Assert.True(surface.FirstLineWidth > surface.SecondLineWidth);
        surface.Reset();
        painter.Circle(new(50, 50), 20f, 2f, Color.Red);
        Assert.True(surface.Lines >= 4);
        Assert.Equal(Color.Black, surface.FirstLineColor);
        surface.Reset();
        painter.CornerBrackets(new RectangleF(10, 10, 120, 40), 12f, 2f, Color.Red);
        Assert.Equal(16, surface.Lines);       // 8 keyline + 8 ink segments
        surface.Reset();
        painter.Text("LOCK", new(100, 100), large: true, 1f, Color.Red);
        Assert.Equal(9, surface.Texts);        // 8 keyline offsets + ink
        painter.Scale = 4f;
        Assert.Equal(6f, painter.Stroke);      // 1.5 px × density scale
    }

    // ── fixtures ─────────────────────────────────────────────────────────
    private static HudFrame FlightFrame(float speed = 210f, float throttle = -1f) => new(
        .8f, .6f, .4f, .3f, .2f, 1f, speed, throttle, false, false, true, 0,
        false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    private static IReadOnlyList<HudContact> Contacts() =>
    [
        new(HudContactKind.Ship, new(600, 400), true, 1f, new HudColor(.5f, .5f, 1f), false, false,
            Name: "Friendly", Range: 800, IsSameSide: true),
    ];

    private static HudModel State(string state) => state switch
    {
        "cockpit" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit,
            velocityMarker: new(940, 520), orderArrows: new(new(0.4f, 0f), new(-0.4f, 0f), false)),
        "chase" => HudModelAdapter.Create(FlightFrame(), CameraMode.Chase),
        "command" => HudModelAdapter.Create(FlightFrame(), CameraMode.Command, commandView: true),
        "docked" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit, docked: true),
        "lifepod" => HudModelAdapter.Create(FlightFrame(throttle: 0f) with { IsAdriftLifepod = true, OxygenSecondsRemaining = 42 },
            CameraMode.Cockpit),
        "dead" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit, dead: true),
        "ghost" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit, ghost: true),
        "turret" => HudModelAdapter.Create(FlightFrame(throttle: -1f), CameraMode.ExternalTurret, turret: true),
        "training" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit, trainingOverlayId: 3),
        "hidden" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit, hidden: true),
        "no-telemetry" => HudModelAdapter.Create(null, CameraMode.Cockpit),
        "empty-telemetry" => HudModelAdapter.Create(FlightFrame(speed: 0f, throttle: -1f) with
            { HullFraction = 0f, ShieldFraction = 0f, FuelFraction = 0f }, CameraMode.Cockpit),
        "notices" => HudModelAdapter.Create(FlightFrame(), CameraMode.Cockpit,
            notices: new[] { new HudNotice("TRAP: hull critical", true, .5f) }),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown state"),
    };

    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinResources? resources = null)
    {
        var definition = HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!;
        return new JetHudRenderer(surface, new HudSettings { Skin = HudSkinIds.Jet },
            resources ?? new CountingResources().Resources, definition);
    }

    private static string TempHudPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p1035-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "hud.json");
    }

    private static string JetJson(string? overrides = null)
    {
        var palette = new Dictionary<string, string>
        {
            ["Primary"] = "\"#79FFC0\"", ["Dim"] = "\"#3DA87B\"", ["Hot"] = "\"#C9FFE3\"",
            ["Plate"] = "\"#06110C\"", ["Keyline"] = "\"#080B09\"",
        };
        if (overrides is { Length: > 0 })
        {
            int colon = overrides.IndexOf(':');
            palette[overrides[..colon].Trim()] = overrides[(colon + 1)..].Trim();
        }
        return "{ \"Schema\": \"hud-skin-1\", \"Id\": \"jet\", \"Name\": \"Jet\", \"Version\": \"1.0.0\", " +
               "\"Defaults\": { \"SelectionBracket\": true, \"RadarDeclutter\": true, \"Level\": \"Tactical\" }, " +
               "\"Palette\": { " + string.Join(", ", palette.Select(kv => $"\"{kv.Key}\": {kv.Value}")) + " } }";
    }

    private static string ClassicJson() =>
        "{ \"Schema\": \"hud-skin-1\", \"Id\": \"classic\", \"Name\": \"Classic\", \"Version\": \"1.0.0\", " +
        "\"Defaults\": { \"SelectionBracket\": false, \"RadarDeclutter\": false, \"Level\": \"Tactical\" } }";

    /// <summary>A three-skin registry whose Jet gate the test can flip.</summary>
    private sealed class RegistryHarness : IDisposable
    {
        private bool _jetEnabled;
        public HudSettings Settings { get; }
        public HudSkinSwitcher Switcher { get; }

        public RegistryHarness(bool jetEnabled, string? initial = null, HudSettings? settings = null)
        {
            _jetEnabled = jetEnabled;
            Settings = settings ?? new HudSettings { Skin = initial ?? HudSkinIds.Classic };
            Switcher = new(Settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern),
                new Stub(HudSkinIds.Jet), jetEnabled: () => _jetEnabled);
        }

        public void SetJetEnabled(bool enabled) => _jetEnabled = enabled;

        /// <summary>The queued skin's id, without applying it.</summary>
        public string? Next()
        {
            var field = typeof(HudSkinSwitcher).GetField("_pending", Hidden);
            return (field?.GetValue(Switcher) as ResolvedHudSkin)?.Definition.Id;
        }

        public void Dispose() => Switcher.Dispose();
    }

    private sealed class Stub(string id) : IHudSkinRenderer
    {
        public string Id => id;
        public IReadOnlySet<string>? Coverage => null;
        public void Draw(HudModel model, in HudDrawFrame frame) { }
        public void DrawCommand(HudModel model) { }
        public void DrawConsole(HudModel model) { }
        public void SetRadarIcons(RadarIconTextures? icons) { }
        public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => default;
        public void Dispose() { }
    }

    /// <summary>Allocation-free surface: counts submissions and keeps the first
    /// line's colour/width plus the distinct text runs it saw.</summary>
    private sealed class CountingJetSurface : IJetVectorSurface
    {
        public int Lines, Fills, Texts, Begins, Ends;
        public Color FirstLineColor;
        public float FirstLineWidth, SecondLineWidth;
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);

        public void Begin() => Begins++;
        public void End() => Ends++;
        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            if (Lines == 0) { FirstLineColor = color; FirstLineWidth = width; }
            if (Lines == 1) SecondLineWidth = width;
            Lines++;
        }
        public void Fill(Vector2 min, Vector2 size, Color color) => Fills++;
        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Texts++;
            Seen.Add(text);
        }
        public Vector2 Measure(string text, bool large, float scale) => new(text.Length * 6f * scale, 12f * scale);

        public void Reset() { Lines = Fills = Texts = 0; Seen.Clear(); }
    }

    /// <summary>The shared-pass counters the Jet renderer must drive exactly like
    /// the Classic adapter (HudSkinResources is an immutable record of callbacks).
    /// P1040 — Jet draws its own chat/cargo/parts/command composition, so only the
    /// command-camera CHROME (NORTH + drag rectangle) is still driven from the host.</summary>
    private sealed class CountingResources
    {
        public int Radar, Tags, PodDeath, Command, Chat, Cargo, Parts, Chrome;
        public bool TagsWereModern, CommandWasClassicComposition;
        public readonly List<string> Order = new();
        public HudSkinResources Resources { get; }

        public CountingResources()
        {
            Resources = new HudSkinResources(
                _ => { Radar++; Order.Add("radar"); },
                (_, modern) => { Tags++; TagsWereModern |= modern; Order.Add("tags"); },
                _ => { PodDeath++; Order.Add("pod/death"); },
                (_, classic) => { Command++; CommandWasClassicComposition = classic; Order.Add("command"); },
                _ => { Chat++; Order.Add("chat"); },
                _ => { Cargo++; Order.Add("cargo"); },
                _ => { Parts++; Order.Add("parts"); })
            {
                CommandChrome = _ => { Chrome++; Order.Add("chrome"); }
            };
        }
    }
}
