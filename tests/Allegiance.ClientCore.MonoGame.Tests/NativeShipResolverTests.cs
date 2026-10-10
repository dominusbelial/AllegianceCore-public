using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P566 — failing-first tests for the NATIVE ship mesh renderer rules
/// (<see cref="NativeShipResolver"/>): triangle-winding correction, native
/// scale, and the hull → model/texture resolution. Winding/scale are pure
/// synthetic; the resolution tests additionally validate against the REAL
/// artwork tree (the same real-asset convention as
/// <see cref="RockTextureSourceTests"/> / <see cref="HullModelResolverTests"/>).
/// </summary>
public class NativeShipResolverTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static bool FileExists(string stem) => File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    private static MdlFile Parse(string stem) => MdlFile.Parse(Path.Combine(ArtRoot, stem + ".mdl"));

    // ---- Winding correction ------------------------------------------------

    [Fact]
    public void CorrectWinding_reverses_each_triangle_indices()
    {
        // The native .mdl stores DirectX clockwise-front triangles (a b c); the
        // renderer reverses each to (c b a) for MonoGame counter-clockwise front.
        var result = NativeShipResolver.CorrectWinding(new ushort[] { 0, 1, 2, 3, 4, 5 });
        Assert.Equal(new ushort[] { 2, 1, 0, 5, 4, 3 }, result);
    }

    [Fact]
    public void CorrectWinding_flips_front_face_from_inward_to_outward()
    {
        // A raw DirectX-front triangle stores its vertices clockwise, so its
        // right-hand-rule geometric normal points INWARD (away from the viewer /
        // outward face). After the winding correction it must point OUTWARD.
        // Take the +Z outward face of a cube: outward = +Z, so raw normal = -Z.
        // Raw order [0,1,2] with v0=(0,0,0), v1=(0,1,0), v2=(1,0,0):
        //   cross(v1-v0, v2-v0) = cross((0,1,0),(1,0,0)) = (0,0,-1)  [inward]
        var positions = new float[]
        {
            0f, 0f, 0f,   // v0
            0f, 1f, 0f,   // v1
            1f, 0f, 0f,   // v2
        };
        var raw = new ushort[] { 0, 1, 2 };

        var corrected = NativeShipResolver.CorrectWinding(raw);
        Vector3 n = FaceNormal(positions, corrected);
        // After reversal the geometric normal flips to +Z — the triangle now
        // faces OUTWARD, i.e. it is front-facing under MonoGame CullCounterClockwise.
        Assert.True(n.Z > 0.99f, $"expected outward +Z normal after correction, got {n}");
    }

    private static Vector3 FaceNormal(float[] positions, ushort[] idx)
    {
        Vector3 v0 = Pt(positions, idx[0]), v1 = Pt(positions, idx[1]), v2 = Pt(positions, idx[2]);
        var n = Vector3.Cross(v1 - v0, v2 - v0);
        float len = n.Length();
        return len < 1e-6f ? Vector3.Zero : n / len;
    }

    private static Vector3 Pt(float[] p, ushort i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);

    // ---- Native scale ------------------------------------------------------

    [Theory]
    [InlineData(20f, 10f)]   // scale = (0.5*20)/10 = 1  (native-authored size)
    [InlineData(50f, 100f)]  // a half-size mesh gets doubled up to hull radius
    [InlineData(30f, 15f)]
    [InlineData(0f, 10f)]    // unknown length → 1 (no scaling)
    [InlineData(20f, 0f)]    // unknown radius → 1 (no scaling)
    public void NativeScaleForLength_maps_mesh_radius_to_hull_collision_radius(float len, float radius)
    {
        float scale = NativeShipResolver.NativeScaleForLength(len, radius);
        float expected = (len > 0f && radius > 0f) ? (0.5f * len) / radius : 1f;
        Assert.Equal(expected, scale, 5);
        // Invariant: the rendered radius equals the gameplay collision radius 0.5·Length.
        if (len > 0f && radius > 0f)
            Assert.Equal(0.5f * len, radius * scale, 4);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealFig05_mesh_radius_and_scale_agree_with_native_radius_math()
    {
        Require.Artwork();
        // Native sizes every ship so its mesh radius becomes 0.5·HullType.Length
        // (shipIGC.cpp:1419, thinggeo.cpp:1185-1187). For a real fighter mesh the
        // rendered radius must equal half the hull length after the native scale.
        var mdl = Parse("fig05");
        float radius = NativeShipResolver.MergedRadius(mdl.Meshes);
        Assert.True(radius > 0f);
        float scale = NativeShipResolver.NativeScaleForLength(30f, radius); // ~fighter length
        Assert.Equal(0.5f * 30f, radius * scale, 3);
    }

    // ---- Hull → model / texture resolution (real assets) -------------------

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fig05_resolves_its_own_bmp_texture_namespace()
    {
        Require.Artwork();
        var mdl = Parse("fig05");
        // fig05.mdl imports fig05bmp; no hull override → the model's own texture.
        Assert.Equal("fig05bmp", NativeShipResolver.ResolveTextureStem(mdl, null, "fig05", FileExists));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void WcGcfig_resolves_shared_texture_not_own_stem()
    {
        Require.Artwork();
        // wc_gcfig.mdl has NO wc_gcfigbmp.mdl — it imports the SHARED fig07bmp
        // (the task's "shared textures": several model variants share one unit).
        var mdl = Parse("wc_gcfig");
        Assert.Equal("fig07bmp", NativeShipResolver.ResolveTextureStem(mdl, null, "wc_gcfig", FileExists));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Utl18a_resolves_shared_utl18bmp_not_utl18abmp()
    {
        Require.Artwork();
        // The wiki's explicit caution: utl18a.mdl imports utl18bmp, NOT utl18abmp.
        var mdl = Parse("utl18a");
        Assert.Equal("utl18bmp", NativeShipResolver.ResolveTextureStem(mdl, null, "utl18a", FileExists));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void HullTextureName_override_wins_when_present_and_distinct()
    {
        Require.Artwork();
        // Native LoadModel honours a real alpha-leading texture override that
        // differs from the model name (trekigc.cpp:1837-1839): the override is
        // used even though the model has its own valid texture namespace.
        var mdl = Parse("fig05");
        bool BothExist(string stem) => stem == "rewbmp" || stem == "fig05bmp";
        Assert.Equal("rewbmp", NativeShipResolver.ResolveTextureStem(mdl, "rewbmp", "fig05", BothExist));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void HullTextureName_equal_to_model_is_ignored_native_nullout()
    {
        Require.Artwork();
        // Native nulls a texture override equal to the model name (trekigc.cpp:1837-1839)
        // → falls back to the model's own imported texture.
        var mdl = Parse("fig05");
        Assert.Equal("fig05bmp", NativeShipResolver.ResolveTextureStem(mdl, "fig05", "fig05", FileExists));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_resolvable_texture_unit_yields_null()
    {
        Require.Artwork();
        // A model whose texture unit is missing → null → the renderer draws the
        // hull lit-solid (part of the fallback chain: native → Meshy → placeholder).
        var mdl = Parse("fig05");
        var stem = NativeShipResolver.ResolveTextureStem(mdl, null, "fig05", _ => false);
        Assert.Null(stem);
    }

    /// <summary>
    /// P586b — weapon .mdl textures are inconsistent about the doubled 'b':
    /// wep08.mdl imports the namespace "wep08bbmp" but the on-disk unit is
    /// "wep08bmp.mdl" (native's "<name>bmp" convention, xfile.cpp:577). The
    /// resolver must fall back to the one-'b'-shorter "wep08bmp" unit so the
    /// weapon model textures instead of rendering lit-solid. Fails on pre-P586b
    /// code, where ResolveTextureStem only probed the raw namespace.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void WeaponDoubledB_import_resolves_to_single_b_bmp_unit()
    {
        Require.Artwork();
        var mdl = Parse("wep08");                       // namespace "wep08bbmp"
        Assert.Equal("wep08bmp", NativeShipResolver.ResolveTextureStem(mdl, null, "wep08", FileExists));
        // And a model whose doubled-b unit ALSO exists on disk keeps it (wep03bbmp.mdl).
        var mdl3 = Parse("wep03");
        Assert.Equal("wep03bbmp", NativeShipResolver.ResolveTextureStem(mdl3, null, "wep03", FileExists));
    }

    /// <summary>
    /// P586b — on a case-sensitive filesystem the renderer resolves a model's
    /// texture stem case-insensitively (native Windows semantics), so the EW
    /// Disruptor's capitalised "Wep07bbmp.mdl" unit is found from the lowercase
    /// "wep07bbmp" namespace (via <see cref="NativeTreasureRenderer"/>'s disk
    /// index; the resolver itself is case-agnostic here because the caller
    /// hands it a case-insensitive probe).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CaseInsensitiveProbe_finds_capitalised_texture_unit()
    {
        Require.Artwork();
        var mdl = Parse("wep07");                       // namespace "wep07bbmp"
        // A case-insensitive probe (the renderer's ExactStem disk index) finds
        // "Wep07bbmp.mdl" even though File.Exists("wep07bbmp.mdl") is false.
        var onDisk = new[] { "Wep07bbmp" };             // only the capitalised unit exists on disk
        bool Probe(string s) => System.Array.FindIndex(onDisk, x => x.Equals(s, StringComparison.OrdinalIgnoreCase)) >= 0;
        Assert.Equal("wep07bbmp", NativeShipResolver.ResolveTextureStem(mdl, null, "wep07", Probe));
    }

    // ---- Native nozzle frames (P566 item 2) -------------------------------

    [Fact]
    public void AftNozzleLocalOffset_uses_the_thrust_frame()
    {
        // Native AddThrustParticles keys on frames named rocket/thrust/smoke
        // (thinggeo.cpp:786-794); the aft nozzle anchor should be that frame.
        var frames = new[]
        {
            new MdlFrame { Name = "gun", Position = new float[] { 0f, 0f, 10f }, Forward = new float[] { 0f, 0f, -1f }, Up = new float[] { 0f, 1f, 0f } },
            new MdlFrame { Name = "rocket", Position = new float[] { 0f, 1f, -12f }, Forward = new float[] { 0f, 0f, 1f }, Up = new float[] { 0f, 1f, 0f } },
        };
        var off = NativeShipResolver.AftNozzleLocalOffset(frames, 5f);
        Assert.Equal(new Vector3(0f, 1f, -12f), off);
    }

    [Fact]
    public void AftNozzleLocalOffset_falls_back_to_guessed_aft_centre()
    {
        // No rocket/thrust/smoke frame → the pre-P566 guessed aft-centre (0,0,+r).
        var frames = new[]
        {
            new MdlFrame { Name = "cockpt", Position = new float[] { 0f, 0f, 8f }, Forward = new float[] { 0f, 0f, -1f }, Up = new float[] { 0f, 1f, 0f } },
        };
        Assert.Equal(new Vector3(0f, 0f, 6f), NativeShipResolver.AftNozzleLocalOffset(frames, 6f));
    }

    // ---- Launch default (--ships native) -----------------------------------

    [Fact]
    public void LaunchOptions_default_ships_native()
    {
        // The --ships setting defaults to NATIVE (the P566 default); only an
        // explicit --ships meshy turns the Meshy content path back on.
        var opts = new Allegiance.ClientCore.MonoGame.Game1.LaunchOptions();
        Assert.True(opts.ShipsNative);
    }
}
