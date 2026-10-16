using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Scene -> bloom at scene resolution -> EASU at native resolution -> RCAS -> final target.
/// Shader effects are borrowed from ShaderAssetCache. All targets use universal RGBA8.</summary>
public sealed class FsrPostProcessor : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Effect? _easu, _rcas;
    private readonly FullscreenQuad _quad;
    private RenderTarget2D? _upscaled;
    public RenderTarget2D? Scene { get; private set; }
    public RenderTarget2D? Bloomed { get; private set; }
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
    public void SetSize(int width, int height, float scale)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var (w, h) = UpscalingSettings.Dimensions(width, height, scale);
        if (Scene?.Width == w && Scene.Height == h && _upscaled?.Width == width && _upscaled.Height == height) return;
        Scene?.Dispose(); Bloomed?.Dispose(); _upscaled?.Dispose();
        Scene = new(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        Bloomed = new(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        _upscaled = new(_gd, width, height, false, SurfaceFormat.Color, DepthFormat.None);
    }
    public void Render(Texture2D source, RenderTarget2D? destination, float sharpness)
    {
        if (!Ready || _upscaled is null) throw new InvalidOperationException("FSR effects/targets unavailable");
        Run(_easu!, "Easu", source, _upscaled, sharpness);
        Run(_rcas!, "Rcas", _upscaled, destination, sharpness);
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
    public void Dispose() { _quad.Dispose(); Scene?.Dispose(); Bloomed?.Dispose(); _upscaled?.Dispose(); }
}
