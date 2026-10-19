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
    private void UpdatePrivacySettings()
    {
        var keys = Keyboard.GetState();
        if (keys.IsKeyDown(Keys.Escape) && !_iPrivacyKeys.IsKeyDown(Keys.Escape)) _introView = IntroView.Main;
        if (keys.IsKeyDown(Keys.K) && !_iPrivacyKeys.IsKeyDown(Keys.K)) { ToggleKeyBindings(); _introView = IntroView.Main; }
        if (keys.IsKeyDown(Keys.G)) { _graphicsSettingsVisible = true; _introView = IntroView.Main; }
        if (keys.IsKeyDown(Keys.S) && _benchmarkSharing?.Available == true) _sharePanel = true;
        _iPrivacyKeys = keys;
    }
    private void DrawPrivacySettings(float width, Color title, Color dim)
    {
        DrawIntroTitle(width, "Settings", "K: Key bindings   G: Graphics   Escape: Back", title, dim);
        if (_benchmarkSharing?.Available == true)
            _spriteBatch.DrawString(_hudSmall, "S: Anonymous benchmark sharing\n" + _benchmarkSharing.Status,
                new Vector2(60, 210), Color.White);
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
