#if !BROWSER
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>MSAA owns ONLY the 3-D scene. MonoGame resolves its renderbuffer to
/// the sampleable texture when SetRenderTarget unbinds it. Post and HUD remain
/// single-sampled. Off allocates nothing and leaves all legacy bindings alone.</summary>
public sealed class SceneAntialiasing(GraphicsDevice device) : IDisposable
{
    private RenderTarget2D? _scene;
    private SpriteBatch? _copy;
    private bool _reported;
    private (int Width, int Height, SurfaceFormat Format, Antialiasing Request)? _configuration;
    public RenderTarget2D? Scene => _scene;
    public int Samples => _scene?.MultiSampleCount ?? 0;

    public RenderTarget2D? Prepare(int width, int height, SurfaceFormat format, Antialiasing request)
    {
        var configuration = (width, height, format, request);
        if (_configuration == configuration && _scene is not { IsDisposed: true }) return _scene;
        _configuration = configuration;
        _reported = false;
        _scene?.Dispose(); _scene = null;
        int samples = AntialiasingOptions.Clamp(request, level =>
        {
            device.Adapter.QueryRenderTargetFormat(device.GraphicsProfile, format, DepthFormat.Depth24, level,
                out var selectedFormat, out var selectedDepth, out int selectedSamples);
            return selectedFormat == format && selectedDepth == DepthFormat.Depth24 && selectedSamples == level;
        });
        while (samples > 0)
        {
            try
            {
                _scene = new(device, width, height, false, format, DepthFormat.Depth24, samples,
                    RenderTargetUsage.PreserveContents); // P582b: survive the volumetric excursion, including depth.
                break;
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                Console.WriteLine($"[antialiasing] {samples}x {format} unavailable ({exception.GetType().Name}); trying lower samples");
                int failed = samples;
                samples = AntialiasingOptions.Clamp((Antialiasing)(failed == 8 ? 4 : failed == 4 ? 2 : 0), level =>
                {
                    device.Adapter.QueryRenderTargetFormat(device.GraphicsProfile, format, DepthFormat.Depth24, level,
                        out var f, out var d, out int n);
                    return f == format && d == DepthFormat.Depth24 && n == level;
                });
            }
        }
        Console.WriteLine($"[antialiasing] requested={AntialiasingOptions.Describe(request)} selected={Samples} "
            + $"scene={width}x{height}/{format} backbuffer={device.PresentationParameters.MultiSampleCount} HUD=single-sample"
            + (Samples != (int)request ? " (clamped to device support)" : ""));
        return _scene;
    }

    /// <summary>Call with the selected 3-D target bound, once per configuration.</summary>
    public void LogBoundSamples()
    {
        if (_reported) return;
        _reported = true;
        Console.WriteLine($"[antialiasing] bound scene reported={Samples} "
            + $"physicalGL={Diagnostics.GlSampleCount.Current()?.ToString() ?? "unavailable"}");
    }

    /// <summary>Unbind BEFORE a bloom/HDR/FSR shader reads the scene; this is
    /// MonoGame's resolve boundary. The plain path needs one opaque 1:1 copy.
    /// The caller's final target stays bound so the HUD follows at output resolution.</summary>
    public void Resolve(RenderTarget2D? finalTarget, bool hasPost)
    {
        if (_scene is null) return;
        device.SetRenderTarget(finalTarget);
        if (hasPost) return;
        _copy ??= new SpriteBatch(device);
        _copy.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        _copy.Draw(_scene, new Rectangle(0, 0, device.Viewport.Width, device.Viewport.Height), Color.White);
        _copy.End();
        device.Textures[0] = null;
        device.BlendState = BlendState.Opaque;
        device.DepthStencilState = DepthStencilState.Default;
        device.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose() { _scene?.Dispose(); _copy?.Dispose(); }
}
#endif
