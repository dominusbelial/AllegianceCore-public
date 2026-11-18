using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

// Existing Options menu handlers/rendering, shared by desktop and the browser intro lifecycle.
public sealed partial class Game1
{
    private KeyboardState _iPrivacyKeys;
    // P757b — the options screen's preference snapshot. Loaded once when the
    // screen opens (never from Draw), so drawing does not touch player.json.
    private IntroPreferences _optionsPrefs = new();
    private bool _optionsPrefsLoaded;

    private void EnsureOptionsPrefs(string? path = null)
    {
        if (_optionsPrefsLoaded) return;
        _optionsPrefs = IntroPreferences.Load(path ?? _playerPrefsPath);
        _optionsPrefsLoaded = true;
    }

    /// <summary>P806 — the intro Options menu is the NATIVE MENU TREE
    /// (<c>WinTrek::ShowOptionsMenu</c>, WinTrek.cpp:4429-4442, and the submenus
    /// <c>GetSubMenu</c> builds at :4502-4590): Sound Options and Game Options
    /// are nested menus (with Chat/Mute/Filter nested one level deeper), not
    /// one-press toggles. <see cref="OptionsMenuModel"/> owns the tree and the
    /// labels; this method feeds it the live switch state and executes the
    /// selected command against the port's real controls. Up/Down move,
    /// Enter activates, a row's native hotkey activates it directly, and ESC
    /// leaves a submenu (and closes the menu at the root — native OnCancel).</summary>
    private void UpdateOptionsMenu(KeyboardState? ks = null)
    {
        var keys = ks ?? Keyboard.GetState();
        EnsureOptionsPrefs();
        bool Press(Keys key) => keys.IsKeyDown(key) && !_iPrivacyKeys.IsKeyDown(key);

        var state = CurrentOptionsState();
        bool esc = Press(Keys.Escape);
        if (Press(Keys.Up)) _optionsMenu.Move(-1, state);
        if (Press(Keys.Down)) _optionsMenu.Move(1, state);

        void Run(OptionsMenuModel.OptionsCommand cmd)
        {
            bool handled = false;
            HandlePlatformOptionsCommand(cmd, ref handled);
            if (handled) return;
            // P871 / SIM-0412 — every MDL button click starts its cue and forces
            // the audio update NOW (native SoundIDEventSink, trekmdl.cpp:452-478;
            // the click must not wait behind the button's own work).
            if (OptionsSound is { } buttonAudio)
                Audio.SoundIdEventSink.Fire(buttonAudio, Allegiance.Core.Audio.NativeSoundCatalog.IdOf("mouseclickSound"));
            switch (cmd)
            {
                case OptionsMenuModel.OptionsCommand.GraphicsSettings:   // native idmOptions
                    _graphicsSettingsVisible = true;
                    _introView = IntroView.Main;
                    break;
                case OptionsMenuModel.OptionsCommand.OpenSoundMenu:
                case OptionsMenuModel.OptionsCommand.OpenGameMenu:
                case OptionsMenuModel.OptionsCommand.OpenChatMenu:
                case OptionsMenuModel.OptionsCommand.OpenMouseMenu:
                case OptionsMenuModel.OptionsCommand.OpenVisualsMenu:   // P979 — port extension
                    _optionsMenu.Activate(state);          // descend one level
                    break;
                case OptionsMenuModel.OptionsCommand.MapKeysAndControls: // native idmConfigure 'C'
                    ToggleKeyBindings();
                    _introView = IntroView.Main;
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleMusic:         // native idmToggleMusic
                    OptionsMusic?.Toggle();
                    break;
                case OptionsMenuModel.OptionsCommand.CycleAudioQuality:
                    CycleOptionsAudioQuality();
                    break;
                case OptionsMenuModel.OptionsCommand.Toggle3DSound:       // P893 — port addition (no native counterpart)
                    ToggleOptions3DSound();
                    break;
                case OptionsMenuModel.OptionsCommand.MusicVolumeUp:       // native idmMusicVolumeUp
                    OptionsMusic?.VolumeUp();
                    break;
                case OptionsMenuModel.OptionsCommand.MusicVolumeDown:     // native idmMusicVolumeDown
                    OptionsMusic?.VolumeDown();
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleFilterChatsToAll: // native idmFilterChatsToAll
                    ToggleOptionsChatFilter();
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleMuteChatSender:    // native idmContextMutePlayer 'M'
                    ToggleOptionsChatMute();
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleLinearControls:   // native idmToggleLinearControls 'L'
                    _mouseFlight.LinearControls = !_mouseFlight.LinearControls;
                    Console.WriteLine($"[intro] game options: linear controls -> {(_mouseFlight.LinearControls ? "on" : "off")}");
                    break;
                case OptionsMenuModel.OptionsCommand.CycleDeadzone:          // native idmToggleLargeDeadZone 'Z'
                {
                    var presets = Flight.MouseFlightController.DeadzonePresets;
                    int i = System.Array.FindIndex(presets, d => MathF.Abs(d - _mouseFlight.Deadzone) < 0.0005f);
                    i = i < 0 ? 0 : (i + 1) % presets.Length;
                    _mouseFlight.Deadzone = presets[i];
                    Console.WriteLine($"[intro] game options: mouse dead zone -> {presets[i]:0.00}");
                    break;
                }
                case OptionsMenuModel.OptionsCommand.ToggleFlipY:            // native idmToggleFlipY 'Y'
                    // P948 — persist it (native ToggleFlipY saves FlipY,
                    // WinTrek.cpp:5297-5305) through the same setter the in-flight
                    // key uses, so both paths write one value.
                    SetInvertMouseY(!_mouseFlight.InvertPitch);
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleVirtualJoystick:  // native idmToggleVirtualJoystick 'J'
                    _mouseFlightEnabled = !_mouseFlightEnabled;
                    if (_mouseFlightEnabled) _mouseFlight.Enable(); else _mouseFlight.Reset();
                    Console.WriteLine($"[intro] game options: virtual joystick -> {(_mouseFlightEnabled ? "on" : "off")}");
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleForceFeedback:     // native idmToggleEnableFeedback 'E'
                    ToggleForceFeedback();
                    break;
                // P948 — native's Mouse Options items (idmMouseSensUp/Down,
                // WinTrek.cpp:4612-4613 → AdjustMouseSens :5512-5531): one 0.01
                // step, clamped to [0.1, 2.0], SAVED.
                case OptionsMenuModel.OptionsCommand.MouseSensitivityUp:
                    StepMouseSensitivity(+1);
                    break;
                case OptionsMenuModel.OptionsCommand.MouseSensitivityDown:
                    StepMouseSensitivity(-1);
                    break;
                // P979 — the Visuals page: the command is generic and acts on the
                // flag whose row was activated. The live object is updated in place
                // (the flight render reads it every frame) and the profile copy is
                // saved, so the choice survives a restart.
                case OptionsMenuModel.OptionsCommand.ToggleVisualFlag:
                    ToggleVisualFlag(_optionsMenu.VisualKeyAt(_optionsMenu.Row0, state));
                    break;
                case OptionsMenuModel.OptionsCommand.RaiseVisualRange:
                    StepVisualRange(_optionsMenu.VisualKeyAt(_optionsMenu.Row0, state), +1);
                    break;
                case OptionsMenuModel.OptionsCommand.LowerVisualRange:
                    StepVisualRange(_optionsMenu.VisualKeyAt(_optionsMenu.Row0, state), -1);
                    break;
            }
        }

        // ESC leaves a submenu; at the root it closes the menu (native OnCancel).
        if (esc)
        {
            if (!_optionsMenu.Back()) _introView = IntroView.Main;
        }
        else if (Press(Keys.Enter))
        {
            if (_optionsMenu.Activate(state) is { } cmd) Run(cmd);
        }
        else
        {
            // Native menu hotkeys (the letters GetSubMenu assigns) first.
            foreach (Keys k in Enum.GetValues<Keys>())
            {
                if (k is < Keys.A or > Keys.Z || !Press(k)) continue;
                if (_optionsMenu.CommandForHotkey((char)('A' + (k - Keys.A)), state) is { } cmd)
                {
                    Run(cmd);
                    break;
                }
            }
        }

        // The port's added benchmark-sharing panel moves to B (S is the native
        // Sound Options hotkey now). Global music keys stay as they were.
#if !BROWSER
        if (Press(Keys.B)) { if (_benchmarkSharing?.Available == true) _sharePanel = true; }
#endif
        if (Press(Keys.M) && OptionsMusic is not null) OptionsMusic.Toggle();
        if (Press(Keys.OemPlus) && OptionsMusic is not null) OptionsMusic.VolumeUp();
        if (Press(Keys.OemMinus) && OptionsMusic is not null) OptionsMusic.VolumeDown();
        _iPrivacyKeys = keys;
    }

