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

    /// <summary>P757 — the intro Options menu (native
    /// <c>WinTrek::ShowOptionsMenu</c>, WinTrek.cpp:4429-4442 + GetSubMenu
    /// :4502-4590): four native-named groups, each backed by the port's real
    /// controls — Graphics Options (the graphics settings overlay), Sound
    /// Options (music on/off + volume), Game Options (chat filter default +
    /// "Map Keys and Controls", both native Game Options items) and the key
    /// bindings popup. Up/Down + Enter navigate; the inherited hotkeys are
    /// G (Graphics), S (benchmark sharing) and K (Map Keys and Controls), with
    /// the new Sound/Game toggles on the previously unused A/F keys.</summary>
    private void UpdateOptionsMenu(KeyboardState? ks = null)
    {
        var keys = ks ?? Keyboard.GetState();
        EnsureOptionsPrefs();
        bool Press(Keys key) => keys.IsKeyDown(key) && !_iPrivacyKeys.IsKeyDown(key);

        bool esc = Press(Keys.Escape);
        if (Press(Keys.Up)) _optionsMenuRow = (_optionsMenuRow + 3) % 4;
        if (Press(Keys.Down)) _optionsMenuRow = (_optionsMenuRow + 1) % 4;

        void Activate(int row)
        {
            switch (row)
            {
                case 0:   // Graphics Options (native idmOptions)
                    _graphicsSettingsVisible = true;
                    _introView = IntroView.Main;
                    break;
                case 1:   // Sound Options (native idmSoundOptions: the port's real
                          // sound controls are the music toggle + volume)
                    if (_music is not null) _music.Toggle();
                    break;
                case 2:   // Game Options (native idmGameOptions): chat filter + controls
                    ToggleOptionsChatFilter();
                    break;
                case 3:   // Map Keys and Controls (native Game Options 'C')
                    ToggleKeyBindings();
                    _introView = IntroView.Main;
                    break;
            }
        }

        // Old, documented hotkeys first (G/S/K keep their pre-P757 meaning),
        // then the new one-press Sound/Game toggles on unused keys.
        if (esc) _introView = IntroView.Main;                 // native OnCancel
        else if (Press(Keys.G)) Activate(0);
        else if (Press(Keys.S)) { if (_benchmarkSharing?.Available == true) _sharePanel = true; }
        else if (Press(Keys.K)) Activate(3);
        else if (Press(Keys.A)) Activate(1);
        else if (Press(Keys.F)) Activate(2);
        else if (Press(Keys.Enter)) Activate(_optionsMenuRow);

        if (Press(Keys.M) && _music is not null) _music.Toggle();
        if (Press(Keys.OemPlus) && _music is not null) _music.VolumeUp();
        if (Press(Keys.OemMinus) && _music is not null) _music.VolumeDown();
        _iPrivacyKeys = keys;
    }

    private void ToggleOptionsChatFilter()
    {
        EnsureOptionsPrefs();
        _optionsPrefs.ChatFilterPlayersOnly = !_optionsPrefs.ChatFilterPlayersOnly;
        _optionsPrefs.Save();
        _chatPaneFilterMode = _optionsPrefs.ChatFilterPlayersOnly ? 1 : 0;
        ApplyChatPaneFilterMode();
        Console.WriteLine($"[intro] game options: chat filter -> {(_optionsPrefs.ChatFilterPlayersOnly ? "PLAYERS" : "ALL")}");
    }

    private void DrawPrivacySettings(float width, float height, Color title, Color dim)
    {
        // Drawing reads the cached snapshot only (P757b): Load() does file I/O
        // and must never run from the Draw path. UpdateOptionsMenu loads it once
        // before Draw on the same frame the Settings view first becomes active.
        bool playersOnly = _optionsPrefs.ChatFilterPlayersOnly;
        DrawIntroTitle(width, "Options", "Graphics / Sound / Game / Controls", title, dim);
        string[] rows =
        {
            "Graphics Options (G)",
            "Sound Options (A toggle, +/- volume)",
            $"Game Options (F: chat filter: {(playersOnly ? "PLAYERS" : "ALL")})",
            "Map Keys and Controls (K)",
        };
        float y = height * 0.36f;
        for (int i = 0; i < rows.Length; i++)
        {
            bool sel = i == _optionsMenuRow;
            if (sel)
                _spriteBatch.Draw(_whitePixel, new Rectangle(60, (int)y - 4, (int)width - 120, 30),
                    new Color(0.08f, 0.35f, 0.33f, 0.55f));
            _spriteBatch.DrawString(_hudSmall, (sel ? "> " : "  ") + rows[i], new Vector2(72, y), sel ? title : Color.White);
            y += 40f;
        }
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER open   ESC back", new Vector2(72, y + 18f), dim);
        if (_benchmarkSharing?.Available == true)
            _spriteBatch.DrawString(_hudSmall, "S: Anonymous benchmark sharing\n" + _benchmarkSharing.Status,
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
