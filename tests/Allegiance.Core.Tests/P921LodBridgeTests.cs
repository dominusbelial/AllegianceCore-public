using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests;

public sealed class P921LodBridgeTests(ITestOutputHelper output)
{
    internal static GlbExportModel Fixture()
    {
        GlbMesh Mesh(string name, string? tex, float z) => new(name, tex,
            [0f, -0f, z, 1f, 0f, z, 0f, 1f, z, 0f, 0f, z+1], [.125f, .25f, .5f, .75f, 1f, 0f, .5f, .5f],
            [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], [0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0]);
        return new GlbExportModel
        {
            Name = "lodfixture", Meshes = [Mesh("base", "basebmp", 0)],
            // Deliberately not sorted: retain authored file order.
            LodLevels = [new(12.25f, [Mesh("coarse", "levelbmp", -1)]),
                new(80.5f, [Mesh("mediumA", "basebmp", -2), Mesh("mediumB", null, -3)])],
            LodPixelRadii = [12.25f, 80.5f], Mounts = [], Lights = [],
            TextureStems = ["basebmp", "levelbmp"], Collision = null,
        };
    }

    [Fact]
    public void Synthetic_levels_roundtrip_bit_exactly_without_sidecar_and_optimize_keeps_them()
    {
        var source = Fixture();
        using var glb = Export(source);
        var read = GlbReader.Read(glb, null);
        Assert.True(Compare.Run(source, read).pass);
        var before = MdlFile.Parse(MdlWriter.Write(source));
        var written = MdlFile.Parse(GlbImporter.Convert(read).mdl);
        AssertExact(before, written);
        AssertOptimized(before, MdlFile.Parse(MdlOptimizer.Optimize(MdlWriter.Write(read)).Bytes));
    }

    [Fact]
    public void Radius_in_name_survives_editors_that_strip_extras_and_node_order_is_not_level_order()
    {
        using var glb = Export(Fixture());
        using var changed = Rewrite(glb, doc =>
        {
            foreach (var node in doc["nodes"]!.AsArray()) node!.AsObject().Remove("extras");
            var children = doc["nodes"]![0]!["children"]!.AsArray();
            var values = children.Select(n => n!.DeepClone()).Reverse().ToArray();
            children.Clear(); foreach (var value in values) children.Add(value);
        });
        var read = GlbReader.Read(changed, null);
        Assert.Equal(new[] {12.25f, 80.5f}, read.LodLevels.Select(l => l.PixelRadius));
        AssertExact(MdlFile.Parse(MdlWriter.Write(Fixture())), MdlFile.Parse(MdlWriter.Write(read)));
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(0)] [InlineData(-1)] [InlineData(80.5f)]
    public void Rejects_invalid_or_duplicate_level_radii(float radius)
    {
        var model = Fixture();
        model = model with { LodLevels = [model.LodLevels[0] with { PixelRadius = radius }, model.LodLevels[1]] };
        Assert.Contains("radii", Assert.Throws<InvalidDataException>(() => GlbImporter.Convert(model)).Message);
    }

    [Fact]
    public void Validates_level_meshes_and_level_only_texture_images()
    {
        var model = Fixture(); var level = model.LodLevels[0];
        var invalid = model with { LodLevels = [level with { Meshes = [level.Meshes[0] with { Indices = [0, 1, 9] }] }] };
        Assert.Contains("out of range", Assert.Throws<InvalidDataException>(() => GlbImporter.Convert(invalid)).Message);
        invalid = model with { LodLevels = [level with { Meshes = [] }] };
        Assert.Contains("no meshes", Assert.Throws<InvalidDataException>(() => GlbImporter.Convert(invalid)).Message);
        model = model with { Images = [new("basebmp", 1, 1, [])] };
        Assert.Contains("levelbmp", Assert.Throws<InvalidDataException>(() => GlbImporter.ValidateTextureImages(model)).Message);
    }

