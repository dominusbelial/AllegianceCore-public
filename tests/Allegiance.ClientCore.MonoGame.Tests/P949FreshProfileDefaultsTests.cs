using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P949 — a fresh profile defaults to the Port layout (keyboard steering, no
/// mouse required), the one-time keys hint is built from the live labels and
/// shown once, and the native stock map stays one F6 preset away.
///
/// <para>The native facts these tests pin: native CAN steer from the keyboard
/// (<c>TK_PitchUp/Down</c>, <c>TK_YawLeft/Right</c> → jsValues,
/// WinTrek/WinTrek.cpp:10895-10912), but its stock
/// <c>Artwork/defaultinputmap.mdl</c> keyCommandMap (lines 3-107) binds no key to
/// TK 17-20 or to VK 37-40, so a stock install flies with the mouse/joystick.
/// The port's fresh default therefore ships a keyboard map and keeps native's
/// stock map as the "Classic Allegiance" preset.</para>
/// </summary>
public sealed class P949FreshProfileDefaultsTests
{
    private static ActionMap Fresh() => new(InputActionCatalog.Definitions);

    // ── fresh = Port ───────────────────────────────────────────────────────

    [Fact]
    public void Fresh_profile_starts_on_the_port_layout_with_keyboard_steering()
    {
        var map = Fresh();
        Assert.Equal(InputLayout.Port, map.Layout);

        // Thrust / steering / roll are all reachable without the mouse, which is
        // exactly what the two fresh installs reported as broken.
        Assert.Equal(new KeyBinding(Keys.W, InputModifiers.None), map.BindingOf("ThrustForward"));
        Assert.Equal(new KeyBinding(Keys.Up, InputModifiers.None), map.BindingOf("PitchUp"));
        Assert.Equal(new KeyBinding(Keys.Down, InputModifiers.None), map.BindingOf("PitchDown"));
        Assert.Equal(new KeyBinding(Keys.Left, InputModifiers.None), map.BindingOf("YawLeft"));
        Assert.Equal(new KeyBinding(Keys.Right, InputModifiers.None), map.BindingOf("YawRight"));
        Assert.Equal(new KeyBinding(Keys.A, InputModifiers.None), map.BindingOf("RollLeft"));
        Assert.Equal(new KeyBinding(Keys.D, InputModifiers.None), map.BindingOf("RollRight"));
        Assert.Equal(new KeyBinding(Keys.Space, InputModifiers.None), map.BindingOf("FireWeapon"));
        Assert.Equal(new KeyBinding(Keys.LeftShift, InputModifiers.None), map.BindingOf("FireBooster"));
        Assert.Equal(new KeyBinding(Keys.Tab, InputModifiers.None), map.BindingOf("ToggleStarmap"));

        // Mouse flight OFF by default in the Port layout; ON only in native's.
        Assert.False(InputLayouts.MouseFlightByDefault(InputLayout.Port));
        Assert.True(InputLayouts.MouseFlightByDefault(InputLayout.Native));
    }

    [Fact]
    public void The_native_preset_is_labelled_for_what_it_is()
    {
        Assert.Equal("Classic Allegiance (stock map: mouse/joystick steering)", InputLayouts.NativeName);
        Assert.Equal(InputLayouts.NativeName, InputLayouts.Name(InputLayout.Native));
        Assert.Equal("Port layout", InputLayouts.Name(InputLayout.Port));
        // The F6 preset cycle keeps both reachable.
        Assert.Equal(InputLayout.Native, InputLayouts.Next(InputLayout.Port));
        Assert.Equal(InputLayout.Port, InputLayouts.Next(InputLayout.Native));
    }

    // ── persistence: never write the fallback, never change an existing file ─

    [Fact]
    public void Fresh_profile_is_not_saved_until_the_player_changes_something()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p949-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keymap.json");
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP");
        Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", path);
        try
        {
            var store = new KeyBindingStore();
            Assert.True(store.FreshProfile);
            Assert.False(store.HasKeymap);
            Assert.Equal(InputLayout.Port, store.Map.Layout);
            Assert.False(File.Exists(path));   // the fallback is NOT written

            // The first real change persists — and the profile stops being fresh.
            store.Map.SetBinding("RollLeft", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
            store.Save();
            Assert.True(File.Exists(path));
            Assert.False(store.FreshProfile);
            Assert.True(store.HasKeymap);
            Assert.Contains("\"layout\": \"port\"", File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", previous);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_existing_keymap_file_is_never_changed_by_loading_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p949-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keymap.json");
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP");
        Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", path);
        try
        {
            // A P941 file that chose the native preset: a saved profile does not
            // move under the player (still the stock map, not the P949 default).
            const string saved = "{\"layout\":\"native\",\"bindings\":{\"RollLeft\":[70,0]}}";
            File.WriteAllText(path, saved);
            var store = new KeyBindingStore();
            Assert.Equal(InputLayout.Native, store.Map.Layout);
            Assert.Equal(new KeyBinding(Keys.F, InputModifiers.None), store.Map.BindingOf("RollLeft"));
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.False(store.FreshProfile);

            // A legacy flat file (no layout marker) keeps the Port layout whose
            // untouched chords it was written from.
            const string legacy = "{\"FireWeapon\":[71,0]}";
            File.WriteAllText(path, legacy);
            var legacyStore = new KeyBindingStore();
            Assert.Equal(InputLayout.Port, legacyStore.Map.Layout);
            Assert.Equal(new KeyBinding(Keys.G, InputModifiers.None), legacyStore.Map.BindingOf("FireWeapon"));
            Assert.Equal(legacy, File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", previous);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ── the one-time hint: live labels ─────────────────────────────────────

    [Fact]
    public void Hint_text_is_built_from_the_live_labels()
    {
        var map = Fresh();
        string? text = ControlHint.TextFor(map);
        Assert.Equal("Keys: W thrust | Up/Down/Left/Right steer | F6 controls (Classic Allegiance keys available)", text);

        // ASCII only (the Classic spritefonts bake ASCII 32..126).
        Assert.All(text!, ch => Assert.InRange(ch, '\u0020', '\u007e'));

        // A rebind of the thrust key shows up immediately.
        map.SetBinding("ThrustForward", new KeyBinding(Keys.F15, InputModifiers.None), force: true);
        Assert.Contains("F15 thrust", ControlHint.TextFor(map));

        // The Classic preset names its own keys (Tab thrust, F6 controls).
        var classic = new ActionMap(InputActionCatalog.Definitions, InputLayout.Native);
        string? classicText = ControlHint.TextFor(classic);
        Assert.Contains("Tab thrust", classicText);
        Assert.Contains("F6 controls", classicText);
        Assert.Equal("Keys: Tab thrust | Up/Down/Left/Right steer | F6 controls (Classic Allegiance keys available)", classicText);
    }

    [Fact]
    public void Hint_lifetime_is_sixty_seconds_or_any_key_and_never_returns()
    {
        Assert.True(ControlHint.ShouldShow(freshProfile: true, alreadySeen: false));
        Assert.False(ControlHint.ShouldShow(freshProfile: false, alreadySeen: false));
        Assert.False(ControlHint.ShouldShow(freshProfile: true, alreadySeen: true));

        var timed = new ControlHint("Keys: ...");
        Assert.True(timed.Active);
        Assert.False(timed.Update(30.0, anyKeyPressed: false));
        Assert.False(timed.Update(29.0, anyKeyPressed: false));
        Assert.True(timed.Update(1.5, anyKeyPressed: false));      // past 60 s
        Assert.Equal("timeout", timed.DismissReason);
        Assert.False(timed.Active);
        Assert.Null(timed.VisibleText);
        Assert.False(timed.Update(1.0, anyKeyPressed: false));     // stays gone

        var keyed = new ControlHint("Keys: ...");
        Assert.True(keyed.Update(0.5, anyKeyPressed: true));
        Assert.Equal("key", keyed.DismissReason);
        Assert.Null(keyed.VisibleText);

        // Dismiss is idempotent (the "keymap saved" path can race the timer).
        var saved = new ControlHint("Keys: ...");
        Assert.True(saved.Dismiss("saved"));
        Assert.False(saved.Dismiss("saved"));
        Assert.Equal("saved", saved.DismissReason);
    }

    [Fact]
    public void Hint_seen_flag_persists_in_hud_settings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p949-hud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "hud.json");
        try
        {
            // A fresh settings file defaults to the hint not being seen.
            Assert.False(HudSettings.Load(path, existingPlayer: false).ControlHintSeen);

            var settings = new HudSettings { ControlHintSeen = true };
            settings.Save(path);
            Assert.True(HudSettings.Load(path, existingPlayer: true).ControlHintSeen);
            Assert.Contains("\"ControlHintSeen\": true", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Hud_model_carries_the_hint_for_both_styles()
    {
        // The hint rides the SHARED model, which both skins draw from (Modern
        // delegates to HudRenderer.Draw), so one pinned field covers Classic and
        // Modern; a null hint is the dismissed state.
        var withHint = HudModelAdapter.Create(null, CameraMode.Cockpit,
            controlHint: "Keys: W thrust | Up/Down/Left/Right steer | F6 controls (Classic Allegiance keys available)");
        Assert.StartsWith("Keys: W thrust", withHint.Context.ControlHint);

        var dismissed = HudModelAdapter.Create(null, CameraMode.Cockpit);
        Assert.Null(dismissed.Context.ControlHint);
    }
}
