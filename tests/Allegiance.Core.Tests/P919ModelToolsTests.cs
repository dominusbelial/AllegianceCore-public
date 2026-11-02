using System.Diagnostics;
using System.Globalization;
using System.Text;
using Allegiance.Core.Collision;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit;

namespace Allegiance.Core.Tests;

public class P919ModelToolsTests
{
    [Fact]
    public void Optimize_removes_materials_folds_texture_buckets_and_combines_meshes_to_a_fixed_point()
    {
        // MaterialGeo::RemoveMaterial, TextureGeo/GroupGeo::FoldTexture and
        // GroupGeo::Fold: engine/geometry.cpp:1009-1068,1235-1399.
        var input = Fixture(w => Group(w,
            a => Group(a, b => Material(b, c => Textured(c, "tex", d => Mesh(d, 0))),
                b => Textured(b, "tex", c => Group(c, d => Mesh(d, 2)))),
            a => Textured(a, "other", b => Mesh(b, 4))));
        var result = MdlOptimizer.Optimize(input);
        Assert.True(result.NodesAfter < result.NodesBefore);
        Assert.True(result.Bytes.Length < input.Length);
        Assert.True(result.Passes >= 2);
        var loaded = MdlFile.Parse(result.Bytes);
        Assert.Equal(2, loaded.Meshes.Count);
        Assert.Equal(2, loaded.Meshes.Single(m => m.TextureStem == "tex").TriangleCount);
        Assert.Equal(1, loaded.Meshes.Single(m => m.TextureStem == "other").TriangleCount);
        var expected = MdlFile.Parse(Fixture(w => Group(w,
            a => Textured(a, "tex", b => Mesh(b, 0)),
            a => Textured(a, "tex", b => Mesh(b, 2)),
            a => Textured(a, "other", b => Mesh(b, 4)))));
        Assert.Equal(Triangles(expected.Meshes), Triangles(loaded.Meshes));
        AssertMetadata(expected, loaded);
        Assert.Equal(result.Bytes, MdlOptimizer.Optimize(result.Bytes).Bytes);
    }

    [Fact]
    public void Optimize_combines_common_vertices_without_collapsing_uv_seams_or_split_normals()
    {
        var identical = Fixture(w => Group(w, a => Mesh(a, 0), a => Mesh(a, 0)));
        var mesh = Assert.Single(MdlFile.Parse(MdlOptimizer.Optimize(identical).Bytes).Meshes);
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
        var seams = Fixture(w => Group(w, a => Mesh(a, 0), a => Mesh(a, 0, uvOffset: .5f),
            a => Mesh(a, 0, normalX: 1)));
        mesh = Assert.Single(MdlFile.Parse(MdlOptimizer.Optimize(seams).Bytes).Meshes);
        Assert.Equal(9, mesh.VertexCount);
        Assert.Equal(Triangles(MdlFile.Parse(seams).Meshes), Triangles([mesh]));
    }

    [Fact]
    public void Optimize_hoists_shared_lod_texture_and_the_game_loader_keeps_uvs_and_every_level()
    {
        var input = Fixture(w => Lod(w,
            a => Textured(a, "tex", b => Mesh(b, 0)),
            a => Textured(a, "tex", b => Mesh(b, 10))));
        var before = MdlFile.Parse(input);
        var result = MdlOptimizer.Optimize(input);
        Assert.True(result.NodesAfter < result.NodesBefore);
        var after = MdlFile.Parse(result.Bytes);
        AssertEquivalent(before, after);
        Assert.Equal("tex", Assert.Single(Assert.Single(after.LodLevels).Meshes).TextureStem);
        Assert.Equal(result.Bytes, MdlOptimizer.Optimize(result.Bytes).Bytes);
    }

    [Fact]
    public void Optimize_folds_empty_and_single_child_groups_and_preserves_outer_texture()
    {
        var input = Fixture(w => Group(w, a => Import(a, "emptyGeo"),
            a => Textured(a, "tex", b => Textured(b, "other", c => Mesh(c, 0)))));
        var result = MdlOptimizer.Optimize(input);
        Assert.Equal(2, result.NodesAfter); // TextureGeo + MeshGeo
        Assert.Equal("tex", Assert.Single(MdlFile.Parse(result.Bytes).Meshes).TextureStem);
    }

    [Fact]
    public void Optimize_rejects_text_truncated_and_unknown_models()
    {
        Assert.Throws<InvalidDataException>(() => MdlOptimizer.Optimize(Encoding.ASCII.GetBytes("object = GroupGeo([]);")));
        Assert.Throws<InvalidDataException>(() => MdlOptimizer.Optimize(Fixture(w => Mesh(w, 0))[..^8]));
        Assert.Throws<InvalidDataException>(() => MdlOptimizer.Optimize(Fixture(w =>
        {
            Import(w, "time"); w.Write(6u); // time is not a geometry constructor
        })));
    }