    [Fact]
    public void New_art_without_lods_remains_legal()
    {
        var model = Fixture() with { LodLevels = [], LodPixelRadii = [] };
        using var glb = Export(model);
        Assert.Empty(MdlFile.Parse(GlbImporter.Convert(GlbReader.Read(glb, null)).mdl).LodLevels);
    }

    [Fact]
    public void Old_export_warns_through_real_cli_and_still_imports_and_texture_options_cover_levels()
    {
        string scratch = Path.Combine(Repo(), ".scratch", "p921", "cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var model = Fixture();
            using (var glb = File.Create(Path.Combine(scratch, "lodfixture.glb"))) GlbWriter.Write(model with { LodLevels = [] }, glb);
            File.WriteAllText(Path.Combine(scratch, "lodfixture.glbside.json"), "{\"lodPixelRadii\":[12.25,80.5]}");
            string log = Run("import", scratch, "lodfixture", "--out", Path.Combine(scratch, "old"), "--no-texture");
            Assert.Contains("model 'lodfixture' lost 2 LOD levels", log);
            Assert.Empty(MdlFile.Parse(Path.Combine(scratch, "old", "lodfixture.mdl")).LodLevels);
            using (var glb = File.Create(Path.Combine(scratch, "lodfixture.glb"))) GlbWriter.Write(model, glb);
            log = Run("import", scratch, "lodfixture", "--out", Path.Combine(scratch, "new"), "--no-texture");
            Assert.DoesNotContain("warning", log);
            var parsed = MdlFile.Parse(Path.Combine(scratch, "new", "lodfixture.mdl"));
            Assert.Equal(2, parsed.LodLevels.Count);
            Assert.All(parsed.Meshes.Concat(parsed.LodLevels.SelectMany(l => l.Meshes)), m => Assert.Null(m.TextureStem));
            var png = PngCodec.Encode(1, 1, [255, 128, 64, 255]);
            model = model with { Images = [new("basebmp", 1, 1, png), new("levelbmp", 1, 1, png)] };
            using (var glb = File.Create(Path.Combine(scratch, "lodfixture.glb"))) GlbWriter.Write(model, glb);
            Run("import", scratch, "lodfixture", "--out", Path.Combine(scratch, "renamed"), "--texture-stem", "renamedbmp");
            parsed = MdlFile.Parse(Path.Combine(scratch, "renamed", "lodfixture.mdl"));
            Assert.Equal("renamedbmp", parsed.LodLevels[0].Meshes[0].TextureStem);
            Assert.Null(parsed.LodLevels[1].Meshes[1].TextureStem);
        }
        finally { Directory.Delete(scratch, true); }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("fig30")] [InlineData("bom04")] [InlineData("ss27")] [InlineData("fig05")] [InlineData("utl18a")]
    public void Five_native_models_keep_every_level_vertex_triangle_radius_and_texture(string stem)
    {
        Require.Artwork();
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        byte[] native = File.ReadAllBytes(Path.Combine(art, stem + ".mdl"));
        var before = MdlFile.Parse(native);
        var model = NativeModelAdapter.FromNative(stem, before, null);
        using var glb = Export(model);
        using var side = new MemoryStream(Encoding.UTF8.GetBytes(SidecarDocument.Build(model, Path.Combine(art, stem + ".mdl"), Path.Combine(art, stem + ".cvh")).ToJson()));
        var read = GlbReader.Read(glb, side);
        byte[] imported = MdlWriter.Write(read);
        var after = MdlFile.Parse(imported);
        AssertExact(before, after);
        AssertOptimized(before, MdlFile.Parse(MdlOptimizer.Optimize(imported).Bytes));
        // Independent token reader checks the native and generated LOD envelopes,
        // including the geo-before-radius pair, list-entry End and final Apply.
        var nativeEnvelope = TokenProbe.LodEnvelopes(native);
        Assert.NotEmpty(nativeEnvelope);
        Assert.Equal(nativeEnvelope, TokenProbe.LodEnvelopes(imported));
        output.WriteLine($"{stem}: levels={before.LodLevels.Count}; radii={string.Join(",", before.LodLevels.Select(l => l.PixelRadius.ToString("R", CultureInfo.InvariantCulture)))}; " +
            $"tris(base;levels)={before.Meshes.Sum(m => m.TriangleCount)};{string.Join(",", before.LodLevels.Select(l => l.Meshes.Sum(m => m.TriangleCount)))}; " +
            $"verts={before.Meshes.Sum(m => m.VertexCount)};{string.Join(",", before.LodLevels.Select(l => l.Meshes.Sum(m => m.VertexCount)))}; bytes={native.Length}->{imported.Length}");
    }

