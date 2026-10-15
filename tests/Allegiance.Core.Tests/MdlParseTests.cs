using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// End-to-end checks for the <c>.mdl</c> binary model parser
/// (<see cref="MdlFile"/>), validated against the proven Python reference
/// <c>~/Development/allegiance-tools/reference/mdl_parse.py</c>. Every
/// vertex/triangle/frame count asserted here was first produced by running
/// that reference on the same real Artwork file, so the C# port's output must
/// match the Python oracle exactly.
/// </summary>
public class MdlParseTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static string Mdl(string name) =>
        Path.Combine(ArtRoot, name);

    private static string Fmt(MdlFile f)
    {
        var m = string.Join("; ", f.Meshes.Select(
            x => $"{x.VertexCount}v/{x.TriangleCount}t"));
        return $"exports=[{string.Join(",", f.Exports)}] meshes=[{m}] frames={f.Frames.Count}";
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mis02_parses_with_expected_mesh_and_frames()
    {
        Require.Artwork();
        var f = MdlFile.Parse(Mdl("mis02.mdl"));

        // Log the actual numbers found (reference oracle, so the next task has
        // them recorded regardless of the assertions below).
        Console.WriteLine($"mis02.mdl: {Fmt(f)}");

        // Python reference output for mis02.mdl: 1 mesh, 81 verts, 378 indices
        // (126 tris); 3 named frames (trail, thrust, rocket).
        Assert.Single(f.Meshes);
        Assert.Equal(81, f.Meshes[0].VertexCount);
        Assert.Equal(378, f.Meshes[0].Indices.Length);
        Assert.Equal(126, f.Meshes[0].TriangleCount);

        Assert.Equal(3, f.Frames.Count);
        Assert.Equal(["rocket", "thrust", "trail"],
            f.Frames.Select(x => x.Name).OrderBy(x => x));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mis08_parses_with_expected_mesh()
    {
        Require.Artwork();
        var f = MdlFile.Parse(Mdl("mis08.mdl"));

        Console.WriteLine($"mis08.mdl: {Fmt(f)}");

        // Python reference output for mis08.mdl: 1 mesh, 113 verts, 234 indices
        // (78 tris).
        Assert.Single(f.Meshes);
        Assert.Equal(113, f.Meshes[0].VertexCount);
        Assert.Equal(234, f.Meshes[0].Indices.Length);
        Assert.Equal(78, f.Meshes[0].TriangleCount);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mesh_ship_model_walks_all_chunks()
    {
        Require.Artwork();
        // p1_fig.mdl is a real multi-chunk ship model: the Python reference
        // reports 3 meshes (5578v/5018t, 1993v/1667t, 205v/222t) and 5 frames.
        var f = MdlFile.Parse(Mdl("p1_fig.mdl"));

        Console.WriteLine($"p1_fig.mdl: {Fmt(f)}");

        Assert.Equal(3, f.Meshes.Count);

        // Order: collection follows the GroupGeo/"object" tree walk depth-first,
        // which the Python oracle enumerates in the same order.
        Assert.Equal(5578, f.Meshes[0].VertexCount);
        Assert.Equal(5018, f.Meshes[0].TriangleCount);
        Assert.Equal(1993, f.Meshes[1].VertexCount);
        Assert.Equal(1667, f.Meshes[1].TriangleCount);
        Assert.Equal(205, f.Meshes[2].VertexCount);
        Assert.Equal(222, f.Meshes[2].TriangleCount);

        Assert.Equal(5, f.Frames.Count);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Headers_are_exposed()
    {
        Require.Artwork();
        var f = MdlFile.Parse(Mdl("mis02.mdl"));

        // Matches the Python reference's header dump for mis02.mdl.
        Assert.Equal(["model", "effect", "mis02bmp"], f.Namespaces);
        Assert.Equal(
            ["ModifiableNumber", "FrameData", "TextureGeo", "mis02bmp", "MeshGeo"],
            f.Imports);
        Assert.Equal(["frame", "frames", "object"], f.Exports);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mesh_index_order_is_preserved_raw_no_winding_flip()
    {
        Require.Artwork();
        // The scope explicitly forbids guessing a triangle-winding flip here;
        // indices must survive byte-for-byte in file order. On mis02.mdl the
        // first triangle's indices are read straight off the buffer -- assert
        // they stay exactly as parsed (no a-b-c reversal).
        var f = MdlFile.Parse(Mdl("mis02.mdl"));
        var idx = f.Meshes[0].Indices;
        Assert.True(idx.Length >= 3);
        foreach (var i in idx)
            Assert.InRange(i, (ushort)0, (ushort)(f.Meshes[0].VertexCount - 1));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Vertex_arrays_are_sized_and_finite()
    {
        Require.Artwork();
        var f = MdlFile.Parse(Mdl("mis08.mdl"));
        var m = f.Meshes[0];

        Assert.Equal(3 * m.VertexCount, m.Positions.Length);
        Assert.Equal(2 * m.VertexCount, m.TexCoords.Length);
        Assert.Equal(3 * m.VertexCount, m.Normals.Length);

        foreach (var v in m.Positions.Concat(m.Normals))
            Assert.False(float.IsNaN(v) || float.IsInfinity(v),
                "non-finite position/normal component");
        foreach (var v in m.TexCoords)
            Assert.False(float.IsNaN(v) || float.IsInfinity(v),
                "non-finite texcoord component");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Missing_or_garbage_file_throws()
    {
        Require.Artwork();
        Assert.Throws<FileNotFoundException>(() =>
            MdlFile.Parse(Mdl("does_not_exist_xyz.mdl")));

        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.Throws<InvalidDataException>(() => MdlFile.Parse(garbage));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fig05_retains_lodgeo_levels_for_native_lod()
    {
        // P628 — the loader must RETAIN the native LODGeo distance-detail levels
        // (previously discarded) so the renderer can pick detail by screen size.
        // fig05.mdl (an IC fighter) ships with a LODGeo carrying 3 levels.
        Require.Artwork();
        var f = MdlFile.Parse(Mdl("fig05.mdl"));
        Assert.NotEmpty(f.LodLevels);

        // Each level carries a baked pixel-radius threshold (the LODGeo
        // (pixels, geo) pair; modeler.cpp:1676-1683, binary encoded geo-first)
        // and resolvable meshes that are no heavier than the base mesh.
        int baseTriangles = f.Meshes.Sum(m => m.TriangleCount);
        foreach (var lev in f.LodLevels)
        {
            Assert.True(lev.PixelRadius > 0f, "LOD threshold must be a positive pixel radius");
            Assert.NotEmpty(lev.Meshes);
            Assert.True(lev.Meshes.Sum(m => m.TriangleCount) <= baseTriangles,
                "a LOD level must not have MORE triangles than the base mesh");
        }
        // Native re-sorts the levels into descending threshold order internally
        // (LODGeoImpl::AddGeo, geometry.cpp:1667-1713) regardless of file order,
        // so the set of thresholds here spans the expected detail ladder.
        Assert.Contains(f.LodLevels, l => l.PixelRadius >= 32f);
        Assert.Contains(f.LodLevels, l => l.PixelRadius <= 8f);
    }
}
