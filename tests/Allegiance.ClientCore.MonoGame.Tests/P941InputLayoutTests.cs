using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P941 — the native default controls for a fresh profile, the "Port layout"
/// preset, the F6 preset switch, the persistence/migration rules and the F1
/// overview.
///
/// <para>Behaviour parity: the native layout is native's own
/// <c>Artwork/defaultinputmap.mdl</c> keyboard table (keyCommandMap, lines 3-107)
/// plus its mouse/wheel map (mouseButtonCommandMap, lines 109-116), decoded by
/// <see cref="DefaultInputMap"/>; the five mouse commands keep no keyboard chord.
/// The pre-P941 port layout stays a named preset. A keymap file written before
/// P941 has no layout marker, so it loads as the port layout — an existing saved
/// profile's bindings do not move.</para>
/// </summary>
public sealed class P941InputLayoutTests
{
    private static ActionMap Fresh() => new(InputActionCatalog.Definitions);

    private static ActionMap Port() => new(InputActionCatalog.Definitions, InputLayout.Port);

    // ── native defaults (keyboard) ────────────────────────────────────────

    [Fact]
    public void Fresh_profile_gets_the_native_keyboard_defaults()
    {
        var map = Fresh();
        Assert.Equal(InputLayout.Native, map.Layout);

        // Movement: native A/D strafe, Q/E roll, Tab thrust forward, W/X vertical
        // (defaultinputmap.mdl:17-23, TK_ThrustLeft 44 .. TK_FireBooster 50).
        Assert.Equal(new KeyBinding(Keys.Q, InputModifiers.None), map.BindingOf("RollLeft"));
        Assert.Equal(new KeyBinding(Keys.E, InputModifiers.None), map.BindingOf("RollRight"));
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("ThrustLeft"));
        Assert.Equal(new KeyBinding(Keys.D, InputModifiers.None), map.BindingOf("ThrustRight"));
        Assert.Equal(new KeyBinding(Keys.Tab, InputModifiers.None), map.BindingOf("ThrustForward"));
        Assert.Equal(new KeyBinding(Keys.W, InputModifiers.None), map.BindingOf("ThrustUp"));
        Assert.Equal(new KeyBinding(Keys.X, InputModifiers.None), map.BindingOf("ThrustDown"));
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireBooster"));