    private static void AssertExact(MdlFile a, MdlFile b)
    {
        Assert.Empty(b.UnsupportedGeometryNodes);
        Meshes(a.Meshes, b.Meshes);
        Assert.Equal(a.LodLevels.Count, b.LodLevels.Count);
        foreach (var (x, y) in a.LodLevels.Zip(b.LodLevels))
        { Assert.Equal(Bits([x.PixelRadius]), Bits([y.PixelRadius])); Meshes(x.Meshes, y.Meshes); }
        Assert.True(MdlFileCompare.Run(a, b).pass);
    }
    private static void Meshes(IReadOnlyList<MdlMesh> a, IReadOnlyList<MdlMesh> b)
    {
        Assert.Equal(a.Count, b.Count);
        foreach (var (x, y) in a.Zip(b))
        {
            Assert.Equal(x.VertexCount, y.VertexCount); Assert.Equal(x.TriangleCount, y.TriangleCount);
            Assert.Equal(Bits(x.Positions), Bits(y.Positions)); Assert.Equal(Bits(x.TexCoords), Bits(y.TexCoords));
            Assert.Equal(Bits(x.Normals), Bits(y.Normals)); Assert.Equal(x.Indices, y.Indices);
            Assert.Equal(x.TextureStem, y.TextureStem);
        }
    }
    private static int[] Bits(float[] v) => v.Select(BitConverter.SingleToInt32Bits).ToArray();
    private static void AssertOptimized(MdlFile a, MdlFile b)
    {
        Assert.Equal(Triangles(a.Meshes), Triangles(b.Meshes));
        Assert.Equal(a.LodLevels.Count, b.LodLevels.Count);
        foreach (var (x, y) in a.LodLevels.Zip(b.LodLevels))
        { Assert.Equal(x.PixelRadius, y.PixelRadius); Assert.Equal(Triangles(x.Meshes), Triangles(y.Meshes)); }
    }
    private static string[] Triangles(IReadOnlyList<MdlMesh> meshes) => meshes.SelectMany(m => m.Indices.Chunk(3).Select(t =>
        m.TextureStem + ":" + string.Join(",", t.SelectMany(i => m.Positions.Skip(i*3).Take(3).Concat(m.TexCoords.Skip(i*2).Take(2)).Concat(m.Normals.Skip(i*3).Take(3))).Select(BitConverter.SingleToInt32Bits))))
        .Order(StringComparer.Ordinal).ToArray();
    private static MemoryStream Export(GlbExportModel model) { var s = new MemoryStream(); GlbWriter.Write(model, s); s.Position = 0; return s; }
    private static MemoryStream Rewrite(MemoryStream glb, Action<JsonObject> edit)
    {
        byte[] b = glb.ToArray(); int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
        var doc = JsonNode.Parse(b.AsSpan(20, n))!.AsObject(); edit(doc);
        byte[] json = Encoding.UTF8.GetBytes(doc.ToJsonString());
        using var temp = new MemoryStream(); using var w = new BinaryWriter(temp);
        int padded = (json.Length + 3) & ~3;
        w.Write(0x46546c67u); w.Write(2); w.Write(20 + padded + b.Length - 20 - n);
        w.Write(padded); w.Write(0x4e4f534au); w.Write(json);
        for (int i = json.Length; i < padded; i++) w.Write((byte)32);
        w.Write(b.AsSpan(20 + n)); return new MemoryStream(temp.ToArray());
    }
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static string Run(params string[] args)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        var info = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(MdlWriter).Assembly.Location); foreach (string arg in args) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!; var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000)); string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(p.ExitCode == 0, log); return log;
    }

    // Independent native token envelope reader, not the production MDL parser.
    // Native geometry.cpp:1937-1953; mdlbin.cpp:95-106,140-145.
    private sealed class TokenProbe(byte[] bytes)
    {
        private readonly BinaryReader r = new(new MemoryStream(bytes));
        private readonly List<string> imports = [];
        private readonly List<string> envelopes = [];
        private sealed record Node(string Kind, string? Detail = null, List<Node>? Args = null);
        public static string[] LodEnvelopes(byte[] bytes) { var p = new TokenProbe(bytes); p.Read(); return p.envelopes.ToArray(); }
        private string CString()
        {
            long start = r.BaseStream.Position; var b = new List<byte>(); byte c;
            while ((c = r.ReadByte()) != 0) b.Add(c);
            r.BaseStream.Position = start + ((b.Count + 4) & ~3); return Encoding.ASCII.GetString(b.ToArray());
        }
        private void Read()
        {
            Assert.Equal(0xDEBADF00u, r.ReadUInt32()); Assert.Equal(0x10000u, r.ReadUInt32());
            int ns = r.ReadInt32(), ni = r.ReadInt32(), ne = r.ReadInt32(), nd = r.ReadInt32();
            for (int i = 0; i < ns; i++) CString();
            for (int i = 0; i < ni; i++) { r.ReadUInt32(); imports.Add(CString()); }
            for (int i = 0; i < ne; i++) CString();
            for (int i = 0; i < ne + nd; i++) { r.ReadUInt32(); Object(); }
            Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
        }
        private Node Object()
        {
            var stack = new Stack<Node>();
            while (true)
            {
                uint token = r.ReadUInt32();
                switch (token)
                {
                    case 0: return Assert.Single(stack);
                    case 1: stack.Push(new("Float", r.ReadSingle().ToString("R", CultureInfo.InvariantCulture))); break;
                    case 5:
                        int count = r.ReadInt32(); var items = new List<Node>();
                        for (int i = 0; i < count; i++) items.Add(Object());
                        stack.Push(new("List", Args: items)); break;
                    case 9: stack.Push(new(imports[r.ReadInt32()])); break;
                    case 10:
                        var radius = stack.Pop(); var geo = stack.Pop();
                        Assert.Equal("Float", radius.Kind);
                        stack.Push(new("Pair", radius.Detail, [geo, radius])); break;
                    case 7:
                        string fn = stack.Pop().Kind; int n = r.ReadInt32();
                        if (fn == "MeshGeo") { int indices = r.ReadInt32(); r.BaseStream.Position += n*32L + indices*2L; }
                        else if (fn == "LightsGeo") r.BaseStream.Position += n*48L;
                        else if (fn == "FrameData") for (int i = 0; i < n; i++) { CString(); r.BaseStream.Position += 36; }
                        else throw new InvalidDataException("probe binary " + fn);
                        stack.Push(new(fn)); break;
                    case 6:
                        string f = stack.Pop().Kind;
                        if (f == "GroupGeo" || f == "ModifiableNumber") stack.Push(new(f, Args: [stack.Pop()]));
                        else if (f == "TextureGeo" || f == "LODGeo")
                        {
                            var geoNode = stack.Pop(); var first = stack.Pop();
                            if (f == "LODGeo")
                            {
                                Assert.Equal("List", first.Kind);
                                Assert.All(first.Args!, p => Assert.Equal("Pair", p.Kind));
                                envelopes.Add("List[" + string.Join(",", first.Args!.Select(p => "geo Float(" + p.Detail + ") Pair End")) + "] base Import(LODGeo) Apply");
                            }
                            stack.Push(new(f, Args: [first, geoNode]));
                        }
                        else throw new InvalidDataException("probe apply " + f);
                        break;
                    default: throw new InvalidDataException("probe token " + token);
                }
            }
        }
    }
}
