using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Scene -> bloom at scene resolution -> EASU at native resolution -> RCAS -> final target.
/// Shader effects are borrowed from ShaderAssetCache. Targets are RGBA8 on the native path and
/// RGBA16F on the P984 HDR path (<see cref="SetSize"/>'s <c>hdr</c> argument).</summary>
public sealed class FsrPostProcessor : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Effect? _easu, _rcas;
    private readonly FullscreenQuad _quad;
    private RenderTarget2D? _upscaled;
    private RenderTarget2D? _hdrOut;
    private bool _hdr;
    public RenderTarget2D? Scene { get; private set; }
    public RenderTarget2D? Bloomed { get; private set; }
    /// <summary>P984 — the upscaled HDR result (float, output resolution) the
    /// single output transform reads on the HDR+FSR path. Non-null only after a
    /// <see cref="Render"/> call with <c>hdr: true</c>.</summary>
    public RenderTarget2D? HdrUpscaled => _hdrOut;
    public bool Ready => _easu?.Techniques["Fsr"]?.Passes["Easu"] is not null
        && _rcas?.Techniques["Fsr"]?.Passes["Rcas"] is not null
        && _easu.Parameters["InputSize"] is not null && _easu.Parameters["SceneTexture"] is not null
        && _rcas.Parameters["InputSize"] is not null && _rcas.Parameters["SceneTexture"] is not null
        && _rcas.Parameters["Sharpness"] is not null;
    public FsrPostProcessor(GraphicsDevice gd, ShaderAssetCache shaders)
    {
        _gd = gd; _quad = new(gd);
        _easu = shaders.Load("Content/Shaders/FsrEasu.ogl.mgfxo");
        _rcas = shaders.Load("Content/Shaders/FsrRcas.ogl.mgfxo");
    }
    /// <summary>(Re)create the FSR targets. P984 — <paramref name="hdr"/> makes the
    /// scene/bloom/upscale targets RGBA16F so the HDR path keeps over-1.0
    /// highlights through the upscale; false is the native 8-bit pipeline. A
    /// format change forces a re-create.</summary>
    public void SetSize(int width, int height, float scale, bool hdr = false)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var (w, h) = UpscalingSettings.Dimensions(width, height, scale);
        if (Scene?.Width == w && Scene.Height == h && _upscaled?.Width == width && _upscaled.Height == height
            && _hdr == hdr && (hdr ? _hdrOut is not null : _hdrOut is null)) return;
        _hdr = hdr;
        SurfaceFormat format = HdrOutput.SceneFormat(hdr);
        Scene?.Dispose(); Bloomed?.Dispose(); _upscaled?.Dispose(); _hdrOut?.Dispose();
        Scene = new(_gd, w, h, false, format, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        Bloomed = new(_gd, w, h, false, format, DepthFormat.None);
        _upscaled = new(_gd, width, height, false, format, DepthFormat.None);
        _hdrOut = hdr ? new(_gd, width, height, false, format, DepthFormat.None) : null;
    }
    /// <summary>EASU upscale + RCAS sharpen. Native (8-bit) path: RCAS writes
    /// straight to <paramref name="destination" /> (backbuffer/capture target) as
    /// before. HDR path (<paramref name="hdr" />): RCAS writes the float
    /// <see cref="HdrUpscaled" /> target, and the caller runs the single output
    /// transform from it (no premature display encode).</summary>
    public void Render(Texture2D source, RenderTarget2D? destination, float sharpness, bool hdr = false)
    {
        if (!Ready || _upscaled is null) throw new InvalidOperationException("FSR effects/targets unavailable");
        Run(_easu!, "Easu", source, _upscaled, sharpness);
        Run(_rcas!, "Rcas", _upscaled, hdr ? _hdrOut : destination, sharpness);
        _gd.Textures[0] = null;
        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }
    private void Run(Effect effect, string pass, Texture2D input, RenderTarget2D? output, float sharpness)
    {
        _gd.Textures[0] = null;
        _gd.SetRenderTarget(output);
        effect.Parameters["InputSize"].SetValue(new Vector2(input.Width, input.Height));
        effect.Parameters["SceneTexture"].SetValue(input);
        effect.Parameters["Sharpness"]?.SetValue(Math.Clamp(sharpness, 0, 1));
        effect.Techniques["Fsr"].Passes[pass].Apply();
        _gd.BlendState = BlendState.Opaque; _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState = RasterizerState.CullNone; _gd.SamplerStates[0] = SamplerState.PointClamp;
        _quad.Draw(_gd);
    }
    public void Dispose()
    {
        _quad.Dispose(); Scene?.Dispose(); Bloomed?.Dispose(); _upscaled?.Dispose(); _hdrOut?.Dispose();
    }
}