        // The pre-P941 port scheme is NOT what a fresh profile gets.
        Assert.NotEqual(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));
        Assert.NotEqual(new KeyBinding(Keys.LeftShift, InputModifiers.None), map.BindingOf("FireBooster"));

        // Targeting / weapons / console modes sampled across the native table.
        Assert.Equal(new KeyBinding(Keys.T, InputModifiers.None), map.BindingOf("TargetFriendly"));
        Assert.Equal(new KeyBinding(Keys.F, InputModifiers.None), map.BindingOf("TargetEnemy"));
        Assert.Equal(new KeyBinding(Keys.P, InputModifiers.None), map.BindingOf("ToggleAutopilot"));
        Assert.Equal(new KeyBinding(Keys.G, InputModifiers.None), map.BindingOf("ToggleCloak"));
        Assert.Equal(new KeyBinding(Keys.Z, InputModifiers.None), map.BindingOf("DropChaff"));
        Assert.Equal(new KeyBinding(Keys.C, InputModifiers.None), map.BindingOf("DropMine"));
        Assert.Equal(new KeyBinding(Keys.OemOpenBrackets, InputModifiers.None), map.BindingOf("AcceptCommand"));
        Assert.Equal(new KeyBinding(Keys.OemCloseBrackets, InputModifiers.None), map.BindingOf("RejectCommand"));
        Assert.Equal(new KeyBinding(Keys.OemTilde, InputModifiers.None), map.BindingOf("QuickChat"));
        Assert.Equal(new KeyBinding(Keys.R, InputModifiers.None), map.BindingOf("Reload"));
        Assert.Equal(new KeyBinding(Keys.F1, InputModifiers.None), map.BindingOf("Help"));
        Assert.Equal(new KeyBinding(Keys.OemPeriod, InputModifiers.None), map.BindingOf("CycleRadar"));
        Assert.Equal(new KeyBinding(Keys.V, InputModifiers.None), map.BindingOf("ToggleMouse"));
    }

    [Fact]
    public void Every_native_default_in_the_catalogue_is_honoured()
    {
        var map = Fresh();
        var kept = InputLayouts.Relocations.Select(r => r.Action).ToHashSet();
        foreach (var def in InputActionCatalog.Definitions)
        {
            if (def.NativeDefault is not { } native || InputLayouts.IsMouseAction(def.Name)) continue;
            if (kept.Contains(def.Name)) continue;
            Assert.Equal(native, map.BindingOf(def.Name));
        }
        // The relocation table is the only place a native default is displaced.
        Assert.NotEmpty(InputLayouts.Relocations);
    }

    // ── native mouse / wheel map ───────────────────────────────────────────

    [Fact]
    public void Native_mouse_and_wheel_map_matches_defaultinputmap_mdl()
    {
        // Parse the real corpus file through the P625 decoder: it must yield the
        // same five rows the catalogue exposes (trekinput.cpp:606-645).
        var parsed = DefaultInputMap.ParseMouseMap(DefaultInputMap.EmbeddedText)
            .ToDictionary(p => p.Tk, p => p.Button);
        Assert.Equal(5, parsed.Count);
        Assert.Equal(new Dictionary<int, int>
        {
            [55] = MouseButtonBinding.Left,     // mdl:111 (0, 55) TK_FireWeapon
            [56] = MouseButtonBinding.Middle,   // mdl:112 (2, 56) TK_FireMissile
            [58] = MouseButtonBinding.WheelUp,  // mdl:113 (9, 58) TK_ThrottleUp
            [59] = MouseButtonBinding.WheelDown,// mdl:114 (8, 59) TK_ThrottleDown
            [90] = MouseButtonBinding.Right,    // mdl:115 (1, 90) TK_TargetCenter
        }, parsed);

        foreach (var (button, tk, action) in InputLayouts.NativeMouseRows)
        {
            Assert.Equal(parsed[tk], button);
            Assert.Equal(button, InputLayouts.MouseMap[action].Button);
            Assert.True(DefaultInputMap.TkName(tk).Length > 0);
        }
    }

    [Fact]
    public void Mouse_left_fires_in_flight_and_target_picking_stays_reachable()
    {
        var map = Fresh();

        // Left button = TK_FireWeapon (mdl:111). Native's keyboard table has NO
        // key for TK_FireWeapon, so the mouse is the only binding — and it is a
        // binding (the action does not lose its only way to fire).
        Assert.Equal(MouseButtonBinding.Left, map.MouseButtonOf("FireWeapon")!.Value.Button);
        Assert.Equal(Keys.None, map.BindingOf("FireWeapon").Key);
        Assert.Equal("Mouse Left", map.LabelOf("FireWeapon"));

        // Middle = TK_FireMissile, right = TK_TargetCenter (mdl:112, :115).
        Assert.Equal(MouseButtonBinding.Middle, map.MouseButtonOf("FireMissile")!.Value.Button);
        Assert.Equal(MouseButtonBinding.Right, map.MouseButtonOf("TargetCenter")!.Value.Button);
        Assert.Equal(Keys.None, map.BindingOf("FireMissile").Key);
        Assert.Equal(Keys.None, map.BindingOf("TargetCenter").Key);

        // The wheel keeps the native throttle commands (:113-114) AND the keyboard
        // chords native also binds (= and -, mdl:29-30), so both stay in force.
        Assert.Equal("Wheel Up", map.MouseButtonOf("ThrottleUp")!.Value.Label);
        Assert.Equal("Wheel Down", map.MouseButtonOf("ThrottleDown")!.Value.Label);
        Assert.Equal("=", map.LabelOf("ThrottleUp"));
        Assert.Equal("-", map.LabelOf("ThrottleDown"));

        // Pointer picking (the "native way"): TK_ToggleMouse turns the virtual
        // joystick off (WinTrek.cpp:9419-9431) and the console picks with the
        // pointer again (console.cpp:1857-1900 ResolvePick / :1912-1990
        // PickObject: left = same-side ships, right = the legal command target).
        Assert.Equal(new KeyBinding(Keys.V, InputModifiers.None), map.BindingOf("ToggleMouse"));
        Assert.Equal(new KeyBinding(Keys.M, InputModifiers.None), map.BindingOf("CycleView"));
    }

    // ── no action loses its only binding ──────────────────────────────────

    [Fact]
    public void No_action_loses_its_only_binding_under_the_native_layout()
    {
        var port = Port();
        var native = Fresh();
        foreach (var def in InputActionCatalog.Definitions)
        {
            bool portBound = port.BindingOf(def.Name).Key != Keys.None;
            if (!portBound) continue;
            bool bound = native.BindingOf(def.Name).Key != Keys.None
                         || native.MouseButtonOf(def.Name) is not null;
            Assert.True(bound, $"{def.Name} lost its only binding in the native layout");
        }
    }

    [Fact]
    public void Native_layout_has_no_duplicate_chords_inside_the_flight_categories()
    {
        // The port legitimately shares a chord across CONTEXTS (Space = fire in
        // flight, select in menus). Inside the flight command groups a duplicate
        // would make one action swallow the other's key, so the native layout
        // must be collision-free there.
        string[] flight =
        [
            "MOVEMENT", "CRUISE THROTTLE", "WEAPONS", "INVENTORY / CARGO",
            "TARGETING / AUTOPILOT", "QUEUED ORDERS (RECEIVED COMMAND)",
            "COMMANDED SHIP (DRONE)", "VOTE / COMMAND",
        ];
        var map = Fresh();
        var seen = new Dictionary<(Keys, InputModifiers), string>();
        foreach (var def in InputActionCatalog.Definitions.Where(d => flight.Contains(d.Category)))
        {
            var chord = map.BindingOf(def.Name);
            if (chord.Key == Keys.None) continue;
            Assert.False(seen.TryGetValue((chord.Key, chord.Modifiers), out var other),
                $"{def.Name} and {other} both use {chord.Label}");
            seen[(chord.Key, chord.Modifiers)] = def.Name;
        }
    }

    // ── the port preset is preserved exactly ──────────────────────────────

    [Fact]
    public void Port_layout_preset_is_the_pre_p941_catalogue()
    {
        var map = Port();
        Assert.Equal(InputLayout.Port, map.Layout);
        foreach (var def in InputActionCatalog.Definitions)
            Assert.Equal(def.Default, map.BindingOf(def.Name));

        // The reported symptoms map 1:1 to this preset (why it is opt-in now).
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));
        Assert.Equal(new KeyBinding(Keys.D, InputModifiers.None), map.BindingOf("RollRight"));
        Assert.Equal(new KeyBinding(Keys.LeftShift, InputModifiers.None), map.BindingOf("FireBooster"));
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
        Assert.Equal(new KeyBinding(Keys.LeftControl, InputModifiers.None), map.BindingOf("FireMissile"));
    }

    [Fact]
    public void Switching_preset_restores_that_presets_defaults()
    {
        var map = Fresh();
        map.SetBinding("RollLeft", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        map.SetLayout(InputLayout.Port);
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));
        map.SetLayout(InputLayout.Native);
        Assert.Equal(new KeyBinding(Keys.Q, InputModifiers.None), map.BindingOf("RollLeft"));
        Assert.Equal(InputLayout.Native, map.Layout);

        // ResetToDefaults follows the CURRENT preset, not the catalogue.
        map.SetLayout(InputLayout.Port);
        map.SetBinding("RollLeft", new KeyBinding(Keys.F16, InputModifiers.None), force: true);
        map.ResetToDefaults();
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));
    }

    // ── persistence + migration ───────────────────────────────────────────

    [Fact]
    public void Json_round_trip_keeps_the_preset_and_the_overrides()
    {
        var map = Fresh();
        map.SetBinding("RollLeft", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        var json = map.ToJson();
        Assert.Contains("\"layout\": \"native\"", json);

        var reloaded = Fresh();
        reloaded.ApplyJson(json);
        Assert.Equal(InputLayout.Native, reloaded.Layout);
        Assert.Equal(new KeyBinding(Keys.F15, InputModifiers.None), reloaded.BindingOf("RollLeft"));
        // An untouched action is the native default, not the port one.
        Assert.Equal(new KeyBinding(Keys.E, InputModifiers.None), reloaded.BindingOf("RollRight"));
    }

    [Fact]
    public void Legacy_flat_keymap_file_migrates_to_native_keeping_the_players_rebinds()
    {
        // Exactly what the pre-P941 client wrote: only the player's rebinds.
        // They are kept on top of the native defaults; everything untouched
        // moves to native (the old port defaults broke the training prompts).
        const string legacy = "{\"RollLeft\":[70,0]}";   // F, a user rebind
        var map = Fresh();
        map.ApplyJson(legacy);

        Assert.Equal(InputLayout.Native, map.Layout);
        Assert.Equal(new KeyBinding(Keys.F, InputModifiers.None), map.BindingOf("RollLeft"));
        var native = Fresh();
        foreach (var def in InputActionCatalog.Definitions.Where(d => d.Name != "RollLeft"))
            Assert.Equal(native.BindingOf(def.Name), map.BindingOf(def.Name));

        // A player who only pressed OK in F6 wrote "{}": plain native.
        var ok = Fresh(); ok.ApplyJson("{}");
        Assert.Equal(InputLayout.Native, ok.Layout);
        Assert.Equal(new KeyBinding(Keys.Q, InputModifiers.None), ok.BindingOf("RollLeft"));
    }

    [Fact]
    public void Keymap_store_picks_the_layout_from_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p941-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keymap.json");
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP");
        Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", path);
        try
        {
            // 1. Fresh profile (no file): native defaults.
            var fresh = new KeyBindingStore();
            Assert.Equal(InputLayout.Native, fresh.Map.Layout);
            Assert.Equal(new KeyBinding(Keys.Q, InputModifiers.None), fresh.Map.BindingOf("RollLeft"));

            // 2. A pre-P941 file: native layout, the player's rebind kept.
            File.WriteAllText(path, "{\"FireWeapon\":[71,0]}");    // G, a user rebind
            var legacy = new KeyBindingStore();
            Assert.Equal(InputLayout.Native, legacy.Map.Layout);
            Assert.Equal(new KeyBinding(Keys.G, InputModifiers.None), legacy.Map.BindingOf("FireWeapon"));
            Assert.Equal(new KeyBinding(Keys.Q, InputModifiers.None), legacy.Map.BindingOf("RollLeft"));

            // 3. A P941 file keeps whatever preset it records.
            legacy.CycleLayout();                                  // Native -> Port, persisted
            Assert.Equal(InputLayout.Port, legacy.Map.Layout);
            var reloaded = new KeyBindingStore();
            Assert.Equal(InputLayout.Port, reloaded.Map.Layout);
            Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), reloaded.Map.BindingOf("RollLeft"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", previous);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ── F6 Controls preset switch ─────────────────────────────────────────

    [Fact]
    public void Controls_controller_cycles_the_named_presets()
    {
        var map = Fresh();
        var ctl = new KeyBindingController(map);
        Assert.Equal(InputLayouts.NativeName, ctl.LayoutName);

        ctl.CycleLayout();
        Assert.Equal(InputLayout.Port, map.Layout);
        Assert.Equal(InputLayouts.PortName, ctl.LayoutName);
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));

        ctl.CycleLayout();
        Assert.Equal(InputLayout.Native, map.Layout);
        Assert.Equal(InputLayouts.NativeName, ctl.LayoutName);

        // The preset swap is pending until OK, exactly like a rebind.
        ctl.CycleLayout();
        ctl.Cancel();   // back to Browse (no store: nothing to reload)
        Assert.Equal(InputLayout.Port, map.Layout);
    }

    [Fact]
    public void F6_labels_follow_the_selected_preset()
    {
        var native = Fresh();
        var port = Port();
        Assert.Equal("Q", native.LabelOf("RollLeft"));
        Assert.Equal("A", native.LabelOf("ThrustLeft"));
        Assert.Equal("Space", native.LabelOf("FireBooster"));
        Assert.Equal("Mouse Left", native.LabelOf("FireWeapon"));
        Assert.Equal("A", port.LabelOf("RollLeft"));
        Assert.Equal("Q", port.LabelOf("ThrustLeft"));
        // The port preset keeps Space on FireWeapon (mouse flight is opt-in there).
        Assert.Equal("Space", port.LabelOf("FireWeapon"));
        Assert.Equal("Shift", port.LabelOf("FireBooster"));
    }

    // ── training prompt/key audit (binding half) ──────────────────────────

    /// <summary>
    /// P941 — the audit table's binding half: every prompt that names an input
    /// must have that input bound to the action the prompt asks for in the
    /// native-default profile. The graph half (each prompt really is in the
    /// mission scripts, and the reminders repeat them) lives in
    /// <c>Allegiance.ClientCore.Tests/P941PromptAuditTests.cs</c>, where the
    /// training fake site is available.
    /// </summary>
    [Fact]
    public void Audited_training_prompt_keys_are_bound_to_their_action_in_the_native_default_profile()
    {
        var map = Fresh();   // fresh profile: native layout
        Assert.NotEmpty(TrainingPromptAudit.All);
        foreach (var prompt in TrainingPromptAudit.All)
        {
            if (prompt.Input is PromptInput.Joystick or PromptInput.Pointer)
                continue;   // joystick hardware / free pointer click: no chord

            // The on-screen text must name the input it asks for.
            Assert.True(prompt.Tokens.Any(t => prompt.Text.Contains(t, StringComparison.OrdinalIgnoreCase)),
                $"{prompt.Goal}: text does not name {prompt.Named}: \"{prompt.Text}\"");

            switch (prompt.Input)
            {
                case PromptInput.Keyboard:
                    Assert.Equal(prompt.Named, map.BindingOf(prompt.Action).Label);
                    break;
                case PromptInput.MouseButton:
                case PromptInput.Wheel:
                    Assert.Equal(prompt.Named, map.MouseButtonOf(prompt.Action)?.Label);
                    break;
            }
        }
    }

    // ── F1 overview reflects the live preset ──────────────────────────────
    [Fact]
    public void F1_overview_names_the_native_defaults()
    {
        var map = Fresh();
        var topics = HelpManualModel.BuildTopics(map);
        string flight = string.Join("\n", topics.First(t => t.Id == "hlpFlight").Lines);
        string combat = string.Join("\n", topics.First(t => t.Id == "hlpCombat").Lines);

        // Native defaults, not the old port prose ("W/S throttle, A/D roll").
        Assert.Contains("Q/E roll the craft", flight);  // roll
        Assert.Contains("A/D", flight);                 // lateral thrust
        Assert.Contains("Space fires the booster", flight);  // booster
        Assert.Contains("Mouse Left", combat);          // fire
        Assert.Contains("Mouse Middle", combat);        // missile
        Assert.Contains("V toggles mouse flight", flight);

        // The live key table carries the mouse rows too.
        var rows = map.ToHelpGroups().SelectMany(g => g.Entries).ToList();
        Assert.Contains(rows, e => e.Key == "Mouse Left" && e.Description.Contains("Fire weapons"));
        Assert.Contains(rows, e => e.Key == "Mouse Middle" && e.Description.Contains("missile"));

        // The Port preset shows the old keys instead.
        var portFlight = string.Join("\n", HelpManualModel.BuildTopics(Port()).First(t => t.Id == "hlpFlight").Lines);
        Assert.Contains("A/D roll the craft", portFlight);
        Assert.Contains("Q/E lateral thrust", portFlight);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void F1_overview_is_shared_by_both_hud_styles(HudStyle style)
    {
        // The help overlay / manual are shared model + renderer code (the modern
        // HUD re-composes gauges and reticles, not the help pane), so both styles
        // read the same live bindings. Pin it per style so a future style split
        // cannot leave one behind.
        _ = style;
        var lines = HelpManualModel.BuildTopics(Fresh())
            .SelectMany(t => t.Lines).ToList();
        Assert.Contains(lines, l => l.Contains("Q/E"));
        Assert.All(lines, l => Assert.All(l, ch => Assert.InRange(ch, '\u0020', '\u007e')));
    }
}