    /// <summary>P806 — the live switch state the options menu labels render.</summary>
    private OptionsMenuModel.OptionsState CurrentOptionsState()
    {
        EnsureOptionsPrefs();
        return new OptionsMenuModel.OptionsState(
            MusicOn: OptionsMusic?.Enabled ?? false,
            FilterChatsToAll: _optionsPrefs.FilterChatsToAll,
            LinearControls: _mouseFlight.LinearControls,
            FlipY: _mouseFlight.InvertPitch,
            VirtualJoystick: _mouseFlightEnabled,
            Deadzone: _mouseFlight.Deadzone,
            ForceFeedback: _optionsPrefs.EnableForceFeedback,
            Sound3DOn: OptionsSound?.Sound3DEnabled ?? _optionsPrefs.Enable3DSound,
#if BROWSER
            ChatMuteTarget: null,
            ChatMuteTargetMuted: false,
#else
            ChatMuteTarget: NewestDirectoryLine?.Sender,
            ChatMuteTargetMuted: NewestDirectoryLine is { } newest
                && _directoryChat.IsMuted(newest.SenderId),
#endif
            AudioQuality: OptionsSound?.Quality ?? _optionsPrefs.AudioQuality,
            // P948 — the live mouse sensitivity the Mouse Options labels act on.
            MouseSensitivity: _mouseFlight.MouseSensitivity,
            // P979 — the EFFECTIVE visual flags (profile group + launch override);
            // the Visuals page labels read and edit this object.
            Visuals: _visuals,
            // P989 — so a flag that depends on the HDR scene path can say it is
            // inactive (null before a flight session created the post-processor).
            HdrSceneActive: OptionsHdrSceneActive);
    }

