using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P984 — the SINGLE output transform of the HDR scene path
/// (<c>Shaders/Tonemap.fx</c>): the fullscreen pass that converts the floating
/// point scene target into the display-referred image the HUD is drawn over.
/// <para>Two passes, one technique: <c>Neutral</c> (the identity below the
/// shoulder onset — the picture today's 8-bit path produces) and <c>Aces</c>
/// (Narkowicz's public filmic fit, for comparison). Nothing else in the frame is
/// graded: bloom runs BEFORE this pass on the float scene, FSR upscales before it,
/// and the HUD/overlays draw after it onto the same final target.</para>
/// <para>Unavailable shader → <see cref="IsUsable"/> false, and
/// <see cref="VfxPostProcessor"/> keeps the native 8-bit pipeline (a missing
/// output transform must never leave an un-graded float scene on screen).</para>
/// </summary>
public sealed class HdrOutputTransform : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Effect _effect;
    private readonly FullscreenQuad _quad;
    private readonly EffectParameter? _pTexture, _pExposure, _pThreshold, _pSlope;

    /// <summary>The effect parameters this transform sets from code every pass
    /// (GL ignores shader defaults, exactly like the other post passes).</summary>
    public static readonly string[] RequiredParameters = { "SceneTexture", "Exposure", "Threshold", "Slope" };

    public HdrOutputTransform(GraphicsDevice gd, Effect effect)
    {
        _gd = gd;
        _effect = effect;
        _quad = new FullscreenQuad(gd);
        _pTexture = effect.Parameters["SceneTexture"];
        _pExposure = effect.Parameters["Exposure"];
        _pThreshold = effect.Parameters["Threshold"];
        _pSlope = effect.Parameters["Slope"];
    }

    /// <summary>The transform can run (both passes and every uniform present).</summary>
    public bool IsUsable
        => _effect.Techniques["Output"]?.Passes["Neutral"] is not null
        && _effect.Techniques["Output"]?.Passes["Aces"] is not null
        && _pTexture is not null && _pExposure is not null
        && _pThreshold is not null && _pSlope is not null;

    /// <summary>
    /// Apply the selected curve to <paramref name="source"/> (the float scene /
    /// post / upscaled target) and write the display-referred result to
    /// <paramref name="destination"/> (the backbuffer or the capture target).
    /// <paramref name="exposure"/> is applied in the curve's own domain; it is
    /// unity in this slice (no auto-exposure).
    /// </summary>
    public void Render(Texture2D source, RenderTarget2D? destination, HdrCurve curve, float exposure = HdrOutput.DefaultExposure)
    {
        if (!IsUsable) throw new InvalidOperationException("HDR output transform shader unavailable");
        // Never leave the previous pass's source bound while the scene target is
        // the destination of a render (P582's feedback-loop rule).
        _gd.Textures[0] = null;
        _gd.SetRenderTarget(destination);
        _pTexture!.SetValue(source);
        _pExposure!.SetValue(exposure);
        _pThreshold!.SetValue(HdrOutput.NeutralThreshold);
        _pSlope!.SetValue(HdrOutput.NeutralSlope);
        _effect.Techniques["Output"].Passes[curve == HdrCurve.Aces ? "Aces" : "Neutral"].Apply();

        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.SamplerStates[0] = SamplerState.LinearClamp;
        _quad.Draw(_gd);

        // Restore the opaque/default state the HUD/overlay passes expect (the
        // P339/P525/P529 rule every post pass follows).
        _gd.Textures[0] = null;
        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _quad.Dispose();
        // The Effect is owned by the ShaderAssetCache (as for the bloom effect).
    }
}
