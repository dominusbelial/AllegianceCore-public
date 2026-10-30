using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private BenchmarkSharing? _benchmarkSharing;
    private bool _sharePanel, _shareView, _shareSessionCounted, _shareSessionPending;
    private int _shareScroll, _sharePayloadIndex;
    private KeyboardState _shareKeys;
    private long _sharePoll;
    private Task _shareWork = Task.CompletedTask;
    private void ShareWork(Func<Task> action)
    {
        // Ordered continuations also keep hardware probing/file I/O off the render thread.
        _shareWork = _shareWork.ContinueWith(async _ =>
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { /* Sharing cannot fail the game. */ }
        }, TaskScheduler.Default).Unwrap();
    }
    private void InitializeBenchmarkSharing(string? endpoint)
    {
        _benchmarkSharing = new(Path.Combine(PlayerConfig.DirectoryPath, "benchmark-sharing"), endpoint);
        ShareWork(() => _benchmarkSharing.InitializeAsync());
    }
    private void RecordBenchmarkSharing(BenchmarkResult result)
    {
        string model = _gpuTimer?.Renderer ?? GraphicsDevice.Adapter.Description;
        string? driver = _gpuTimer?.DriverVersion;
        ShareWork(async () =>
        {
            var payload = SharePayload.From(result, ShareHardware.Capture(model, driver, result.Width, result.Height));
            await _benchmarkSharing!.RecordAsync(payload).ConfigureAwait(false);
            await _benchmarkSharing.PumpAsync().ConfigureAwait(false);
        });
    }
    private void UpdateBenchmarkSharing()
    {
        if (_benchmark is not null || _benchmarkSharing is null || (!_shareSessionPending && Environment.TickCount64 < _sharePoll) || !_shareWork.IsCompleted) return;
        _sharePoll = Environment.TickCount64 + 30000;
        var settings = ShareSettings.From(CurrentBenchmarkSettings());
        bool session = _shareSessionPending; _shareSessionPending = false;
        ShareWork(async () =>
        {
            await _benchmarkSharing.ObserveSettingsAsync(settings, session).ConfigureAwait(false);
            await _benchmarkSharing.PumpAsync().ConfigureAwait(false);
        });
    }
    private void ObserveBenchmarkSharingSession()
    {
        if (_benchmarkSharing is null || _shareSessionCounted) return;
        _shareSessionCounted = true;
        // A direct-connect acknowledgement can precede InitializeBenchmark applying saved settings.
        // Capture on the next update, after initialization completes.
        _shareSessionPending = true;
    }
    private KeyboardState _iPrivacyKeys;
    // P757b — the options screen's preference snapshot. Loaded once when the
    // screen opens (never from Draw), so drawing does not touch player.json.
    private IntroPreferences _optionsPrefs = new();
    private bool _optionsPrefsLoaded;

    private void EnsureOptionsPrefs()
    {
        if (_optionsPrefsLoaded) return;
        _optionsPrefs = IntroPreferences.Load();
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
            // P871 / SIM-0412 — every MDL button click starts its cue and forces
            // the audio update NOW (native SoundIDEventSink, trekmdl.cpp:452-478;
            // the click must not wait behind the button's own work).
            if (_sound is { } buttonAudio)
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
                    _optionsMenu.Activate(state);          // descend one level
                    break;
                case OptionsMenuModel.OptionsCommand.MapKeysAndControls: // native idmConfigure 'C'
                    ToggleKeyBindings();
                    _introView = IntroView.Main;
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleMusic:         // native idmToggleMusic
                    _music?.Toggle();
                    break;
                case OptionsMenuModel.OptionsCommand.Toggle3DSound:       // P893 — port addition (no native counterpart)
                    ToggleOptions3DSound();
                    break;
                case OptionsMenuModel.OptionsCommand.MusicVolumeUp:       // native idmMusicVolumeUp
                    _music?.VolumeUp();
                    break;
                case OptionsMenuModel.OptionsCommand.MusicVolumeDown:     // native idmMusicVolumeDown
                    _music?.VolumeDown();
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
                    _mouseFlight.InvertPitch = !_mouseFlight.InvertPitch;
                    Console.WriteLine($"[intro] game options: flip Y -> {(_mouseFlight.InvertPitch ? "on" : "off")}");
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleVirtualJoystick:  // native idmToggleVirtualJoystick 'J'
                    _mouseFlightEnabled = !_mouseFlightEnabled;
                    if (_mouseFlightEnabled) _mouseFlight.Enable(); else _mouseFlight.Reset();
                    Console.WriteLine($"[intro] game options: virtual joystick -> {(_mouseFlightEnabled ? "on" : "off")}");
                    break;
                case OptionsMenuModel.OptionsCommand.ToggleForceFeedback:     // native idmToggleEnableFeedback 'E'
                    ToggleForceFeedback();
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
        if (Press(Keys.B)) { if (_benchmarkSharing?.Available == true) _sharePanel = true; }
        if (Press(Keys.M) && _music is not null) _music.Toggle();
        if (Press(Keys.OemPlus) && _music is not null) _music.VolumeUp();
        if (Press(Keys.OemMinus) && _music is not null) _music.VolumeDown();
        _iPrivacyKeys = keys;
    }

    /// <summary>P806 — the live switch state the options menu labels render.</summary>
    private OptionsMenuModel.OptionsState CurrentOptionsState()
    {
        EnsureOptionsPrefs();
        return new OptionsMenuModel.OptionsState(
            MusicOn: _music?.Enabled ?? false,
            FilterChatsToAll: _optionsPrefs.FilterChatsToAll,
            LinearControls: _mouseFlight.LinearControls,
            FlipY: _mouseFlight.InvertPitch,
            VirtualJoystick: _mouseFlightEnabled,
            Deadzone: _mouseFlight.Deadzone,
            ForceFeedback: _optionsPrefs.EnableForceFeedback,
            Sound3DOn: _sound?.Sound3DEnabled ?? _optionsPrefs.Enable3DSound,
            ChatMuteTarget: NewestDirectoryLine?.Sender,
            ChatMuteTargetMuted: NewestDirectoryLine is { } newest
                && _directoryChat.IsMuted(newest.SenderId));
    }

    /// <summary>P871b — native <c>contextMute</c> (<c>WinTrek/WinTrek.cpp:3731-3734</c>,
    /// the chat menu's 'M' item at :4310) toggles
    /// <c>PlayerInfo::SetMute</c> (<c>clintlib/clintlib.h:326</c>); a muted player's
    /// lines never reach the pane (<c>trekigc.cpp:4492</c>). The port applies it to
    /// the newest directory-chat sender (the port extension's chat panel) and
    /// records it on the lobby account so it survives a reconnect.</summary>
    private void ToggleOptionsChatMute()
    {
        var newest = NewestDirectoryLine;
        if (newest is null)
        {
            Console.WriteLine("[intro] chat options: no directory chat sender to mute yet");
            return;
        }
        bool mute = !_directoryChat.IsMuted(newest.SenderId);
        SetDirectorySenderMuted(newest.Sender, mute);
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
        if (_sound is { } snd) snd.Sound3DEnabled = _optionsPrefs.Enable3DSound;
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
        if (_session is not null) _lastChatCount = FilteredChatCount();
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
            _ => "Graphics / Sound / Game / Controls",
        };
        DrawIntroTitle(width, "Options", subtitle, title, dim);
        float y = height * 0.36f;
        for (int i = 0; i < rows.Count; i++)
        {
            bool sel = i == _optionsMenu.Row0;
            if (sel)
                _spriteBatch.Draw(_whitePixel, new Rectangle(60, (int)y - 4, (int)width - 120, 30),
                    new Color(0.08f, 0.35f, 0.33f, 0.55f));
            string hot = rows[i].Hotkey == '\0' ? "" : $"  ({rows[i].Hotkey})";
            _spriteBatch.DrawString(_hudSmall, (sel ? "> " : "  ") + rows[i].Label + hot,
                new Vector2(72, y), sel ? title : Color.White);
            y += 40f;
        }
        string hint = _optionsMenu.Current == OptionsMenuModel.Page.Root
            ? "UP/DOWN select   ENTER open   ESC back"
            : "UP/DOWN select   ENTER toggle   ESC back";
        _spriteBatch.DrawString(_hudSmall, hint, new Vector2(72, y + 18f), dim);
        if (_benchmarkSharing?.Available == true)
            _spriteBatch.DrawString(_hudSmall, "B: Anonymous benchmark sharing\n" + _benchmarkSharing.Status,
                new Vector2(72, y + 60f), Color.White);
    }
    private bool UpdateBenchmarkSharingPanel(KeyboardState keys)
    {
        if (!_sharePanel || _benchmarkSharing?.Available != true) { _shareKeys = keys; return false; }
        bool Press(Keys k) => keys.IsKeyDown(k) && !_shareKeys.IsKeyDown(k);
        if (Press(Keys.Escape))
        {
            if (_shareView) _shareView = false;
            else { _sharePanel = false; _benchEsc = _iEsc = true; _iPrivacyKeys = keys; }
        }
        if (Press(Keys.V)) { _shareView = !_shareView; _shareScroll = _sharePayloadIndex = 0; }
        if (Press(Keys.Y)) ShareWork(async () => { await _benchmarkSharing.SetConsentAsync(true); await _benchmarkSharing.PumpAsync(); });
        if (Press(Keys.N)) ShareWork(() => _benchmarkSharing.SetConsentAsync(false));
        if (Press(Keys.D)) ShareWork(async () => { await _benchmarkSharing.DeleteAsync(); await _benchmarkSharing.PumpAsync(); });
        if (_shareView)
        {
            int length = Math.Max(1, _benchmarkSharing.Outgoing.Count);
            if (Press(Keys.Right)) { _sharePayloadIndex = (_sharePayloadIndex + 1) % length; _shareScroll = 0; }
            if (Press(Keys.Left)) { _sharePayloadIndex = (_sharePayloadIndex + length - 1) % length; _shareScroll = 0; }
            if (Press(Keys.Down) || Press(Keys.PageDown)) _shareScroll += Press(Keys.PageDown) ? 20 : 1;
            if (Press(Keys.Up) || Press(Keys.PageUp)) _shareScroll = Math.Max(0, _shareScroll - (Press(Keys.PageUp) ? 20 : 1));
        }
        _shareKeys = keys;
        return true;
    }
    private void DrawBenchmarkSharingPanel()
    {
        if (!_sharePanel || _benchmarkSharing?.Available != true) return;
        _spriteBatch.Begin();
        _spriteBatch.Draw(_whitePixel, GraphicsDevice.Viewport.Bounds, new Color(4, 12, 20, 255));
        string heading = "Share these results to help improve the recommendations?\n" + _benchmarkSharing.Status +
            "\nY: Yes (remember)   N: No (remember)   V: View what's sent\nD: Delete my shared data / reset install ID   Escape: Back";
        _spriteBatch.DrawString(_hudSmall, heading, new Vector2(18, 20), Color.Cyan);
        if (_shareView)
        {
            var outgoing = _benchmarkSharing.Outgoing;
            _sharePayloadIndex = Math.Clamp(_sharePayloadIndex, 0, Math.Max(0, outgoing.Count - 1));
            string[] lines = (outgoing.Count > 0 ? outgoing[_sharePayloadIndex] : _benchmarkSharing.Preview).Split('\n');
            int columns = Math.Max(1, (GraphicsDevice.Viewport.Width - 36) / Math.Max(1, (int)_hudSmall.MeasureString("M").X));
            // Soft-wrap the display only. The inspected/uploaded JSON string is unchanged.
            lines = lines.SelectMany(line => Enumerable.Range(0, Math.Max(1, (line.Length + columns - 1) / columns))
                .Select(i => line.Substring(i * columns, Math.Min(columns, line.Length - i * columns)))).ToArray();
            int count = Math.Max(1, (GraphicsDevice.Viewport.Height - 190) / Math.Max(1, _hudSmall.LineSpacing));
            _shareScroll = Math.Min(_shareScroll, Math.Max(0, lines.Length - count));
            string visible = string.Join('\n', lines.Skip(_shareScroll).Take(count));
            _spriteBatch.DrawString(_hudSmall, visible, new Vector2(18, 140), Color.White);
            _spriteBatch.DrawString(_hudSmall, $"Payload {_sharePayloadIndex + 1}/{Math.Max(1, outgoing.Count)} (Left/Right); JSON rows {_shareScroll + 1}-{Math.Min(lines.Length, _shareScroll + count)}/{lines.Length}; arrows / PageUp / PageDown\nBefore first opt-in, installId is null. Yes creates the random ID; V then shows the exact queued JSON.",
                new Vector2(18, GraphicsDevice.Viewport.Height - 46), Color.Yellow);
        }
        else _spriteBatch.DrawString(_hudSmall,
            "Default: No. Results are always saved on this device.\nSharing sends hardware, timings and settings using a random, resettable ID.\nNo pilot names, machine names, paths or IP addresses are stored.\nRaw shared data: 24 months. Anonymous aggregates: indefinitely.\nDelete is queued if offline. Turning sharing off cancels pending uploads.\nDeletion also resets the ID and turns sharing off.\nSee docs/benchmark/PRIVACY.md for details.",
            new Vector2(18, 160), Color.White);
        _spriteBatch.End();
    }
}
