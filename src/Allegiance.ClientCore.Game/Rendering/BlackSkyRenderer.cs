using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P588 — the <b>black sky</b> tier (<c>--sky black</c>, the new DEFAULT):
/// a pure black backdrop (the existing <c>Color.Black</c> clear) with the
/// restored <b>pre-P576 point starfield</b> (the three generic parallax
/// <see cref="Starfield"/> shells) plus the <b>native planet poster</b> — and
/// <b>no nebula dome</b>. Ambient space dust is drawn separately, unchanged.
///
/// Chosen star layer: the PRE-P576 <see cref="Starfield"/> shells, restored
/// exactly (same radii/count/seeds/brightness as the original creation in
/// <c>Game1.Lifecycle.cs:303-308</c>), NOT the P576 seeded stars — because
/// that is literally the "old black sky" Domingo asked to keep. The P576 stars
/// sit on a shell at infinity (no parallax); the finite bands of the restored
/// shells give the real motion parallax the old look had, which is the visual
/// cue that made the pre-P576 sky feel like a living backdrop.
///
/// Placement is camera-relative (P576b): drawn behind everything with the sky's
/// own large-far projection (<see cref="NativeSkyPlacement.SkyProjection"/>) so
/// neither the outer star band nor the planet far-clips, and no black wedges.
/// </summary>
public sealed class BlackSkyRenderer : ISkyRenderer, IDisposable
{
    /// <summary>Planet billboard distance — the same sky-shell distance native
    /// uses for its planet (<c>NativeSkyRenderer.StarRadius</c>), sitting just
    /// beyond the farthest star band (6000) so the planet reads clear of the
    /// stars.</summary>
    private const float PlanetDist = 6650f;

    private readonly GraphicsDevice _gd;
    private readonly Starfield _starNear;
    private readonly Starfield _starMid;
    private readonly Starfield _starFar;
    private readonly PlanetPoster _planet;

    public BlackSkyRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        // Restored pre-P576 starfield shells — the exact old look, centred on
        // the origin: near band brighter + sparser (fast, sharp parallax),
        // far band dimmer + denser (slow, backdrop-like). Parameters match the
        // original creation verbatim (Game1.Lifecycle.cs:303-308).
        _starNear = new Starfield(gd, Vector3.Zero, 1200f, 2200f, 500, seed: 111, minBrightness: 220, maxBrightness: 255);
        _starMid = new Starfield(gd, Vector3.Zero, 2200f, 3800f, 1200, seed: 222, minBrightness: 160, maxBrightness: 220);
        _starFar = new Starfield(gd, Vector3.Zero, 3800f, 6000f, 2500, seed: 333, minBrightness: 90, maxBrightness: 160);
        _planet = new PlanetPoster(gd, assets);
    }

    /// <summary>Switch the sky to a cluster's data (planet poster; stars are the
    /// fixed pre-P576 shells, not per-sector). Called on sector change.</summary>
    public void SetCluster(DataClusterIGC cluster) => _planet.SetCluster(cluster);

    /// <summary>Draw the black sky behind everything: point stars then the
    /// native planet, both camera-relative under the sky's own projection.
    /// No nebula dome by design. Render states are pinned (P568 lesson): the
    /// sky is a backdrop and must neither write depth nor disturb the opaque
    /// scene's blend/sampler state (mirrors NativeSkyRenderer).</summary>
    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        var skyProj = NativeSkyPlacement.SkyProjection(projection);

        var gd = _gd;
        var prevDepth = gd.DepthStencilState;
        var prevBlend = gd.BlendState;
        var prevRaster = gd.RasterizerState;
        var prevSampler = gd.SamplerStates[0];

        try
        {
            gd.DepthStencilState = DepthStencilState.None;   // backdrop: no depth at all
            gd.RasterizerState = RasterizerState.CullNone;

            // Stars (the old three parallax shells), then the native planet.
            _starFar.Draw(view, skyProj, cameraEye);
            _starMid.Draw(view, skyProj, cameraEye);
            _starNear.Draw(view, skyProj, cameraEye);
            _planet.Draw(view, skyProj, cameraEye, PlanetDist);
        }
        finally
        {
            gd.DepthStencilState = prevDepth;
            gd.BlendState = prevBlend;
            gd.RasterizerState = prevRaster;
            gd.SamplerStates[0] = prevSampler;
        }
    }

    public void Dispose() => _planet.Dispose();
}
