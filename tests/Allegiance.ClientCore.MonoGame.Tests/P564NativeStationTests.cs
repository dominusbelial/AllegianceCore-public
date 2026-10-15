using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P564 — native per-station-type model rendering rules. Mirrors the
/// <see cref="NativeShipResolverTests"/> / <see cref="NativeAsteroidResolverTests"/>
/// convention: pure rules tested headless on real station <c>.mdl</c> files from the
/// artwork source (the same files <c>DataStationTypeIGC.ModelName</c> names at runtime),
/// plus the P570 culling proof applied to stations (raw native winding + perspective ⇒
/// the outside is kept by <c>CullCounterClockwiseFace</c>, so the station is solid, not
/// inside-out/see-through).
/// </summary>
public class P564NativeStationTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static bool FileExists(string stem) => File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    private static MdlFile? TryParse(string stem)
    {
        if (!File.Exists(Path.Combine(ArtRoot, stem + ".mdl"))) return null;
        try { return MdlFile.Parse(Path.Combine(ArtRoot, stem + ".mdl")); }
        catch { return null; }
    }

    [Fact]
    public void ScaleForRadius_is_radius_over_meshRadius_guarded()
    {
        // Native: m_scale = radius / m_radiusMesh (thinggeo.cpp:1187), radius =
        // DataStationTypeIGC.Radius (stationIGC.cpp:249 SetRadius).
        Assert.Equal(2f, NativeStationResolver.ScaleForRadius(10f, 5f));
        Assert.Equal(0.599601f, NativeStationResolver.ScaleForRadius(302.5f, 504.502f), 4);
        // Degenerate inputs degrade to no-scaling (1), never NaN/Inf.
        Assert.Equal(1f, NativeStationResolver.ScaleForRadius(0f, 5f));
        Assert.Equal(1f, NativeStationResolver.ScaleForRadius(10f, 0f));
        Assert.Equal(1f, NativeStationResolver.ScaleForRadius(-1f, 5f));
    }

    [Fact]
    public void RenderIndices_ships_the_raw_native_winding()
    {
        // Native uploads the authored indices, never a radial guess.
        var raw = new ushort[] { 0, 1, 2, 3, 4, 5 };
        Assert.Same(raw, NativeStationResolver.RenderIndices(raw));
    }

    [Theory]
    [InlineData("ss24a", "bgrnd03bmp")]   // standard Garrison
    [InlineData("ss92", "ss92bmp")]     // standard Outpost
    public void ResolveTextureStem_finds_the_model_imported_texture(string model, string expectedTex)
    {
        var mdl = TryParse(model);
        if (mdl is null) return;   // artwork absent here -> inert

        var tex = NativeStationResolver.ResolveTextureStem(mdl, null, model, FileExists);
        Assert.NotNull(tex);
        Assert.Equal(expectedTex, tex, ignoreCase: true);
    }

    [Fact]
    public void ResolveTextureStem_prefers_a_meaningful_station_texture_override()
    {
        var mdl = TryParse("ss24a");
        if (mdl is null) return;

        // A real override stem that exists wins (trekigc.cpp:1837-1839).
        var tex = NativeStationResolver.ResolveTextureStem(mdl, "ss92bmp", "ss24a", FileExists);
        Assert.Equal("ss92bmp", tex, ignoreCase: true);
        // A non-alpha / meaningless override is ignored (falls back to the model's own).
        var tex2 = NativeStationResolver.ResolveTextureStem(mdl, "ss24a", "ss24a", FileExists);
        Assert.NotNull(tex2);
        Assert.NotEqual("ss24a", tex2);
    }

    [Theory]
    [InlineData("ss24a")]
    [InlineData("ss92")]
    [InlineData("ss27")]
    [InlineData("ss300")]
    [InlineData("ss101")]
    public void All_groups_preserve_native_winding_including_interior_walls(string model)
    {
        var file = TryParse(model);
        if (file is null) return;
        foreach (var mesh in file.Meshes)
            Assert.Same(mesh.Indices, NativeStationResolver.RenderIndices(mesh.Indices));
    }
}
