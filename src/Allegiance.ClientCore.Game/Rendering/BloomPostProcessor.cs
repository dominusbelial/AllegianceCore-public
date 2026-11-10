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
    /// luminance a pixel blooms; below it is killed by the soft knee. This is the
    /// P547 default; P989's sim palette raises it (in the scene target's stored
    /// domain) through <see cref="BrightPassThreshold"/>.</summary>
    public const float DefaultThreshold = 0.80f;

    /// <summary>Bright-pass threshold actually used this frame (P547 default
    /// unless a grade sets another value).</summary>
    public float BrightPassThreshold { get; set; } = DefaultThreshold;

    /// <summary>Bloom intensity scale (multiplies the bright-pass before blur).
    /// P989 — this is also the palette's glow "contribution": scaling the extracted
    /// bright colour is exactly scaling the glow the composite adds, and it needs
    /// NO shader change, so the default path's Bloom.fx bytecode and behaviour stay
    /// P547's bit for bit. 1 = the P547 look; the sim palette uses 0.08.</summary>
    public const float DefaultBloomScale = 1.0f;

    /// <summary>Live bright-pass scale / glow contribution (default P547).</summary>
    public float BloomScale { get; set; } = DefaultBloomScale;
    public bool Polished { get; set; }

    private readonly GraphicsDevice _gd;
    private readonly Effect _effect;
    private readonly FullscreenQuad _quad;

    /// <summary>The effect parameters this processor sets from code every pass
    /// (no reliance on GL parameter defaults). Exposed for the P547 unit test.</summary>
    public static readonly string[] RequiredParameters =
        { "TexelSize", "Threshold", "BloomScale", "Direction", "SceneTexture", "BloomTexture" };

    private RenderTarget2D? _bright;
    private RenderTarget2D? _blurH;
    private RenderTarget2D? _blurV;

    private readonly EffectParameter _pTexel, _pThreshold, _pScale, _pDirection, _pTexture;
    private readonly SpriteBatch _batch;
    private readonly EffectParameter _pBloom;

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
        _pBloom = effect.Parameters["BloomTexture"];
        SetSize(gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
    }

    public bool IsUsable =>
        _effect.Techniques["Bloom"] is not null
        && _pTexel is not null && _pThreshold is not null && _pScale is not null
        && _pDirection is not null && _pTexture is not null && _pBloom is not null
        && _bright is not null && _blurH is not null && _blurV is not null;

    /// <summary>
    /// (Re)create the half-res bright-pass/blur targets. P984 — <paramref name="hdr"/>
    /// makes them RGBA16F so the bright-pass threshold and the blur operate on the
    /// float scene's over-1.0 highlights instead of clipping them at 8 bits.
    /// </summary>
    public void SetSize(int width, int height, bool hdr = false)
    {
        int bw = Math.Max(1, width / Downsample), bh = Math.Max(1, height / Downsample);
        SurfaceFormat format = HdrOutput.SceneFormat(hdr);
        _bright?.Dispose(); _blurH?.Dispose(); _blurV?.Dispose();
        _bright = new RenderTarget2D(_gd, bw, bh, false, format, DepthFormat.None);
        _blurH = new RenderTarget2D(_gd, bw, bh, false, format, DepthFormat.None);
        _blurV = new RenderTarget2D(_gd, bw, bh, false, format, DepthFormat.None);
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
        if (Polished)
        {
            _pBloom.SetValue(_blurV);
            RunPass(scene, texelScene, "Composite", 0);
            _gd.Textures[1] = null;
            return true;
        }
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
        _pThreshold.SetValue(BrightPassThreshold);
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
