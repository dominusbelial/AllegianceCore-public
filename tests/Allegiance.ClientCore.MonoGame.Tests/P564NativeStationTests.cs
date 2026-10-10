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
        // P570 — the raw DirectX winding is correct under MonoGame's perspective.
        var raw = new ushort[] { 0, 1, 2, 3, 4, 5 };
        Assert.Equal(raw, NativeStationResolver.RenderIndices(raw));
    }

    [Theory]
    [InlineData("ss24a", "ss24abmp")]   // standard Garrison
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
    [InlineData("ss300")]
    [InlineData("ss101")]
    public void Shipped_native_station_winding_keeps_the_outside_under_the_game_perspective(string model)
    {
        var f = TryParse(model);
        if (f is null) return;   // artwork absent here -> inert, not a false fail
        var mesh = f.Meshes.Where(m => m.VertexCount >= 3 && m.Indices.Length >= 3)
                           .OrderByDescending(m => m.Indices.Length).FirstOrDefault();
        if (mesh is null) return;

        // Per-model normalisation: keep raw when already outside-kept, else reverse.
        var shipped = NativeStationResolver.OutsideIndices(mesh);
        var (total, kept, culled) = MeasureStationWinding(mesh, shipped);

        Assert.True(total > 50, $"'{model}' parsed too little geometry ({total} camera-facing triangles)");
        double keptFraction = (double)kept / total;
        Assert.True(keptFraction >= 0.9,
            $"'{model}': only {keptFraction:P0} of the near-surface faces are kept by " +
            $"CullCounterClockwiseFace (should be ≥90%) — winding correction failed");
    }

    [Fact]
    public void Opposite_station_windings_are_both_normalised_to_keep_the_outside()
    {
        // The shipped corpus is NOT winding-uniform: ss24a (raw) and ss92 (raw) wind
        // oppositely on their near surface. OutsideIndices must normalise BOTH to
        // keep the outside (ss92 must be reversed). This guards the per-model fix.
        foreach (var model in new[] { "ss24a", "ss92" })
        {
            var f = TryParse(model);
            if (f is null) return;
            var mesh = f.Meshes.Where(m => m.VertexCount >= 3 && m.Indices.Length >= 3)
                               .OrderByDescending(m => m.Indices.Length).FirstOrDefault();
            if (mesh is null) return;

            var (total, kept, culled) = MeasureStationWinding(mesh, NativeStationResolver.OutsideIndices(mesh));
            if (total == 0) continue;
            Assert.True((double)kept / total >= 0.9,
                $"'{model}' still inside-out after OutsideIndices ({kept}/{total} kept)");
        }
    }

    /// <summary>The P570 winding proof adapted for a station (a bigger model, so a
    /// camera-relative distance of 600 is even safer). Station transform folds the
    /// native RY(π) flip + uniform scale ~ radius/meshRadius (≈1 for a correctly
    /// authored station mesh) before the caller world.</summary>
    private static (int Total, int Kept, int Culled) MeasureStationWinding(MdlMesh mesh, ushort[] indices)
    {
        var noseFlip = Matrix.CreateRotationY(MathHelper.Pi);
        var world = Matrix.CreateTranslation(new Vector3(0f, 0f, -600f));
        var local = noseFlip * world;
        var view = Matrix.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, -600f), Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 16f / 9f, 0.1f, 60000f);
        var localViewProj = local * view * proj;

        Vector3 meshCentroid = Vector3.Zero;
        for (int v = 0; v < mesh.VertexCount; v++)
            meshCentroid += new Vector3(mesh.Positions[3 * v], mesh.Positions[3 * v + 1], mesh.Positions[3 * v + 2]);
        meshCentroid /= Math.Max(1, mesh.VertexCount);

        double alignSum = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            var pa = new Vector3(mesh.Positions[3 * ia], mesh.Positions[3 * ia + 1], mesh.Positions[3 * ia + 2]);
            var pb = new Vector3(mesh.Positions[3 * ib], mesh.Positions[3 * ib + 1], mesh.Positions[3 * ib + 2]);
            var pc = new Vector3(mesh.Positions[3 * ic], mesh.Positions[3 * ic + 1], mesh.Positions[3 * ic + 2]);
            var n = Vector3.Cross(pb - pa, pc - pa);
            var center = (pa + pb + pc) / 3f;
            alignSum += Vector3.Dot(n, center - meshCentroid);
        }
        float globalSign = alignSum >= 0 ? 1f : -1f;

        int kept = 0, culled = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            var pa = new Vector3(mesh.Positions[3 * ia], mesh.Positions[3 * ia + 1], mesh.Positions[3 * ia + 2]);
            var pb = new Vector3(mesh.Positions[3 * ib], mesh.Positions[3 * ib + 1], mesh.Positions[3 * ib + 2]);
            var pc = new Vector3(mesh.Positions[3 * ic], mesh.Positions[3 * ic + 1], mesh.Positions[3 * ic + 2]);

            var wa = Vector3.Transform(pa, local);
            var wb = Vector3.Transform(pb, local);
            var wc = Vector3.Transform(pc, local);
            var outward = globalSign * Vector3.TransformNormal(Vector3.Cross(pb - pa, pc - pa), local);
            var centroid = (wa + wb + wc) / 3f;
            bool cameraFacing = Vector3.Dot(outward, -centroid) > 0f;
            if (!cameraFacing) continue;

            Vector4 ca = Vector4.Transform(new Vector4(pa, 1f), localViewProj);
            Vector4 cb = Vector4.Transform(new Vector4(pb, 1f), localViewProj);
            Vector4 cc = Vector4.Transform(new Vector4(pc, 1f), localViewProj);
            if (ca.W <= 1e-6f || cb.W <= 1e-6f || cc.W <= 1e-6f) continue;
            float ax = ca.X / ca.W, ay = ca.Y / ca.W;
            float bx = cb.X / cb.W, by = cb.Y / cb.W;
            float cx = cc.X / cc.W, cy = cc.Y / cc.W;
            float signedArea = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (signedArea < 0f) kept++; else culled++;
        }
        return (kept + culled, kept, culled);
    }
}
