using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly UpscalingSettings _upscaling = UpscalingSettings.Load(UpscalingSettings.DefaultPath);
    private readonly DynamicResolutionController _resolutionController = new();
    private readonly FlightGcLatency _flightGc = new(Environment.GetEnvironmentVariable("ALLEGIANCE_CLIENT_GC_LOWLATENCY") == "1");
    private ClientFrameTelemetry? _frameTelemetry;
    private GlFrameTimer? _gpuTimer;
    private FsrPostProcessor? _fsr;
    private bool _graphicsSettingsVisible, _graphicsOpenWasDown;
    private KeyboardState _graphicsPrevious;
    private int _graphicsRow;
    private Action<long, bool, double>? _receiveGpu;
    private long _budgetStart;
    private double _budgetDuration;
    private string? _budgetCapture;
    private long _resolutionGpuGeneration;
    private int _postWidth, _postHeight;
    private bool _scaledThisFrame;

    private void InitializeGpuBudget()
    {
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
    private bool UpdateGraphicsSettings(KeyboardState ks)
    {
        bool open = KeyOf("OpenGraphicsSettings").Matches(ks);
        if (open && !_graphicsOpenWasDown && !_chatActive && !_qcMenuOpen) _graphicsSettingsVisible = !_graphicsSettingsVisible;
        _graphicsOpenWasDown = open;
        if (!_graphicsSettingsVisible) { _graphicsPrevious = ks; return false; }
        _flightGc.SetFlying(false);
        bool Press(Keys k) => ks.IsKeyDown(k) && !_graphicsPrevious.IsKeyDown(k);
        if (Press(Keys.Escape)) _graphicsSettingsVisible = false;
        if (Press(Keys.Down)) _graphicsRow = (_graphicsRow + 1) % 5;
        if (Press(Keys.Up)) _graphicsRow = (_graphicsRow + 4) % 5;
        int direction = Press(Keys.Right) ? 1 : Press(Keys.Left) ? -1 : 0;
        bool changed = direction != 0;
        if (changed)
        {
            switch (_graphicsRow)
            {
                case 0: _upscaling.Upscaling = _upscaling.Upscaling == UpscalingMode.Off ? UpscalingMode.FSR1 : UpscalingMode.Off; break;
                case 1: _upscaling.Preset = (FsrPreset)(((int)_upscaling.Preset + direction + 4) % 4); break;
                case 2: _upscaling.Sharpness = Math.Clamp(_upscaling.Sharpness + direction * .05f, 0, 1); break;
                case 3: _upscaling.DynamicResolution = !_upscaling.DynamicResolution; break;
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
        if (changed)
        {
            if (_upscaling.DynamicResolution) EnableFrameTelemetry();
            _resolutionController.Reset(UpscalingSettings.Scale(_upscaling.Preset));
            try { _upscaling.Save(UpscalingSettings.DefaultPath); }
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
            _spriteBatch.Draw(_whitePixel, new Rectangle(32, 32, Math.Min(760, GraphicsDevice.Viewport.Width - 48), 330), new Color(5, 15, 22, 245));
            _spriteBatch.DrawString(_hudSmall, "Graphics (Ctrl+F6) - arrows change, Esc closes; saved automatically", new Vector2(48, 44), Color.White);
            string[] rows = {
                $"Upscaling: {_upscaling.Upscaling}",
                $"Preset: {_upscaling.Preset} ({UpscalingSettings.Scale(_upscaling.Preset):P0})",
                $"RCAS sharpness: {_upscaling.Sharpness:P0}",
                $"Dynamic resolution: {(_upscaling.DynamicResolution ? "On" : "Off")}",
                $"Target frame time: {_upscaling.TargetFrameMs:F1} ms" };
            for (int i = 0; i < rows.Length; i++)
                _spriteBatch.DrawString(_hudSmall, (_graphicsRow == i ? "> " : "  ") + rows[i], new Vector2(48, 78 + i * 46), _graphicsRow == i ? Color.Yellow : Color.White);
            _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, 300, 8), Color.Gray);
            _spriteBatch.Draw(_whitePixel, new Rectangle(52, 198, (int)(300 * _upscaling.Sharpness), 8), Color.Cyan);
            _spriteBatch.DrawString(_hudSmall, _fsr is { Ready: true } ? "FSR1 ready; HUD stays at native resolution" : "FSR1 unavailable: using native resolution", new Vector2(48, 316), Color.White);
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
        DrawGpuBudgetOverlay();
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
    }
}