    [SkippableTheory]
    [InlineData("fig30")]
    [InlineData("bom04")]
    [InlineData("ss27")]
    [InlineData("fig05")]
    [InlineData("utl18a")]
    [Trait("Requires", "Artwork")]
    public void Optimize_real_corpus_preserves_geometry_uvs_lods_lights_frames_and_hardpoints(string stem)
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var bytes = File.ReadAllBytes(Path.Combine(art, stem + ".mdl"));
        var before = MdlFile.Parse(bytes);
        var result = MdlOptimizer.Optimize(bytes);
        Assert.True(result.NodesAfter <= result.NodesBefore);
        var after = MdlFile.Parse(result.Bytes);
        AssertEquivalent(before, after);
        Assert.Equal(result.Bytes, MdlOptimizer.Optimize(result.Bytes).Bytes);
        var cvh = CvhReader.Parse(File.ReadAllText(Path.Combine(art, stem + ".cvh")));
        var a = NativeModelAdapter.BuildMounts(before, cvh);
        var b = NativeModelAdapter.BuildMounts(after, cvh);
        Assert.Equal(a.Select(m => m.NativeName), b.Select(m => m.NativeName));
        foreach (var (x, y) in a.Zip(b))
        {
            Assert.Equal(x.Position, y.Position); Assert.Equal(x.Forward, y.Forward);
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Real_ship_cli_export_import_optimize_and_inspect_reload_in_the_game_loader()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var scratch = Path.Combine(FindRepo(), ".scratch", "p919", "cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string export = Path.Combine(scratch, "export"), import = Path.Combine(scratch, "import"), optimized = Path.Combine(scratch, "optimized");
            Run("export", art, "fig30", "--out", export);
            Run("import", export, "fig30", "--out", import);
            Assert.Contains("game loader reload OK", Run("optimize", import, "fig30", "--out", optimized));
            var info = Run("inspect", optimized, "fig30");
            Assert.Contains("meshes=", info); Assert.Contains("tris=", info);
            Assert.Contains("mounts=", info); Assert.Contains("lights=", info);
            Assert.Contains("collisionHulls=", info); Assert.Contains("lodLevels=", info);
            AssertEquivalent(MdlFile.Parse(Path.Combine(import, "fig30.mdl")), MdlFile.Parse(Path.Combine(optimized, "fig30.mdl")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(import, "fig30.cvh")), File.ReadAllBytes(Path.Combine(optimized, "fig30.cvh")));
            var original = MdlFile.Parse(Path.Combine(art, "fig30.mdl"));
            var written = MdlFile.Parse(Path.Combine(optimized, "fig30.mdl"));
            Assert.Equal(Triangles(original.Meshes), Triangles(written.Meshes));
            AssertMetadata(original, written);
            foreach (string stem in written.Meshes.Select(m => m.TextureStem).OfType<string>().Distinct())
            {
                // P916: file row zero is V=0 on meshes. Verify the *actual CLI*
                // embedding/import path, not a decoder/writer pair with cancelling flips.
                var source = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, stem + ".mdl")), flipVertical: false);
                using var glb = File.OpenRead(Path.Combine(export, "fig30.glb"));
                var image = PngCodec.Decode(GlbReader.Read(glb, null).Images!.Single(i => i.Stem == stem).Png);
                Assert.Equal(source.Rgba, image.Rgba);
                var target = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(optimized, stem + ".mdl")), flipVertical: false);
                Assert.Equal(source.Rgba, target.Rgba);
                Assert.Equal(source.Width, target.Width); Assert.Equal(source.Height, target.Height);
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Run(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = FindRepo() };
        start.ArgumentList.Add(typeof(MdlWriter).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "model CLI timed out");
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private static void AssertEquivalent(MdlFile a, MdlFile b)
    {
        Assert.Empty(b.UnsupportedGeometryNodes);
        Assert.Equal(Triangles(a.Meshes), Triangles(b.Meshes));
        Assert.Equal(a.LodLevels.Count, b.LodLevels.Count);
        foreach (var (x, y) in a.LodLevels.Zip(b.LodLevels))
        {
            Assert.Equal(x.PixelRadius, y.PixelRadius);
            Assert.Equal(Triangles(x.Meshes), Triangles(y.Meshes));
        }
        AssertMetadata(a, b);
    }

    private static void AssertMetadata(MdlFile a, MdlFile b)
    {
        Assert.Equal(a.Frames.Count, b.Frames.Count);
        foreach (var (x, y) in a.Frames.Zip(b.Frames))
        {
            Assert.Equal(x.Name, y.Name); Assert.Equal(x.Position, y.Position);
            Assert.Equal(x.Forward, y.Forward); Assert.Equal(x.Up, y.Up);
        }
        Assert.Equal(a.Lights.Count, b.Lights.Count);
        foreach (var (x, y) in a.Lights.Zip(b.Lights))
        {
            Assert.Equal(x.Color, y.Color); Assert.Equal(x.Position, y.Position);
            Assert.Equal((x.Period, x.Phase, x.RampUp, x.Hold, x.RampDown), (y.Period, y.Phase, y.RampUp, y.Hold, y.RampDown));
        }
    }

    // Mesh merging/reindexing is allowed. Every oriented triangle, with its
    // per-corner position, UV, normal and texture, must remain bit-exact.
    private static string[] Triangles(IReadOnlyList<MdlMesh> meshes) => meshes.SelectMany(m =>
        Enumerable.Range(0, m.Indices.Length / 3).Select(t => (m.TextureStem ?? "<none>") + ":" +
            string.Join(",", m.Indices.Skip(t * 3).Take(3).SelectMany(i =>
                m.Positions.Skip(i * 3).Take(3).Concat(m.TexCoords.Skip(i * 2).Take(2)).Concat(m.Normals.Skip(i * 3).Take(3)))
                .Select(f => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture)))))
        .Order(StringComparer.Ordinal).ToArray();

    private static readonly string[] Imports = ["ModifiableNumber", "LightsGeo", "FrameData", "GroupGeo", "TextureGeo", "MeshGeo", "LODGeo", "MaterialGeo", "Material", "Color", "emptyGeo", "tex", "other", "time"];
    private static void CString(BinaryWriter w, string s)
    {
        var b = Encoding.ASCII.GetBytes(s); w.Write(b); w.Write((byte)0);
        for (int i = b.Length + 1; i % 4 != 0; i++) w.Write((byte)0);
    }
    private static void Import(BinaryWriter w, string name) { w.Write(9u); w.Write(Array.IndexOf(Imports, name)); }
    private static void Float(BinaryWriter w, float f) { w.Write(1u); w.Write(f); }
    private static void Apply(BinaryWriter w, string name, params Action<BinaryWriter>[] args)
    {
        foreach (var arg in args) arg(w); Import(w, name); w.Write(6u);
    }
    private static void Group(BinaryWriter w, params Action<BinaryWriter>[] children) => Apply(w, "GroupGeo", a =>
    {
        a.Write(5u); a.Write(children.Length);
        foreach (var child in children) { child(a); a.Write(0u); }
    });
    private static void Textured(BinaryWriter w, string stem, Action<BinaryWriter> geo) => Apply(w, "TextureGeo", a => Import(a, stem), geo);
    private static void Material(BinaryWriter w, Action<BinaryWriter> geo) => Apply(w, "MaterialGeo", a =>
        Apply(a, "Material", b => Float(b, 1), Color, Color, Color), geo);
    private static void Color(BinaryWriter w) => Apply(w, "Color",
        a => Float(a, 1), a => Float(a, .5f), a => Float(a, .25f), a => Float(a, 1));
    private static void Lod(BinaryWriter w, Action<BinaryWriter> finest, Action<BinaryWriter> coarse) => Apply(w, "LODGeo", a =>
    {
        a.Write(5u); a.Write(1u); coarse(a); Float(a, 64); a.Write(10u); a.Write(0u);
    }, finest);
    private static void Mesh(BinaryWriter w, float offset, float uvOffset = 0, float normalX = 0)
    {
        Import(w, "MeshGeo"); w.Write(7u); w.Write(3u); w.Write(3u);
        for (int i = 0; i < 3; i++)
        {
            w.Write(offset + i); w.Write(i == 1 ? 1f : 0f); w.Write(i == 2 ? 1f : 0f);
            w.Write(uvOffset + 0.13f * i); w.Write(0.27f * i); w.Write(normalX); w.Write(1f); w.Write(0f);
        }
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
    }
    private static byte[] Fixture(Action<BinaryWriter> root)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0xDEBADF00u); w.Write(0x10000u); w.Write(4u); w.Write(Imports.Length); w.Write(4u); w.Write(0u);
        foreach (var ns in new[] { "model", "effect", "tex", "other" }) CString(w, ns);
        foreach (var name in Imports)
        {
            w.Write(name is "LightsGeo" or "FrameData" ? 1u : name == "tex" ? 2u : name == "other" ? 3u : 0u); CString(w, name);
        }
        foreach (var name in new[] { "frame", "lights", "frames", "object" }) CString(w, name);
        w.Write(0u); Apply(w, "ModifiableNumber", a => Float(a, 60)); w.Write(0u);
        w.Write(1u); Import(w, "LightsGeo"); w.Write(7u); w.Write(1u);
        foreach (float f in new float[] { 1, .5f, .25f, 1, 3, 2, 1, 1.25f, .4f, .1f, 0, .05f }) w.Write(f);
        w.Write(0u);
        w.Write(2u); Import(w, "FrameData"); w.Write(7u); w.Write(1u); CString(w, "wepmnt1");
        foreach (float f in new float[] { 1, 2, 3, 0, 0, -1, 0, 1, 0 }) w.Write(f);
        w.Write(0u);
        w.Write(3u); root(w); w.Write(0u);
        return ms.ToArray();
    }
}
