using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — thresholded, separable-Gaussian bloom (<c>Bloom.fx</c>), the ONLY
/// acceptable bloom (the spike proved shader-free bloom re-raises clipping to
/// 89.7% in dense combat; the thresholded .fx bloom keeps it ≤ ~20%). The
/// pipeline: bright-pass the scene → half-res, blur H, blur V, then composite
/// the scene + the blurred bright-pass additively onto the final target.
/// HUD/SpriteBatch UI is drawn AFTER this composite so it never blooms.
/// Off by default (a setting/CLI toggle). All effect parameters are set from
/// code every pass (GL ignores shader defaults).
/// </summary>
/// <remarks>
/// Render-state hygiene (P339/P525/P529): every pass pins its own
/// blend/depth/sampler state and restores opaque/default at the end, so no
/// leak into the HUD/overlay passes that follow.
/// </remarks>
public sealed class BloomPostProcessor : IDisposable
{
    /// <summary>Downsample factor for the bright-pass target (half res).</summary>
    public const int Downsample = 2;

    /// <summary>Bright-pass threshold on linear-ish luminance (0..1). Above this
    /// luminance a pixel blooms; below it is killed by the soft knee.</summary>
    public const float Threshold = 0.80f;

    /// <summary>Bloom intensity scale (multiplies the bright-pass before blur).</summary>
    public const float BloomScale = 1.0f;

    private readonly GraphicsDevice _gd;
    private readonly Effect _effect;
    private readonly FullscreenQuad _quad;

    /// <summary>The effect parameters this processor sets from code every pass
    /// (no reliance on GL parameter defaults). Exposed for the P547 unit test.</summary>
    public static readonly string[] RequiredParameters =
        { "TexelSize", "Threshold", "BloomScale", "Direction", "SceneTexture" };

    private RenderTarget2D? _bright;
    private RenderTarget2D? _blurH;
    private RenderTarget2D? _blurV;

    private readonly EffectParameter _pTexel, _pThreshold, _pScale, _pDirection, _pTexture;
    private readonly SpriteBatch _batch;

    public BloomPostProcessor(GraphicsDevice gd, Effect effect)
    {
        _gd = gd;
        _effect = effect;
        _quad = new FullscreenQuad(gd);
        _batch = new SpriteBatch(gd);
        _pTexel = effect.Parameters["TexelSize"];
        _pThreshold = effect.Parameters["Threshold"];
        _pScale = effect.Parameters["BloomScale"];
        _pDirection = effect.Parameters["Direction"];
        _pTexture = effect.Parameters["SceneTexture"];
        SetSize(gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
    }

    public bool IsUsable =>
        _effect.Techniques["Bloom"] is not null
        && _pTexel is not null && _pThreshold is not null && _pScale is not null
        && _pDirection is not null && _pTexture is not null
        && _bright is not null && _blurH is not null && _blurV is not null;

    public void SetSize(int width, int height)
    {
        int bw = Math.Max(1, width / Downsample), bh = Math.Max(1, height / Downsample);
        _bright?.Dispose(); _blurH?.Dispose(); _blurV?.Dispose();
        _bright = new RenderTarget2D(_gd, bw, bh, false, SurfaceFormat.Color, DepthFormat.None);
        _blurH = new RenderTarget2D(_gd, bw, bh, false, SurfaceFormat.Color, DepthFormat.None);
        _blurV = new RenderTarget2D(_gd, bw, bh, false, SurfaceFormat.Color, DepthFormat.None);
    }

    /// <summary>
    /// Apply bloom to <paramref name="scene"/>: bright-pass → H-blur → V-blur,
    /// then composite <c>scene + bloom</c> additively onto the CURRENTLY SET
    /// render target (the caller sets the final/backbuffer target first — the
    /// HUD draws after on the same target). Returns false (no-op) if unusable.
    /// </summary>
    public bool Render(Texture2D scene)
    {
        if (!IsUsable) return false;

        var current = _gd.GetRenderTargets();
        var texelScene = new Vector2(1f / Math.Max(1, scene.Width), 1f / Math.Max(1, scene.Height));
        var texelHalf = new Vector2(1f / _bright!.Width, 1f / _bright.Height);

        // 1) Bright-pass (threshold + soft knee) -> half-res.
        _gd.SetRenderTarget(_bright);
        _gd.Clear(Color.Transparent);
        RunPass(scene, texelScene, passName: "BrightPass", direction: 0);

        // 2) Horizontal blur -> blurH.
        _gd.SetRenderTarget(_blurH);
        _gd.Clear(Color.Transparent);
        RunPass(_bright!, texelHalf, "Blur", direction: 0);

        // 3) Vertical blur -> blurV (the finished bloom).
        _gd.SetRenderTarget(_blurV);
        _gd.Clear(Color.Transparent);
        RunPass(_blurH!, texelHalf, "Blur", direction: 1);

        // 4) Composite onto the CALLER'S target (restored below): opaque scene
        //    copy + additive bloom. HUD/UI draws afterward on the same target.
        _gd.SetRenderTarget(current.Length > 0 ? (RenderTarget2D)current[0].RenderTarget : null);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        _batch.Draw(scene, new Rectangle(0, 0, _gd.Viewport.Width, _gd.Viewport.Height), Color.White);
        _batch.End();
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        _batch.Draw(_blurV, new Rectangle(0, 0, _gd.Viewport.Width, _gd.Viewport.Height), Color.White);
        _batch.End();
        return true;
    }

    private void RunPass(Texture2D source, Vector2 texel, string passName, int direction)
    {
        var pass = _effect.Techniques["Bloom"].Passes[passName];
        _pTexel.SetValue(texel);
        _pThreshold.SetValue(Threshold);
        _pScale.SetValue(BloomScale);
        _pDirection.SetValue(direction);
        _pTexture.SetValue(source);
        pass.Apply();

        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.SamplerStates[0] = SamplerState.LinearClamp;
        _quad.Draw(_gd);

        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _effect.Dispose();
        _quad.Dispose();
        _batch.Dispose();
        _bright?.Dispose(); _blurH?.Dispose(); _blurV?.Dispose();
    }
}
