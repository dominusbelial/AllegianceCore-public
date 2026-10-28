using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P588/P589 — the <b>black sky</b> tier (<c>--sky black</c>, the DEFAULT):
/// a pure black backdrop with the restored <b>pre-P576 point starfield</b>
/// (three parallax <see cref="Starfield"/> shells) plus the <b>native planet
/// poster</b> — and <b>no nebula dome</b>. P589 adds the space objects that
/// read beautifully against black (Domingo's art direction): the <b>native sun
/// + lens flare</b> at the sector's light direction, <b>moons</b> (native art),
/// <b>distant galaxies / small nebulae</b> cut-outs and the <b>deep-field
/// galaxy field + Little Red Dots</b> — all "paintings" on the black sky,
/// camera-relative, drawn behind everything. Ambient space dust is drawn
/// separately, unchanged.
///
/// Every P589 object is toggleable off: <c>--moons off</c>,
/// <c>--distant-objects off</c>, <c>--galaxies off</c> (the sun/lens flare is
/// native and ON always).
///
/// Chosen star layer: the PRE-P576 <see cref="Starfield"/> shells, restored
/// exactly (same radii/count/seeds/brightness as the original creation in
/// <c>Game1.Lifecycle.cs:303-308</c>), NOT the P576 seeded stars — because
/// that is literally the "old black sky" Domingo asked to keep.
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

    /// <summary>Moon/distant-object shell distance — just beyond the planet so
    /// the moons hug the planet's neighbourhood and the distant objects sit
    /// behind it.</summary>
    private const float ObjectDist = 7800f;

    private readonly GraphicsDevice _gd;
    private readonly Starfield _starNear;
    private readonly Starfield _starMid;
    private readonly Starfield _starFar;
    private readonly PlanetPoster _planet;
    private readonly LensFlareRenderer _lensFlare;
    private readonly MoonRenderer _moons;
    private readonly DistantObjectRenderer _distant;
    private readonly GalaxyFieldRenderer _galaxies;
    private readonly GiantObjectRenderer _giants;

    private readonly bool _moonsOn;
    private readonly bool _distantObjectsOn;
    private readonly bool _galaxiesOn;
    private readonly bool _giantObjectsOn;

    public BlackSkyRenderer(
        GraphicsDevice gd,
        IAssetSource assets,
        bool moonsOn = true,
        bool distantObjectsOn = true,
        bool galaxiesOn = true,
        bool giantObjectsOn = true)
    {
        _gd = gd;
        _moonsOn = moonsOn;
        _distantObjectsOn = distantObjectsOn;
        _galaxiesOn = galaxiesOn;
        _giantObjectsOn = giantObjectsOn;
        // Restored pre-P576 starfield shells — the exact old look, centred on
        // the origin: near band brighter + sparser (fast, sharp parallax),
        // far band dimmer + denser (slow, backdrop-like). Parameters match the
        // original creation verbatim (Game1.Lifecycle.cs:303-308).
        _starNear = new Starfield(gd, Vector3.Zero, 1200f, 2200f, 500, seed: 111, minBrightness: 220, maxBrightness: 255);
        _starMid = new Starfield(gd, Vector3.Zero, 2200f, 3800f, 1200, seed: 222, minBrightness: 160, maxBrightness: 220);
        _starFar = new Starfield(gd, Vector3.Zero, 3800f, 6000f, 2500, seed: 333, minBrightness: 90, maxBrightness: 160);
        _planet = new PlanetPoster(gd, assets);
        _lensFlare = new LensFlareRenderer(gd, assets);
        _moons = new MoonRenderer(gd, assets);
        _distant = new DistantObjectRenderer(gd, assets);
        _galaxies = new GalaxyFieldRenderer(gd, assets);
        _giants = new GiantObjectRenderer(gd, assets);
    }

    /// <summary>P862 — decode a planet poster texture into this tier's cache
    /// WITHOUT drawing (the sector asset warm-up). Same loader <see cref="SetCluster"/>
    /// uses. Returns 1 when created now, 0 when already resident, -1 when unavailable.</summary>
    public int WarmPlanet(string planetName) => _planet.Warm(planetName);

    /// <summary>P862 — how many planet poster textures this tier has resident.</summary>
    public int LoadedPlanetTextures => _planet.LoadedTextureCount;

    /// <summary>Switch the sky to a cluster's data (planet poster + P589 sun,
    /// moons, distant objects and galaxy field; stars are the fixed pre-P576
    /// shells). Called on sector change.</summary>
    public void SetCluster(DataClusterIGC cluster)
    {
        _planet.SetCluster(cluster);

        var toward = TowardLight(cluster);
        _lensFlare.SetLightDirection(toward);
        _moons.SetCluster(cluster);
        _distant.SetCluster(cluster);
        _galaxies.SetCluster(cluster);
        _giants.SetCluster(cluster);
    }

    /// <summary>Draw the black sky behind everything: point stars, the native
    /// planet, then the P589 sun/flare, moons, distant objects and galaxy
    /// field, all camera-relative under the sky's own projection. No nebula
    /// dome by design. Render states are pinned (P568 lesson): the sky is a
    /// backdrop and must neither write depth nor disturb the opaque scene's
    /// blend/sampler state.</summary>
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

            // P592 — the GIANT sky object (one curved sky-sphere patch, e.g. the
            // face-on Milky Way over Terra / Andromeda over Alderan), drawn
            // right behind the other paintings, before the sun/lens flare.
            if (_giantObjectsOn) _giants.Draw(view, skyProj, ObjectDist);

            // P589 — the space objects (paintings on the black sky), each
            // gated by its toggle (ON by default).
            if (_moonsOn) _moons.Draw(view, skyProj, cameraEye, ObjectDist);
            if (_distantObjectsOn) _distant.Draw(view, skyProj, cameraEye, ObjectDist);
            if (_galaxiesOn) _galaxies.Draw(view, skyProj, cameraEye, ObjectDist);
            // The sun/lens flare: additive screen-space, drawn last so it reads
            // over the other sky elements (the opaque scene still draws over it).
            _lensFlare.Draw(view, skyProj);
        }
        finally
        {
            gd.DepthStencilState = prevDepth;
            gd.BlendState = prevBlend;
            gd.RasterizerState = prevRaster;
            gd.SamplerStates[0] = prevSampler;
        }
    }

    private static Vector3? TowardLight(DataClusterIGC cluster)
    {
        var ld = cluster.LightDirection;
        if (ld is { Length: 3 })
        {
            float len = MathF.Sqrt(ld[0] * ld[0] + ld[1] * ld[1] + ld[2] * ld[2]);
            if (len >= 1e-6f) return new Vector3(ld[0] / len, ld[1] / len, ld[2] / len);
        }
        return null; // degenerate/missing → no sun this sector
    }

    public void Dispose()
    {
        _planet.Dispose();
        _lensFlare.Dispose();
        _moons.Dispose();
        _distant.Dispose();
        _galaxies.Dispose();
        _giants.Dispose();
    }
}
