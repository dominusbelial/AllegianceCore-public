using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Platform;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private Game1? _game;
    private IJSObjectReference? _platform;
    private IJSInProcessObjectReference? _briefingDecoder;
    private Task? _briefingPreparation;
    private DotNetObjectReference<Menu>? _reference;
    private readonly ResidentProfileStorage _storage = new();
    private readonly CancellationTokenSource _lifetime = new();
    private StagedAssetSource? _assets;
    private StagedManifest? _joinManifest;
    private readonly Dictionary<string, byte[]> _assetMemoryFallback = new(StringComparer.Ordinal);
    private bool _joinStaged, _loading, _attempted, _flushPending, _disposed, _captureRequested;
    private string? _error;
    private string _status = "Preparing verified starter pack…", _persistence = "Preferences in memory", _audioStatus = "Enable menu sound";
    private int _progress;
    private HudStyle _style = HudStyle.Modern;
    private long _savedRevision = -1;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (_loading || _disposed) return;
        _loading = true; _joinStaged = false; _joinManifest = null; _sectorManifest = null; _error = null; _progress = 0; StateHasChanged();
        try
        {
            _platform ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", await JsRuntime.InvokeAsync<string>("preview.asset", "./game-platform.js"));
            if (!_attempted)
            {
                var snapshot = await _platform.InvokeAsync<Dictionary<string, string>>("settingsRead");
                _storage.Import(snapshot);
                ProfileStorage.Default = _storage;
                var hud = HudSettings.Load("hud.json", _storage.Exists("player.json"), _storage);
                _style = hud.Style;
                _savedRevision = _storage.Revision;
                // Remember the initial migration/default choice before a later
                // player.json write can make this new profile look like a legacy one.
                if (!_storage.Exists("hud.json")) hud.Save("hud.json", _storage);
            }
            bool retry = _attempted;
            var stager = new AssetStager(Http, new BrowserAssetCache(_platform, _assetMemoryFallback), request =>
            {
                request.SetBrowserResponseStreamingEnabled(true);
                if (retry) request.SetBrowserRequestCache(BrowserRequestCache.Reload);
            }) { CommitCacheManifest = false };
            _attempted = true;
            var manifest = await stager.ReadManifestAsync(_lifetime.Token);
            _assets = await stager.StageAsync(manifest, (percent, status) =>
            {
                _progress = percent; _status = status; StateHasChanged();
            }, _lifetime.Token);
            var dimensions = await JsRuntime.InvokeAsync<int[]>("menuViewport");
            StopGame();
            _game = new Game1(_assets, _style, dimensions[0], dimensions[1]);
            _game.OpenOnline = () => _ = OpenOnlineAsync();
            _game.DownloadScreenshot = () => _ = ScreenshotAsync();
            _reference ??= DotNetObjectReference.Create(this);
            await JsRuntime.InvokeVoidAsync("menuText", _reference);
            await JsRuntime.InvokeVoidAsync("preview.startFrames", _reference);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            StopGame();
            _error = "The starter pack or renderer could not start. " + error.Message;
        }
        finally { _loading = false; if (!_disposed) StateHasChanged(); }
    }
    private async Task SetStyleAsync(HudStyle style)
    {
        _style = style;
        _game?.SelectHudStyle(style);
        var settings = HudSettings.Load("hud.json", true, _storage);
        settings.Style = style;
        settings.Save("hud.json", _storage);
        await FlushAsync();
        // The shared intro has identical artwork in both styles; the style is a persisted HUD preference.
        StateHasChanged();
    }
    private async Task FlushAsync()
    {
        if (_flushPending || _platform is null) return;
        _flushPending = true;
        try
        {
            while (_savedRevision != _storage.Revision && !_disposed)
            {
                long revision = _storage.Revision;
                bool persisted = await _platform.InvokeAsync<bool>("settingsWrite", _storage.Snapshot());
                _persistence = persisted ? "Preferences saved" : "Browser storage unavailable — preferences last until this tab closes";
                _savedRevision = revision;
                StateHasChanged();
            }
        }
        finally { _flushPending = false; }
    }
    private void EnableAudio() => _audioStatus = _game?.EnableMenuAudio() == true ? "Menu sound on" : "Menu sound unavailable";
    private Task ScreenshotAsync()
    {
        _captureRequested = true;
        return Task.CompletedTask;
    }
    private async Task CaptureFrameAsync()
    {
        try { if (_platform is not null) await _platform.InvokeVoidAsync("screenshot"); }
        catch (JSException error) { Console.Error.WriteLine("[browser] Screenshot download failed: " + error.Message); }
    }
    [JSInvokable]
    public void TextInput(string text) => _game?.InputText(text);
    [JSInvokable]
    public void SetKeyboardFocus(bool focused) => _game?.SetKeyboardFocus(focused);
    [JSInvokable]
    public bool TickDotNet()
    {
        try
        {
            if (_game is null) return false;
            if (!_game.Started) { _game.Run(); _game.Started = true; }
            _game.Tick();
            RefreshOnline();
            // Snapshot in this RAF submission, before WebGL discards the drawing buffer.
            if (_captureRequested) { _captureRequested = false; _ = CaptureFrameAsync(); }
            if (_savedRevision != _storage.Revision) _ = FlushAsync();
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            _error = "Game1 rendering stopped. " + error.Message;
            StopGame(); StateHasChanged(); return false;
        }
    }
    [JSInvokable]
    public void FlightKeys(string[] codes) => _game?.FlightKeys(codes);
    [JSInvokable]
    public void PresentationSize(int width, int height) => _game?.SetPresentationSize(width, height);
    [JSInvokable]
    public void FlightGamepad(float[] axes, float[] buttons) => _game?.FlightGamepad(axes, buttons);
    [JSInvokable]
    public void FlightMouseButtons(int buttons) => _game?.FlightMouseButtons(buttons);
    [JSInvokable]
    public void PointerMotion(int dx, int dy) => _game?.PointerMotion(dx, dy);
    [JSInvokable]
    public void PointerCapture(bool locked) => _game?.PointerCapture(locked);
    [JSInvokable]
    public void ReleaseFlightInput() => _game?.ReleaseFlightInput();
    [JSInvokable]
    public void ContextLost() { _error = "WebGL context lost. Reload this page to restore the game."; StopGame(); StateHasChanged(); }
    [JSInvokable]
    public object Metrics() => new { screen = _game?.Screen, optionsPage = _game?.OptionsPage,
        invertMouseY = _game?.InvertMouseY, menuSoundEnabled = _game?.MenuSoundEnabled, managedHeapBytes = _game?.ManagedHeapBytes,
        residentBytes = _assets?.ResidentBytes, sectors = new { active = _sectorStreamer?.ActiveSector, retries = _sectorStreamer?.RetryCount, completed = _sectorStreamer?.CompletedPacks, status = SectorStatus }, settingsPending = _flushPending, style = _style.ToString(), gameStyle = _game?.HudStyleName, online = new { state = _onlineState, pendingJoins = _pendingJoins, decoderReady = _briefingDecoder is not null, shipId = _session?.World.MyShipId, resumeSuccesses = _session?.ResumeSuccesses, resumeAttempts = _session?.ResumeAttempts, reconnecting = _session?.Reconnecting, malformed = _session?.MalformedMessageCount },
        launch = new { busy = _onlineBusy, guard = _launchGuard, readySends = _readySends, shipId = _session?.World.MyShipId,
            sideId = _session?.World.MySideId, stage = _session?.World.Mission?.Stage, inGame = _session?.World.InGame,
            joinedShipId = _session?.World.JoinedShipId,
            shipsHasOwn = _session?.World.MyShipId is { } ownId && _session.World.Ships.ContainsKey(ownId),
            shipsHasJoined = _session?.World.JoinedShipId is { } joinedId && _session.World.Ships.ContainsKey(joinedId) }, flight = _game?.FlightMetrics };
    private void StopGame()
    {
        var game = _game; _game = null;
        try { game?.Dispose(); }
        catch (Exception error) { Console.Error.WriteLine("[browser] Cleanup after failed initialization: " + error.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _lifetime.Cancel();
        await StopSectorStreamingAsync();
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        StopGame();
        if (_reference is not null) await JsRuntime.InvokeVoidAsync("menuDetachText");
        _reference?.Dispose(); _lifetime.Dispose();
        if (_platform is not null) await _platform.DisposeAsync();
        if (_briefingPreparation is { } preparation)
            try { await preparation; } catch (Exception) when (_disposed) { }
        if (_briefingDecoder is not null) await _briefingDecoder.DisposeAsync();
    }
}
