using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P530 — failing-first unit proofs for the shared sector-lighting helper
/// (<see cref="SectorLightingEffect"/>) that every 3D scene renderer (ships,
/// stations, missiles, probes, building effects, asteroids) now routes its
/// BasicEffect through, and for the per-sector change behaviour. Following the
/// P529 split: the PURE derivation is asserted here (BasicEffect itself needs a
/// real GraphicsDevice the unit suite doesn't have); the real BasicEffect
/// application is proven offscreen in the P530 visual proof.
/// </summary>
public sealed class P530SectorLightingAllObjectsTests
{
    private const float Tol = 1e-3f;

    private static void AssertVec(Vector3 v, float x, float y, float z)
    {
        Assert.InRange(v.X, x - Tol, x + Tol);
        Assert.InRange(v.Y, y - Tol, y + Tol);
        Assert.InRange(v.Z, z - Tol, z + Tol);
    }

    /// <summary>The shared helper maps a sector's key/alt/ambient onto the
    /// effect-ready parameters. Light on the +X side (toward = (1,0,0)), key
    /// colour 0x00FF8000: the key propagates the OPPOSITE way (−toward), the
    /// alt back-light from the other side at native constant blue, 0.25 ambient.</summary>
    [Fact]
    public void Derive_MapsKeyAltAndAmbientFromSectorLighting()
    {
        var lighting = SectorLighting.FromCluster([1f, 0f, 0f], 0x00FF8000);

        var s = SectorLightingEffect.Derive(lighting);

        AssertVec(s.KeyDirection, -1f, 0f, 0f);
        AssertVec(s.KeyColor, 1f, 128f / 255f, 0f);
        AssertVec(s.AltDirection, 1f, 0f, 0f);
        AssertVec(s.AltColor, 0f, 0.5f, 1f);
        AssertVec(s.Ambient, 0.25f, 0.25f, 0.25f);
    }

    /// <summary>Native parity invariants encoded by the helper: specular is
    /// OFF on every light (native's BidirectionalLightVertex has no specular
    /// term — the exact cause of the asteroid black↔gold flicker), and the
    /// stock third light is OFF (native has no headlight).</summary>
    [Fact]
    public void Derive_EncodesNoSpecularAndNoThirdLight()
    {
        var s = SectorLightingEffect.Derive(SectorLighting.FromCluster([0f, 0f, 1f], 0xFFFFFFFF));

        Assert.True(s.SpecularZero, "native has no specular — the helper must encode specular-off");
        Assert.False(s.ThirdLightEnabled, "native has no third light / headlight — the helper must keep it off");
    }

    /// <summary>
    /// P564 — the sector-lit pass must ENABLE lighting. Native always runs with
    /// <c>D3DRS_LIGHTING TRUE</c> (device3d.cpp:749 non-bidirectional, :791
    /// bidirectional), but BasicEffect's <c>LightingEnabled</c> defaults to FALSE —
    /// so until P564 every sector-lit object rendered FLAT (ambient + texture only,
    /// the directional lights programmed but never applied): the exact
    /// "everything reads as a flat silhouette" report. The derived settings must
    /// carry <c>LightingEnabled = true</c> so <see cref="SectorLightingEffect.Apply"/>
    /// turns the lights on. Fails on the pre-P564 code (flag absent / false).
    /// </summary>
    [Fact]
    public void Derive_EnablesLightingForDirectionalShading()
    {
        var s = SectorLightingEffect.Derive(SectorLighting.FromCluster([1f, 0f, 0f], 0x00FF8000));

        Assert.True(s.LightingEnabled,
            "the sector-lit pass must have lighting enabled — native always sets D3DRS_LIGHTING TRUE; " +
            "without it every object renders flat (the P564 flat-silhouette report)");
    }

    /// <summary>A sector change swaps the light for every renderer: each
    /// renderer reads the SAME per-frame <c>_sectorLighting</c> (recomputed
    /// from the current sector's cluster every frame), so two clusters with
    /// different light colour/direction must produce different derived
    /// settings — which is what re-renders every ship/station/missile/probe/
    /// building/asteroid under the new sector's light on the warp frame.</summary>
    [Fact]
    public void SectorChange_SwapsTheLightForEveryRenderer()
    {
        // Bright warm sector (sun on +X, strong orange key) …
        var bright = SectorLightingEffect.Derive(
            SectorLighting.FromCluster([1f, 0f, 0f], 0x00FFFFFF));
        // … vs a dim cold sector (sun on −Z, deep blue key).
        var dim = SectorLightingEffect.Derive(
            SectorLighting.FromCluster([0f, 0f, -1f], 0x00001860));

        // Same ambient (bidirectional is a constant 0.25)…
        AssertVec(bright.Ambient, 0.25f, 0.25f, 0.25f);
        AssertVec(dim.Ambient, 0.25f, 0.25f, 0.25f);

        // …but the KEY direction flips and the key colour changes, so the
        // derived settings differ → the light a renderer applies swaps.
        Assert.NotEqual(bright.KeyDirection, dim.KeyDirection);
        Assert.NotEqual(bright.KeyColor, dim.KeyColor);
        Assert.NotEqual(bright, dim);
    }
}