    /// <summary>
    /// P979 — flip one Visuals-page toggle. The live object is mutated in place so
    /// the very next rendered frame uses the new value; the profile's own group is
    /// updated and saved separately (never the launch override's resolved value),
    /// so the choice survives a restart and an unrelated preferences save cannot
    /// persist an override.
    /// </summary>
    private void ToggleVisualFlag(string? key)
    {
        if (key is null || Rendering.VisualFlags.Find(key) is not { Kind: Rendering.VisualFlagKind.Toggle }) return;
        EnsureOptionsPrefs();
        bool on = !_visuals.IsOn(key);
        _visuals.SetToggle(key, on);
        ApplyOptionsRemoteTimeline(key, on);
        (_optionsPrefs.Visuals ??= new Rendering.VisualOptions()).SetToggle(key, on);
        _optionsPrefs.Save(_playerPrefsPath);
        Console.WriteLine($"[intro] visuals: {key} -> {(on ? "on" : "off")} (saved)");
    }

    /// <summary>P979 — step a Visuals-page numeric flag by the table's step,
    /// clamped to its bounds; live + saved like the toggles.</summary>
    private void StepVisualRange(string? key, int direction)
    {
        var definition = key is null ? null : Rendering.VisualFlags.Find(key);
        if (definition is not { Kind: Rendering.VisualFlagKind.Range }) return;
        EnsureOptionsPrefs();
        float step = definition.Step > 0f ? definition.Step : 1000f;
        float next = System.Math.Clamp(_visuals.Value(key!) + direction * step, definition.MinValue, definition.MaxValue);
        _visuals.SetValue(key!, next);
        (_optionsPrefs.Visuals ??= new Rendering.VisualOptions()).SetValue(key!, next);
        _optionsPrefs.Save(_playerPrefsPath);
        Console.WriteLine($"[intro] visuals: {key} -> {Rendering.VisualOptions.Format(next)} m (saved)");
    }

    /// <summary>P871b — native <c>contextMute</c> (<c>WinTrek/WinTrek.cpp:3731-3734</c>,
    /// the chat menu's 'M' item at :4310) toggles
    /// <c>PlayerInfo::SetMute</c> (<c>clintlib/clintlib.h:326</c>); a muted player's
    /// lines never reach the pane (<c>trekigc.cpp:4492</c>). The port applies it to
    /// the newest directory-chat sender (the port extension's chat panel) and
    /// records it on the lobby account so it survives a reconnect.</summary>
    partial void ToggleOptionsChatMute();

    /// <summary>Native low/medium/high software voice budgets, persisted and
    /// applied immediately in the shared options path (P902).</summary>
    private void CycleOptionsAudioQuality()
    {
        EnsureOptionsPrefs();
        _optionsPrefs.AudioQuality = Audio.AudioQualityPresets.Next(_optionsPrefs.AudioQuality);
        _optionsPrefs.Save();
        if (OptionsSound is { } sound) sound.Quality = _optionsPrefs.AudioQuality;
    }

    /// <summary>P893 — flip the positional-3D sound switch (Sound Options →
    /// "Positional 3D Sound"): persist it and drive the live sound manager
    /// immediately so the mix changes without a restart. With it off the mix is
    /// exactly the pre-P893 curve path.</summary>
    private void ToggleOptions3DSound()
    {
        EnsureOptionsPrefs();
        _optionsPrefs.Enable3DSound = !_optionsPrefs.Enable3DSound;
        _optionsPrefs.Save();
        if (OptionsSound is { } snd) snd.Sound3DEnabled = _optionsPrefs.Enable3DSound;
        Console.WriteLine($"[intro] sound options: positional 3D -> {(_optionsPrefs.Enable3DSound ? "on" : "off")}");
    }

