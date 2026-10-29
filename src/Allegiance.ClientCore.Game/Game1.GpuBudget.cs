using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly EffectLights _effectLights = new();
    private bool _allowSoft = true;
    private bool? _bloomOverride;
    private readonly List<Allegiance.ClientCore.State.HullHitEntity> _lightImpacts = new();
    private void ApplyEffectsDetail(Allegiance.ClientCore.State.EffectsDetail detail)
    {
        if (!Enum.IsDefined(detail)) detail = Allegiance.ClientCore.State.EffectsDetail.Medium;
        _upscaling.Effects = detail;
        _effectLights.Detail = _enhancedVfx ? detail : Allegiance.ClientCore.State.EffectsDetail.Low;
        _effectLights.Clear();
        _lightImpacts.Clear();
        _softEnabled = _enhancedVfx && _allowSoft && detail != Allegiance.ClientCore.State.EffectsDetail.Low;
        _bloomEnabled = _enhancedVfx && detail != Allegiance.ClientCore.State.EffectsDetail.Low &&
            (_bloomOverride ?? detail == Allegiance.ClientCore.State.EffectsDetail.High);
        if (_post?.Bloom is { } bloom) bloom.Polished = detail != Allegiance.ClientCore.State.EffectsDetail.Low;
    }
    private readonly HudSettings _hudSettings = HudSettings.Load(HudSettings.DefaultPath,
        File.Exists(IntroPreferences.FilePath) || File.Exists(UpscalingSettings.DefaultPath) || File.Exists(KeyBindingStore.ResolvePath()));
    private HudRenderer ActiveHud => _hudSettings.Style == HudStyle.Modern ? _modernHudRenderer : _hudRenderer;
    private readonly UpscalingSettings _upscaling = UpscalingSettings.Load(UpscalingSettings.DefaultPath);
    private readonly DynamicResolutionController _resolutionController = new();
    private readonly FlightGcLatency _flightGc = new(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_GC_LOWLATENCY") == "1");
    private ClientFrameTelemetry? _frameTelemetry;
    private GlFrameTimer? _gpuTimer;
    private FsrPostProcessor? _fsr;
    private bool _graphicsSettingsVisible, _graphicsOpenWasDown;
    /// <summary>P805 (row 470) — native ShowChatHistoryHUD toggle (menu-only,
    /// NOT persisted: WinTrek.cpp:2897,5074-5078,3934), default on.</summary>
    private bool _chatHistoryVisible = true;
    private KeyboardState _graphicsPrevious;
    private int _graphicsRow;
    /// <summary>P828 — the number of rows in the Ctrl+F6 settings panel (13 → 16
    /// with P805's Center/Target HUD + Chat History, 16 → 19 with the radar label
    /// declutter rows, 19 → 20 with the P836 HUD level row, 20 → 21 with native's
    /// Debris cycle). Up/Down wrap on it.</summary>
    private const int GraphicsRowCount = 21;
    private Action<long, bool, double>? _receiveGpu;
    private long _budgetStart;
    private double _budgetDuration;
    private string? _budgetCapture;
    private long _resolutionGpuGeneration;
    private int _postWidth, _postHeight;
    private bool _scaledThisFrame;

    private void InitializeGpuBudget()
    {
        try { _hudSettings.Save(HudSettings.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[hud2] cannot save HUD preferences: {e.Message}"); }
        _nativeShipRenderer.HdTexturesEnabled = _upscaling.HdTextures;
        _nativeShipRenderer.HdPbrEnabled = _upscaling.HdPbr;
        SyncHdMaterialMode();
        _hdMaterials.Flashes = _effectLights;
        _fsr = new(GraphicsDevice, _shaders!);
        _postWidth = GraphicsDevice.PresentationParameters.BackBufferWidth;
        _postHeight = GraphicsDevice.PresentationParameters.BackBufferHeight;
        _budgetStart = Stopwatch.GetTimestamp();
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _budgetDuration);
        _budgetCapture = Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_CAPTURE");
        _resolutionController.Reset(UpscalingSettings.Scale(_upscaling.Preset));
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF") == "1" || _upscaling.DynamicResolution)
            EnableFrameTelemetry();
    }
    /// <summary>P836 — persist hud.json from the in-game cycle key without
    /// surfacing an error (same swallow shape as the settings panel).</summary>
    private void SaveHudSettingsQuietly()
    {
        try { _hudSettings.Save(HudSettings.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[hud2] cannot save HUD preferences: {e.Message}"); }
    }

    /// <summary>HD-5 — push the two player settings (HD textures, experimental HD
    /// lighting) into the shared <see cref="HdMaterialRenderer.Mode"/> so every
    /// renderer follows one coherent pack mode. Null-safe for menu unit tests that
    /// drive the menu before the renderer material is constructed.</summary>
    private void SyncHdMaterialMode()
    {
        if (_hdMaterials is not null)
            _hdMaterials.Mode = HdMaterialRenderer.ModeFor(_upscaling.HdTextures, _upscaling.HdPbr);
    }
    private void EnableFrameTelemetry()
    {
        if (_frameTelemetry is not null) return;
        _frameTelemetry = new(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_PERF_CSV"));
        _receiveGpu = _frameTelemetry.ReceiveGpu;
        _gpuTimer = GlFrameTimer.TryCreate();
        _frameTelemetry.GpuAvailable = _gpuTimer is not null;
        Console.WriteLine($"[p643] GPU timer: {(_gpuTimer is null ? "unavailable (present wait is CPU blocking, not GPU time)" : "GL_TIME_ELAPSED scene/post, asynchronous")}");
    }
    private bool PrepareUpscaling(int width, int height)
    {
        bool enabled = _upscaling.Upscaling == UpscalingMode.FSR1 && _fsr is { Ready: true };
        float maximum = UpscalingSettings.Scale(_upscaling.Preset);
        float scale = maximum;
        if (enabled && _upscaling.DynamicResolution)
        {
            EnableFrameTelemetry();
            if (_frameTelemetry!.GpuGeneration != _resolutionGpuGeneration)
            {
                _resolutionGpuGeneration = _frameTelemetry.GpuGeneration;
                _resolutionController.Observe(_frameTelemetry.LatestGpuMs, _upscaling.TargetFrameMs, maximum);
            }
            scale = _resolutionController.CurrentScale;
        }
        var dimensions = enabled ? UpscalingSettings.Dimensions(width, height, scale) : (width, height);
        if (_postWidth != dimensions.Item1 || _postHeight != dimensions.Item2)
        {
            _post?.SetSize(dimensions.Item1, dimensions.Item2);
            _postWidth = dimensions.Item1; _postHeight = dimensions.Item2;
        }
        if (enabled) _fsr!.SetSize(width, height, scale);
        _scaledThisFrame = enabled;
        return enabled;
    }
    /// <summary>P828 — the settings panel's height: header + one 46 px row per
    /// option + footer, so the 19 rows (up from 16) stay inside the panel. Pure
    /// so a test can pin that the panel outgrew the old fixed 700 px.</summary>
    internal static int GraphicsPanelHeight(int rows) => 60 + 46 * rows + 30;
    private bool UpdateGraphicsSettings(KeyboardState ks)
    {
        bool open = KeyOf("OpenGraphicsSettings").Matches(ks);
        if (open && !_graphicsOpenWasDown && !_chatActive && !_qcMenuOpen) _graphicsSettingsVisible = !_graphicsSettingsVisible;
        _graphicsOpenWasDown = open;
        if (!_graphicsSettingsVisible) { _graphicsPrevious = ks; return false; }
        _flightGc.SetFlying(false);
        bool Press(Keys k) => ks.IsKeyDown(k) && !_graphicsPrevious.IsKeyDown(k);
        if (Press(Keys.Escape)) _graphicsSettingsVisible = false;
        if (Press(Keys.Down)) _graphicsRow = (_graphicsRow + 1) % GraphicsRowCount;
        if (Press(Keys.Up)) _graphicsRow = (_graphicsRow + GraphicsRowCount - 1) % GraphicsRowCount;
        int direction = Press(Keys.Right) ? 1 : Press(Keys.Left) ? -1 : 0;
        bool changed = direction != 0;
        if (changed)
        {
            switch (_graphicsRow)
            {
                case 9: ApplyEffectsDetail((Allegiance.ClientCore.State.EffectsDetail)(((int)_upscaling.Effects + direction + 3) % 3)); break;
                case 6: _hudSettings.Style = _hudSettings.Style == HudStyle.Classic ? HudStyle.Modern : HudStyle.Classic; break;
                case 7: _hudSettings.Size = Math.Clamp(_hudSettings.Size + direction * .05f, .75f, 1.5f); break;
                case 8: _hudSettings.ColorBlindSafe = !_hudSettings.ColorBlindSafe; break;
                case 11: _hudSettings.RoundRadar = !_hudSettings.RoundRadar; break;
                // HUD-19 — native Graphics Options "Joystick Indicator: On/Off"
                // (WinTrek.cpp:4507,4526), toggled/saved at WinTrek.cpp:5222-5238.
                case 12: _hudSettings.ShowJoystickIndicator = !_hudSettings.ShowJoystickIndicator; break;
                // P805 (row 470) — native Graphics Options toggles: Center HUD
                // (idmToggleCenterHUD 615, persisted WinTrek.cpp:5082-5086), Target
                // HUD (616, :5090-5094) and the menu-only Chat History (614,
                // :5074-5078, NOT persisted).
                case 13: _hudSettings.ShowCenterHud = !_hudSettings.ShowCenterHud; break;
                case 14: _hudSettings.ShowTargetHud = !_hudSettings.ShowTargetHud; break;
                case 15: _chatHistoryVisible = !_chatHistoryVisible; break;
                // P828 — the Modern-HUD radar label declutter: on/off, and the
                // nearest-N ordinary label budget (left/right steps by 2).
                case 16: _hudSettings.RadarDeclutter = !_hudSettings.RadarDeclutterOn; break;
                case 17: _hudSettings.RadarLabelMax = Math.Clamp(_hudSettings.RadarLabelMax + direction * 2, 0, RadarLabelDeclutter.MaxExtraLabelsLimit); break;
                case 18: _hudSettings.RadarLabelTelemetry = !_hudSettings.RadarLabelTelemetry; break;
                // P836 — the Modern-HUD density level (Classic ignores it). Left/
                // right step through the five settable levels (Full/Tactical/
                // Combat/Cinematic/Auto). Auto resolves from the in-sector ship
                // count and never selects Cinematic.
                case 19: _hudSettings.Level = Rendering.HudLevels.Step(_hudSettings.Level, direction); break;
                // Native Graphics Options "Debris Low/Medium/High/Off" (idmToggleDebris 'D',
                // WinTrek.cpp:4511,4633-4653): one cycle step per press, saved, applied live.
                case 20:
                    _upscaling.Debris = Rendering.DebrisGeo.NextDensity(_debrisGeo.Density);
                    _debrisGeo.Density = _upscaling.Debris;
                    Console.WriteLine($"[p643] {Rendering.DebrisGeo.MenuLabel(_upscaling.Debris)}");
                    break;
                case 0: _upscaling.Upscaling = _upscaling.Upscaling == UpscalingMode.Off ? UpscalingMode.FSR1 : UpscalingMode.Off; break;
                case 1: _upscaling.Preset = (FsrPreset)(((int)_upscaling.Preset + direction + 4) % 4); break;
                case 2: _upscaling.Sharpness = Math.Clamp(_upscaling.Sharpness + direction * .05f, 0, 1); break;
                case 3: _upscaling.DynamicResolution = !_upscaling.DynamicResolution; break;
                case 5: _upscaling.HdTextures = !_upscaling.HdTextures; _nativeShipRenderer.HdTexturesEnabled = _upscaling.HdTextures; SyncHdMaterialMode(); break;
                case 10: _upscaling.HdPbr = !_upscaling.HdPbr; _nativeShipRenderer.HdPbrEnabled = _upscaling.HdPbr; SyncHdMaterialMode(); break;
                case 4: _upscaling.TargetFrameMs = Math.Clamp(_upscaling.TargetFrameMs + direction, 8, 100); break;
            }
        }
        var mouse = Mouse.GetState();
        var slider = new Rectangle(52, 198, 300, 14);
        if (mouse.LeftButton == ButtonState.Pressed && slider.Contains(mouse.Position))
        {
            _graphicsRow = 2;
            float sharpness = Math.Clamp((mouse.X - slider.X) / (float)slider.Width, 0, 1);
            changed |= sharpness != _upscaling.Sharpness;
            _upscaling.Sharpness = sharpness;
        }
        var hudSlider = new Rectangle(360, 78 + 7 * 46 + 30, 260, 8);
        if (mouse.LeftButton == ButtonState.Pressed && hudSlider.Contains(mouse.Position))
        {
            _graphicsRow = 7; changed = true;
            _hudSettings.Size = .75f + .75f * Math.Clamp((mouse.X - hudSlider.X) / (float)hudSlider.Width, 0, 1);
        }
        if (changed)
        {
            if (_upscaling.DynamicResolution) EnableFrameTelemetry();
            _resolutionController.Reset(UpscalingSettings.Scale(_upscaling.Preset));
            try { _hudSettings.Save(HudSettings.DefaultPath); _upscaling.Save(UpscalingSettings.DefaultPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[p643] cannot save graphics settings: {e.Message}"); }
        }
        _graphicsPrevious = ks;
        return true;
    }
    private void DrawGpuBudgetOverlay()
    {
        if (!_graphicsSettingsVisible && _frameTelemetry is null) return;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        if (_graphicsSettingsVisible)
        {
            string[] rows = {
                $"Upscaling: {_upscaling.Upscaling}",
                $"Preset: {_upscaling.Preset} ({UpscalingSettings.Scale(_upscaling.Preset):P0})",
                $"RCAS sharpness: {_upscaling.Sharpness:P0}",
                $"Dynamic resolution: {(_upscaling.DynamicResolution ? "On" : "Off")}",
                $"Target frame time: {_upscaling.TargetFrameMs:F1} ms",
                $"HD textures: {(_upscaling.HdTextures ? "On" : "Off")} ({_nativeShipRenderer.HdResidentBytes / 1048576.0:F1} MiB resident)",
                $"HUD style: {_hudSettings.Style}",
                $"HUD size: {_hudSettings.Size:P0}",
                $"Colour-blind safe palette: {(_hudSettings.ColorBlindSafe ? "On" : "Off")}",
                $"Effects quality: {_upscaling.Effects}",
                $"HD lighting (PBR, experimental): {(_upscaling.HdPbr ? "On" : "Off")}",
                $"Round radar border: {(_hudSettings.RoundRadar ? "On" : "Off")}",
                $"Joystick indicator: {(_hudSettings.ShowJoystickIndicator ? "On" : "Off")}",
                $"Center HUD: {(_hudSettings.ShowCenterHud ? "On" : "Off")}",
                $"Target HUD: {(_hudSettings.ShowTargetHud ? "On" : "Off")}",
                $"Chat history: {(_chatHistoryVisible ? "On" : "Off")}",
                // P828 (Modern-HUD visual option; Classic stays exactly native).
                $"Radar label declutter: {(_hudSettings.RadarDeclutterOn ? "On" : "Off")} (Modern default)",
                $"Radar labels kept: {_hudSettings.RadarLabelMax} (nearest, after priority)",
                $"Radar label telemetry: {(_hudSettings.RadarLabelTelemetry ? "On" : "Off")}",
                // P836 — the Modern-HUD density level (Full/Tactical/Combat/
                // Cinematic/Auto; Classic ignores it). Shows the resolved level
                // too when Auto is active.
                $"HUD level: {_hudSettings.Level}{( _hudSettings.Level == Rendering.HudLevel.Auto ? $" → {_hudSettings.ActiveLevel}" : "")}",
                Rendering.DebrisGeo.MenuLabel(_debrisGeo.Density) };
            int panelHeight = Math.Min(GraphicsPanelHeight(rows.Length), GraphicsDevice.Viewport.Height - 24);
            _spriteBatch.Draw(_whitePixel, new Rectangle(32, 32, Math.Min(760, GraphicsDevice.Viewport.Width - 48), panelHeight), new Color(5, 15, 22, 245));
            _spriteBatch.DrawString(_hudSmall, "Settings - Graphics / HUD (Ctrl+F6); arrows change, Esc closes", new Vector2(48, 44), Color.White);
            for (int i = 0; i < rows.Length; i++)
                _spriteBatch.DrawString(_hudSmall, (_graphicsRow == i ? "> " : "  ") + rows[i], new Vector2(48, 78 + i * 46), _graphicsRow == i ? Color.Yellow : Color.White);
            _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, 300, 8), Color.Gray);
            _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, (int)(300 * _upscaling.Sharpness), 8), Color.Cyan);
            _spriteBatch.Draw(_whitePixel, new Rectangle(360, 430, 260, 8), Color.Gray);
            _spriteBatch.Draw(_whitePixel, new Rectangle(360, 430, (int)(260 * (_hudSettings.Size - .75f) / .75f), 8), Color.Cyan);
            _spriteBatch.DrawString(_hudSmall, _fsr is { Ready: true } ? "FSR1 ready; HUD stays at native resolution" : "FSR1 unavailable: using native resolution", new Vector2(48, 78 + rows.Length * 46 + 8), Color.White);
        }
        if (_frameTelemetry is not null)
        {
            int y = Math.Max(0, GraphicsDevice.Viewport.Height - 92);
            _spriteBatch.Draw(_whitePixel, new Rectangle(0, y, GraphicsDevice.Viewport.Width, 92), new Color(0, 0, 0, 205));
            _spriteBatch.DrawString(_hudSmall, _frameTelemetry.Overlay, new Vector2(8, y), Color.White);
        }
        _spriteBatch.End();
    }
    protected override void EndDraw()
    {
        if (_benchResult is null) DrawGpuBudgetOverlay();
        if (_benchExitAfterDraw && _benchResult is not null && Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_SCREENSHOT") is { Length: > 0 } screenshot)
        {
            var parameters = GraphicsDevice.PresentationParameters;
            var pixels = new Color[parameters.BackBufferWidth * parameters.BackBufferHeight];
            GraphicsDevice.GetBackBufferData(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
            File.WriteAllBytes(screenshot, Capture.CapturePngSaver.Encode(pixels, parameters.BackBufferWidth, parameters.BackBufferHeight));
        }
        if (_budgetDuration > 0 && Stopwatch.GetElapsedTime(_budgetStart).TotalSeconds >= _budgetDuration)
        {
            if (!string.IsNullOrWhiteSpace(_budgetCapture))
            {
                var ppCapture = GraphicsDevice.PresentationParameters;
                var pixels = new Color[ppCapture.BackBufferWidth * ppCapture.BackBufferHeight];
                GraphicsDevice.GetBackBufferData(pixels);
                using var texture = new Texture2D(GraphicsDevice, ppCapture.BackBufferWidth, ppCapture.BackBufferHeight);
                texture.SetData(pixels);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_budgetCapture))!);
                using var file = File.Create(_budgetCapture);
                texture.SaveAsPng(file, texture.Width, texture.Height);
            }
            _budgetDuration = 0;
            Exit();
        }
        _frameTelemetry?.Submitted();
        long draws = GraphicsDevice.Metrics.DrawCount;
        var pp = GraphicsDevice.PresentationParameters;
        long start = Stopwatch.GetTimestamp();
        base.EndDraw();
        _frameTelemetry?.EndDraw(Stopwatch.GetElapsedTime(start).TotalMilliseconds, draws,
            pp.BackBufferWidth, pp.BackBufferHeight, _scaledThisFrame ? _postWidth : pp.BackBufferWidth,
            _scaledThisFrame ? _postHeight : pp.BackBufferHeight);
        if (_benchExitAfterDraw && _benchResult is not null) Exit();
    }
}
