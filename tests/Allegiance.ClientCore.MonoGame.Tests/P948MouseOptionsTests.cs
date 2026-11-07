using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P948 — mouse options parity: native's <c>FlipY</c> (invert mouse Y) and
/// <c>MouseSensitivity</c> persist in the player's <c>player.json</c>, load at
/// startup, save on every change, and are exposed on the in-game settings panel
/// as well as by the in-flight keys.
///
/// <para>Native references: <c>FlipY</c> loaded default 0 (<c>WinTrek.cpp:3048</c>),
/// toggled+saved (<c>:5297-5305</c>), applied to pitch (<c>:10849-10851</c>);
/// <c>MouseSensitivity</c> loaded default 0.6 (<c>:2809</c>), given to the engine
/// mouse (<c>:2811</c>), stepped 0.01 within [0.1, 2.0] and saved
/// (<c>AdjustMouseSens</c>, <c>:5512-5531</c>); engine delta math
/// <c>dinput.cpp:386-394</c> × the joystick image's 1/512 (<c>efimage.cpp:186-190</c>).
/// </para>
/// </summary>
public sealed class P948MouseOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p948-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath;
    private readonly KeyboardState _noKeys = new();

    public P948MouseOptionsTests()
    {
        Directory.CreateDirectory(_dir);
        _prefsPath = Path.Combine(_dir, "player.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ---- persistence -------------------------------------------------------

    [Fact]
    public void Mouse_options_round_trip_through_player_json()
    {
        var saved = new IntroPreferences { InvertMouseY = true, MouseSensitivity = 1.25f };
        saved.Save(_prefsPath);

        var loaded = IntroPreferences.Load(_prefsPath);
        Assert.True(loaded.InvertMouseY);
        Assert.Equal(1.25f, loaded.MouseSensitivity, 5);
    }

    [Fact]
    public void Legacy_profile_without_the_fields_loads_native_defaults()
    {
        // A pre-P948 file: no InvertMouseY / MouseSensitivity keys at all.
        File.WriteAllText(_prefsPath, "{ \"Server\": \"example\", \"Pilot\": \"pilot\" }");

        var loaded = IntroPreferences.Load(_prefsPath);
        Assert.False(loaded.InvertMouseY);                                     // native FlipY default 0
        Assert.Equal(0.6f, loaded.MouseSensitivity, 5);                        // native MouseSensitivity default
        Assert.Equal(MouseFlightController.DefaultMouseSensitivity, loaded.MouseSensitivity, 5);
    }

    [Fact]
    public void Stored_sensitivity_is_clamped_to_natives_range()
    {
        File.WriteAllText(_prefsPath, "{ \"MouseSensitivity\": 9.5 }");
        Assert.Equal(MouseFlightController.MaxMouseSensitivity, IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);

        File.WriteAllText(_prefsPath, "{ \"MouseSensitivity\": -3 }");
        Assert.Equal(MouseFlightController.MinMouseSensitivity, IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);

        var nan = new IntroPreferences { MouseSensitivity = float.NaN };
        nan.Normalize();
        Assert.Equal(MouseFlightController.DefaultMouseSensitivity, nan.MouseSensitivity, 5);
    }

    // ---- 5a: the effective scale at the default equals native's ------------

    [Fact]
    public void Default_effective_pixel_scale_equals_native_0_6_times_1_over_512()
    {
        var c = new MouseFlightController();

        // port: DefaultSensitivity (1/512) x MouseSensitivity (0.6)
        Assert.Equal(1f / 512f, c.Sensitivity, 7);
        Assert.Equal(0.6f, c.MouseSensitivity, 5);
        Assert.Equal((1f / 512f) * 0.6f, c.PixelScale, 8);

        // native: engine delta x 0.6 (dinput.cpp:386-394, acceleration default 0,
        // WinTrek.cpp:3051) then x 1/512 (efimage.cpp:186-190).
        const float nativeEngineSensitivity = 0.6f;
        const float nativeImageSensitivity = 1f / 512f;
        Assert.Equal(nativeEngineSensitivity * nativeImageSensitivity, c.PixelScale, 8);

        // And the live delta really uses it: 10 px -> -10 * 0.6 / 512 (subtracted,
        // native's sign), comfortably inside the unit-circle clamp.
        var d = c.Update(10f, 0f);
        Assert.Equal(-10f * nativeEngineSensitivity * nativeImageSensitivity, d.Yaw, 6);
    }

    // ---- startup apply -----------------------------------------------------

    [Fact]
    public void Startup_applies_the_saved_mouse_options()
    {
        new IntroPreferences { InvertMouseY = true, MouseSensitivity = 1.4f }.Save(_prefsPath);
        var game = NewGame(out var flight);

        Call(game, "LoadMouseOptions", _prefsPath);

        Assert.True(flight.InvertPitch);
        Assert.Equal(1.4f, flight.MouseSensitivity, 5);
    }

    // ---- the key and the options toggle write the same value --------------

    [Fact]
    public void Invert_key_and_options_setter_flip_and_save_the_same_value()
    {
        var game = NewGameWithPrefs(out var flight);
        var keys = ChordKeys("InvertMouse");
        Assert.NotEmpty(keys);

        // In-flight key (InvertMouse): one press flips and saves.
        Call(game, "UpdateMouseOptionKeys", new KeyboardState(keys.ToArray()), false, 0.016f);
        Assert.True(flight.InvertPitch);
        Assert.True(IntroPreferences.Load(_prefsPath).InvertMouseY);

        // Release, then the options-panel row path (the exact setter the Ctrl+F6
        // row and the intro Game Options row call) flips it back and saves.
        Call(game, "UpdateMouseOptionKeys", _noKeys, false, 0.016f);
        Call(game, "SetInvertMouseY", false);
        Assert.False(flight.InvertPitch);
        Assert.False(IntroPreferences.Load(_prefsPath).InvertMouseY);

        // Holding the key cannot re-fire (edge-triggered, unchanged by P948).
        Call(game, "UpdateMouseOptionKeys", new KeyboardState(keys.ToArray()), false, 0.016f);
        Assert.True(flight.InvertPitch);
        Call(game, "UpdateMouseOptionKeys", new KeyboardState(keys.ToArray()), false, 0.016f);
        Assert.True(flight.InvertPitch);
    }

    [Fact]
    public void Invert_key_is_ignored_while_composing()
    {
        var game = NewGameWithPrefs(out var flight);
        var keys = ChordKeys("InvertMouse");
        Call(game, "UpdateMouseOptionKeys", new KeyboardState(keys.ToArray()), true, 0.016f);
        Assert.False(flight.InvertPitch);
    }

    // ---- held sensitivity keys repeat, clamp and save ----------------------

    [Fact]
    public void Held_sensitivity_key_repeats_clamps_and_saves()
    {
        var game = NewGameWithPrefs(out var flight);
        var keys = ChordKeys("MouseSensitivityUp");
        Assert.NotEmpty(keys);
        var held = new KeyboardState(keys.ToArray());
        Assert.Equal(0.6f, flight.MouseSensitivity, 5);

        // Press frame: exactly one native 0.01 step, saved immediately.
        Call(game, "UpdateMouseOptionKeys", held, false, 0.016f);
        Assert.Equal(0.61f, flight.MouseSensitivity, 5);
        Assert.Equal(0.61f, IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);

        // Before the repeat delay elapses: no extra steps.
        for (int i = 0; i < 10; i++)   // 10 x 16 ms = 0.16 s < 0.35 s delay
            Call(game, "UpdateMouseOptionKeys", held, false, 0.016f);
        Assert.Equal(0.61f, flight.MouseSensitivity, 5);

        // Past the delay it repeats (~10 steps/s): 1.5 s of holding adds ~12 steps.
        for (int i = 0; i < 94; i++)   // 1.5 s
            Call(game, "UpdateMouseOptionKeys", held, false, 0.016f);
        Assert.InRange(flight.MouseSensitivity, 0.68f, 0.85f);
        Assert.Equal(flight.MouseSensitivity, IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);

        // At the native end stops the key clamps and still saves.
        flight.MouseSensitivity = 1.99f;
        Call(game, "UpdateMouseOptionKeys", _noKeys, false, 0.016f);   // release resets the repeat
        Call(game, "UpdateMouseOptionKeys", held, false, 0.016f);     // fresh press -> one step
        Assert.Equal(MouseFlightController.MaxMouseSensitivity, flight.MouseSensitivity, 5);
        Assert.Equal(MouseFlightController.MaxMouseSensitivity,
            IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);

        var down = new KeyboardState(ChordKeys("MouseSensitivityDown").ToArray());
        flight.MouseSensitivity = 0.11f;
        Call(game, "UpdateMouseOptionKeys", _noKeys, false, 0.016f);
        Call(game, "UpdateMouseOptionKeys", down, false, 0.016f);
        Assert.Equal(MouseFlightController.MinMouseSensitivity, flight.MouseSensitivity, 5);
        Assert.Equal(MouseFlightController.MinMouseSensitivity,
            IntroPreferences.Load(_prefsPath).MouseSensitivity, 5);
    }

    [Fact]
    public void Sensitivity_change_shows_the_hud_readout()
    {
        var game = NewGameWithPrefs(out var flight);
        var keys = new KeyboardState(ChordKeys("MouseSensitivityDown").ToArray());
        Call(game, "UpdateMouseOptionKeys", keys, false, 0.016f);

        var notice = (MouseFlightNotice)GetField(game, "_mouseFlightNotice");
        Assert.True(notice.Visible);
        Assert.Equal("MOUSE SENSITIVITY 59 %", notice.Text);

        // It fades out by itself (hold + fade), then stops drawing. The timer is
        // aged by Update's per-frame notice tick, which the production app calls
        // in every state (so an intro-screen change does not greet the pilot).
        for (int i = 0; i < 200; i++)
            Call(game, "UpdateMouseOptionNotice", 0.016f);
        Assert.False(notice.Visible);
        Assert.Equal(0.59f, flight.MouseSensitivity, 5);   // changing it does not drift
    }

    // ---- pure timing helpers ----------------------------------------------

    [Fact]
    public void KeyRepeat_fires_on_press_then_after_the_delay_at_ten_herz()
    {
        var repeat = new KeyRepeat();
        Assert.True(repeat.Update(true, 0.016f));          // the tap == native's one step
        Assert.False(repeat.Update(true, 0.016f));         // inside the delay
        Assert.Equal(10, KeyRepeat.RepeatIntervalSeconds > 0
            ? (int)MathF.Round(1f / KeyRepeat.RepeatIntervalSeconds) : 0);

        int fires = 0;
        for (float t = 0f; t < 1.0f; t += 0.016f)
            if (repeat.Update(true, 0.016f)) fires++;
        // 1 s at 10 Hz after a 0.35 s delay ≈ 6-7 repeats (frame quantisation).
        Assert.InRange(fires, 5, 8);

        Assert.False(repeat.Update(false, 0.016f));        // release resets
        Assert.True(repeat.Update(true, 0.016f));          // a new press fires at once
    }

    [Fact]
    public void Notice_holds_then_fades()
    {
        var notice = new MouseFlightNotice();
        Assert.False(notice.Visible);
        notice.Show("MOUSE SENSITIVITY 60 %");
        Assert.True(notice.Visible);
        Assert.Equal(1f, notice.Opacity, 3);

        notice.Tick(MouseFlightNotice.HoldSeconds);
        Assert.True(notice.Visible);
        Assert.Equal(1f, notice.Opacity, 3);               // still fully legible at end of hold

        notice.Tick(MouseFlightNotice.FadeSeconds + 0.05f);
        Assert.False(notice.Visible);                      // gone
        Assert.Equal("", notice.Text);
    }

    // ---- option helpers ----------------------------------------------------

    [Fact]
    public void Step_matches_natives_delta_and_end_stops()
    {
        Assert.Equal(0.61f, MouseOptions.Step(0.6f, 1), 5);   // c_fMouseSensDelta = 0.01
        Assert.Equal(0.59f, MouseOptions.Step(0.6f, -1), 5);
        Assert.Equal(2.0f, MouseOptions.Step(2.0f, 1), 5);
        Assert.Equal(0.1f, MouseOptions.Step(0.1f, -1), 5);
        Assert.Equal("Raise Mouse Sensitivity to 61 %", MouseOptions.StepLabel(0.6f, 1));
        Assert.Equal("Lower Mouse Sensitivity to 59 %", MouseOptions.StepLabel(0.6f, -1));
        Assert.Equal("Raise Mouse Sensitivity (highest)", MouseOptions.StepLabel(2.0f, 1));
        Assert.Equal("Lower Mouse Sensitivity (lowest)", MouseOptions.StepLabel(0.1f, -1));
    }

    // ---- source guards: the wiring exists ---------------------------------

    [Fact]
    public void Startup_loads_the_options_and_the_panel_exposes_both_rows()
    {
        string root = SourceRoot();
        string game1 = File.ReadAllText(Path.Combine(root, "Game1.cs"));
        Assert.Contains("LoadMouseOptions();", game1);        // applied at startup

        string gpu = File.ReadAllText(Path.Combine(root, "Game1.GpuBudget.cs"));
        Assert.Contains("case GraphicsRowInvertMouseY:", gpu);       // panel toggle row
        Assert.Contains("case GraphicsRowMouseSensitivity:", gpu);   // panel -/+ row
        Assert.Contains("\"Invert mouse Y: ", gpu);
        Assert.Contains("\"Mouse sensitivity: ", gpu);
        Assert.Contains("GraphicsRowCount = 24", gpu);

        string catalog = File.ReadAllText(Path.Combine(root, "Rendering/InputActionCatalog.cs"));
        Assert.Contains("Invert mouse pitch", catalog);
        Assert.DoesNotContain("session only", catalog);
    }

    // ---- harness -----------------------------------------------------------

    private static Game1 NewGame(out MouseFlightController flight)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        flight = new MouseFlightController();
        SetField(game, "_mouseFlight", flight);
        SetField(game, "_optionsPrefs", new IntroPreferences());
        SetField(game, "_optionsPrefsLoaded", true);
        SetField(game, "_mouseFlightNotice", new MouseFlightNotice());
        SetField(game, "_mouseSensRepeatUp", new KeyRepeat());
        SetField(game, "_mouseSensRepeatDown", new KeyRepeat());
        return game;
    }

    /// <summary>A game whose mouse options read/write this test's temp
    /// <c>player.json</c> (never the operator's real profile).</summary>
    private Game1 NewGameWithPrefs(out MouseFlightController flight)
    {
        var game = NewGame(out flight);
        SetField(game, "_playerPrefsPath", _prefsPath);
        return game;
    }

    private static void SetField(Game1 game, string name, object value)
        => typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);

    private static object GetField(Game1 game, string name)
        => typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

    private static object? Call(Game1 game, string name, params object[] args)
        => typeof(Game1).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, args);

    /// <summary>The keys (modifiers included) the LIVE map currently binds to
    /// <paramref name="action"/>, so the test works in either layout preset.</summary>
    private static List<Keys> ChordKeys(string action)
    {
        var chord = KeyBindingStore.Instance.Map.BindingOf(action);
        var keys = new List<Keys> { chord.Key };
        if ((chord.Modifiers & InputModifiers.Control) != 0) keys.Add(Keys.LeftControl);
        if ((chord.Modifiers & InputModifiers.Alt) != 0) keys.Add(Keys.LeftAlt);
        if ((chord.Modifiers & InputModifiers.Shift) != 0) keys.Add(Keys.LeftShift);
        return keys;
    }

    private static string SourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AllegianceCore.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "Allegiance.ClientCore.Game");
    }
}