    /// <summary>P871 / SIM-0179 — native <c>ToggleEnableFeedback</c>
    /// (<c>WinTrek/WinTrek.cpp:5308-5313</c>): flip the switch, persist it, and
    /// stop any running effect immediately when it is turned off.</summary>
    private void ToggleForceFeedback()
    {
        EnsureOptionsPrefs();
        _optionsPrefs.EnableForceFeedback = !_optionsPrefs.EnableForceFeedback;
        _optionsPrefs.Save();
        ForceFeedback.SetEnabled(_optionsPrefs.EnableForceFeedback);
        Console.WriteLine($"[intro] game options: force feedback -> {(_optionsPrefs.EnableForceFeedback ? "on" : "off")}");
    }

    /// <summary>P806 — native <c>ToggleFilterChatsToAll</c> (WinTrek.cpp:4706-4722):
    /// flip the "filter chats sent to all" switch, persist it, and re-filter the
    /// chat log (native drops CHAT_EVERYONE in <c>ReceiveChat</c>,
    /// <c>trekigc.cpp:4493</c>; the port applies it to the visible history).</summary>
    private void ToggleOptionsChatFilter()
    {
        EnsureOptionsPrefs();
        _optionsPrefs.FilterChatsToAll = !_optionsPrefs.FilterChatsToAll;
        _optionsPrefs.Save();
        _chatFilterChatsToAll = _optionsPrefs.FilterChatsToAll;
        // The Options screen also opens from the intro (no session yet): only the
        // in-flight/team-lobby pane has a log to re-window.
        ApplyOptionsChatFilter();
        Console.WriteLine($"[intro] game options: filter chats sent to all -> {(_chatFilterChatsToAll ? "on" : "off")}");
    }

    /// <summary>P806 — draw the current native menu page (root or submenu): the
    /// highlighted row and the labels, plus the native navigation hint. Drawing
    /// reads the cached preference snapshot only (P757b: no file I/O here).</summary>
    private void DrawPrivacySettings(float width, float height, Color title, Color dim)
    {
        EnsureOptionsPrefs();
        var state = CurrentOptionsState();
        var rows = _optionsMenu.Rows(state);
        string subtitle = _optionsMenu.Current switch
        {
            OptionsMenuModel.Page.Sound => "Sound Options",
            OptionsMenuModel.Page.Game => "Game Options",
            OptionsMenuModel.Page.Chat => "Game Options / Chat, Mute, Filter",
            OptionsMenuModel.Page.Mouse => "Game Options / Mouse Options",
            // P979 — the port's visual-enhancement flags (no native counterpart).
            OptionsMenuModel.Page.Visuals => "Visuals (enhancements, default off)",
            _ => "Graphics / Sound / Game / Controls",
        };
        var (firstY, rowStep) = OptionsMenuModel.LayoutRows(height, rows.Count);
        var frameRows = new List<ScreenRow>();
        var plates = new List<ScreenPlate>();
        float y = firstY;
        for (int i = 0; i < rows.Count; i++)
        {
            bool sel = i == _optionsMenu.Row0;
            var band = new RectangleF(60, y - 4, width - 120, 30);
            if (sel)
                plates.Add(new(band, ScreenPlateRole.Selection, new Color(0.08f, 0.35f, 0.33f, 0.55f)));
            string hot = rows[i].Hotkey == '\0' ? "" : $"  ({rows[i].Hotkey})";
            frameRows.Add(BandedRow(rows[i].Label, (sel ? "> " : "  ") + rows[i].Label + hot, 72, y, band,
                sel, sel ? ScreenInkRole.Title : ScreenInkRole.Plain, sel ? title : Color.White));
            y += rowStep;
        }
        string hint = _optionsMenu.Current == OptionsMenuModel.Page.Root
            ? "UP/DOWN select   ENTER open   ESC back"
            : "UP/DOWN select   ENTER toggle   ESC back";
        var lines = new List<ScreenTextRun>
        {
            new(hint, new Vector2(72, y + 18f), 1f, ScreenInkRole.Dim, dim),
        };
#if !BROWSER
        if (_benchmarkSharing?.Available == true)
            lines.Add(new("B: Anonymous benchmark sharing\n" + _benchmarkSharing.Status,
                new Vector2(72, y + 60f), 1f, ScreenInkRole.Plain, Color.White));
#endif
        DrawIntroSubView(BuildViewFrame("options", "Options", subtitle, frameRows, lines, plates,
            title, dim, Color.White));
    }

    partial void HandlePlatformOptionsCommand(OptionsMenuModel.OptionsCommand command, ref bool handled);
    partial void ApplyOptionsRemoteTimeline(string key, bool on);
    partial void ApplyOptionsChatFilter();
}
