using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — the per-frame context for the soft-particle path: the linear-depth
/// target just filled by the depth pass, the near/far used to normalize depth
/// (fed to BOTH LinearDepth.fx and SoftParticle.fx so the comparison is in the
/// same space) and the softness. When the caller has no depth target / effect
/// (or the setting is off) it passes <c>null</c> and the smoke falls back to
/// the near-camera-fade-only path — never rendering nothing.
/// </summary>
public readonly record struct SoftParticleContext(Texture2D Depth, float Near, float Far, float SoftnessNormalized)
{
    public static readonly SoftParticleContext Off = default;
}

/// <summary>
/// P547 — draws the ENHANCED smoke billboards with the compiled
/// <c>SoftParticle.fx</c> so each puff fades where it meets the opaque scene's
/// linear depth (ships, asteroids, stations — the depth pass). This is the
/// proper version of P546's near-camera fade: no hard cut edges against
/// hulls/rocks/stations. If the effect is unavailable the caller uses the
/// near-fade-only path (see <see cref="TryDraw"/> returning false).
/// </summary>
/// <remarks>
/// Vertices are built by the same <see cref="ParticleBillboardBatcher"/>
/// routine so the geometry is identical to the BasicEffect path; only the
/// effect (and its extra depth/near/far/softness parameters, ALL set from code
/// every frame — GL ignores shader parameter defaults) and the premultiplied
/// blend differ. Render state is pinned and restored (P339/P525/P529).
/// </remarks>
public sealed class SoftParticleRenderer : IDisposable
{
    private readonly Effect _effect;
    private readonly RasterizerState _cullNone;
    private readonly BlendState _premul;

    /// <summary>The effect parameters this renderer sets from code EVERY draw.
    /// Exposed so the P547 unit test can verify the shader declares exactly
    /// these (no reliance on GL parameter defaults).</summary>
    public static readonly string[] RequiredParameters =
        { "WorldView", "WorldViewProj", "InvViewportSize", "Near", "InvRange", "SoftnessInv", "Texture", "DepthTexture" };

    // Cached effect parameters (resolve once per effect at construction).
    private readonly EffectParameter _pWorldView, _pWorldViewProj, _pInvViewport, _pNear, _pInvRange, _pSoftnessInv;
    private readonly EffectParameter _pTexture, _pDepthTexture;

    public SoftParticleRenderer(Effect effect)
    {
        _effect = effect;
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        // Premultiplied alpha (ONE, INVSRCALPHA) — identical to
        // ParticleBillboardBatcher.Premultiplied, so soft smoke accumulates
        // translucency exactly like the P546 smoke it replaces.
        _premul = new BlendState
        {
            ColorSourceBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        _pWorldView = Param("WorldView");
        _pWorldViewProj = Param("WorldViewProj");
        _pInvViewport = Param("InvViewportSize");
        _pNear = Param("Near");
        _pInvRange = Param("InvRange");
        _pSoftnessInv = Param("SoftnessInv");
        _pTexture = Param("Texture");
        _pDepthTexture = Param("DepthTexture");
    }

    private EffectParameter Param(string name) => _effect.Parameters[name];

    /// <summary>
    /// Whether the underlying effect has the parameters this renderer needs.
    /// If a parameter is missing (e.g. an unexpectedly-shaped compiled shader),
    /// the soft path is treated as unavailable.
    /// </summary>
    public bool IsUsable =>
        _pWorldView is not null && _pWorldViewProj is not null && _pInvViewport is not null
        && _pNear is not null && _pInvRange is not null && _pSoftnessInv is not null
        && _pTexture is not null && _pDepthTexture is not null;

    /// <summary>
    /// Draw <paramref name="items"/> as premultiplied smoke billboards sampling
    /// the linear-depth target <paramref name="ctx"/>. Returns true if drawn,
    /// false if the effect is unusable (caller falls back to near-fade only).
    /// </summary>
    public bool TryDraw(IReadOnlyList<ParticleBillboard> items, Texture2D tex, Matrix view, Matrix projection,
        SoftParticleContext ctx)
    {
        if (items.Count == 0 || !IsUsable) return false;

        var gd = _effect.GraphicsDevice;
        // Eye-relative billboards: world is identity, so WorldView = view,
        // WorldViewProj = view*proj. Same frame the color pass uses.
        var vp = view * projection;
        var invViewport = new Vector2(1f / gd.Viewport.Width, 1f / gd.Viewport.Height);
        float invRange = LinearDepth.InvRange(ctx.Near, ctx.Far);

        _pWorldView.SetValue(view);
        _pWorldViewProj.SetValue(vp);
        _pInvViewport.SetValue(invViewport);
        _pNear.SetValue(ctx.Near);
        _pInvRange.SetValue(invRange);
        _pSoftnessInv.SetValue(1f / Math.Max(1e-6f, ctx.SoftnessNormalized));
        _pTexture.SetValue(tex);
        _pDepthTexture.SetValue(ctx.Depth);

        ParticleBillboardBatcher.BuildBillboardArrays(items, view, out var verts, out var indices);

        var technique = _effect.Techniques["SoftParticle"];
        if (technique is null) return false;
        _effect.CurrentTechnique = technique;
        _effect.CurrentTechnique.Passes[0].Apply();

        gd.BlendState = _premul;   // premultiplied (ONE, INVSRCALPHA)
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, items.Count * 2);

        // Restore the three states we touched (P339 B2 discipline).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        return true;
    }

    public void Dispose()
    {
        _effect.Dispose();
        _cullNone.Dispose();
        _premul.Dispose();
    }
}
