using System.IO;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P812 — the native-mesh ↔ glTF prototype (<c>tools/mdl-to-glb</c>): the
/// GLB container round-trips geometry/hardpoints/collision bit-exactly, the
/// hardpoint kind mapping follows the native frame-name conventions
/// (effect/efapp.cpp:562-583) and the cvh→mdl frame-space mapping is the one
/// verified numerically against real artwork in the P812 report.
/// </summary>
public class MdlToGlbTests
{
    // -- synthetic fixtures --------------------------------------------------

    private static GlbExportModel SyntheticModel()
    {
        var mesh = new GlbMesh(
            "hull_hullbmp_0", "hullbmp",
            [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, -2f],
            [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f],
            [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0.26726124f, 0.53452247f, 0.80178368f],
            [0, 1, 2, 0, 2, 3]);
        var mounts = new List<MountPoint>
        {
            new() { NodeName = "HP_Weapon_0", Kind = "Weapon", Index = 0, NativeName = "wepemt", Sources = ["cvh", "mdl"],
                Position = [0.5f, -0.25f, 2f], Forward = [0f, 0f, 1f] },
            new() { NodeName = "HP_MainEngine_0", Kind = "MainEngine", Index = 0, NativeName = "trail", Sources = ["mdl"],
                Position = [0f, 0f, -2f], Forward = [0f, 0f, -1f] },
            new() { NodeName = "HP_Turret_0", Kind = "Turret", Index = 0, NativeName = "turwepemt1", Sources = ["cvh"],
                Position = [0f, 0.8f, 1.3f], Forward = [0f, 1f, 0f] },
            new() { NodeName = "HP_Frame_0", Kind = "Frame", Index = 0, NativeName = "weird_frame", Sources = ["mdl"],
                Position = [1f, 2f, 3f], Forward = [0.57735026f, 0.57735026f, 0.57735026f] },  // unit diag
        };
        var lights = new List<GlbLight>
        {
            new("Light_0", [1f, 0.5f, 0.25f, 1f], [0f, 1f, 0f], 2f, 0.5f, 0.1f, 1f, 0.2f),
        };
        var collision = new CollisionSidecar
        {
            Radius = 4.0052886f,
            EllipseEquation = [1f, 2f, 3f],
            EllipseRadiusMultiplier = 5f,
            Hulls =
            [
                new SidecarHull([0f, 0f, 0f],
                [
                    new SidecarVertex([1f, 0f, 0f], [1, 2]),
                    new SidecarVertex([0f, 1f, 0f], [0, 2]),
                    new SidecarVertex([0f, 0f, 1f], [0, 1]),
                ]),
            ],
            Frames = [new SidecarFrame("wepemt", [0.5f, -0.25f, -2f], [0f, 0f, 1f])],
        };
        return new GlbExportModel
        {
            Name = "hull",
            Meshes = [mesh],
            Mounts = mounts,
            Lights = lights,
            LodPixelRadii = [64f, 32f, 16f],
            TextureStems = ["hullbmp"],
            Collision = collision,
        };
    }

    private static (GlbExportModel reimported, byte[] glb) WriteAndRead(GlbExportModel model)
    {
        using var ms = new MemoryStream();
        GlbWriter.Write(model, ms);
        var glb = ms.ToArray();

        var sidecar = SidecarDocument.Build(model, "hull.mdl", "hull.cvh");
        using var sideStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sidecar.ToJson()));
        using var glbStream = new MemoryStream(glb);
        return (GlbReader.Read(glbStream, sideStream), glb);
    }

    // -- container round trip --------------------------------------------------

    [Fact]
    public void Glb_roundtrip_preserves_geometry_hardpoints_and_collision()
    {
        var model = SyntheticModel();
        var (reimported, glb) = WriteAndRead(model);

        var (results, pass) = Compare.Run(model, reimported);
        Assert.True(pass, string.Join("; ", results.Where(r => !r.Pass).Select(r => $"{r.Name}: {r.Detail}")));

        // GLB structural invariants: magic + 4-byte-aligned chunks.
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        Assert.Equal(0, glb.Length % 4);
    }

    [Fact]
    public void Glb_roundtrip_keeps_native_index_order_and_float_bits()
    {
        var model = SyntheticModel();
        var (reimported, _) = WriteAndRead(model);

        var mesh = Assert.Single(reimported.Meshes);
        Assert.Equal(model.Meshes[0].Indices, mesh.Indices);          // raw native winding, unmodified
        Assert.Equal(model.Meshes[0].Positions, mesh.Positions);      // float32: bit-exact
        Assert.Equal(model.Meshes[0].TexCoords, mesh.TexCoords);
        Assert.Equal(model.Meshes[0].Normals, mesh.Normals);
        Assert.Equal("hullbmp", mesh.TextureStem);
    }

    [Fact]
    public void Glb_mount_nodes_carry_position_and_z_forward()
    {
        var model = SyntheticModel();
        var (reimported, _) = WriteAndRead(model);

        var engine = reimported.Mounts.Single(m => m.NodeName == "HP_MainEngine_0");
        Assert.Equal(model.Mounts[1].Position, engine.Position);
        // +Z-forward convention survives a 180° nozzle (0,0,-1) and a side mount (0,1,0).
        Assert.Equal(model.Mounts[1].Forward, engine.Forward);
        var turret = reimported.Mounts.Single(m => m.NodeName == "HP_Turret_0");
        Within(model.Mounts[2].Forward, turret.Forward);
        var diag = reimported.Mounts.Single(m => m.NodeName == "HP_Frame_0");
        Within(model.Mounts[3].Forward, diag.Forward);
    }

    private static void Within(float[] expected, float[] actual, float eps = 1e-5f)
    {
        for (int i = 0; i < expected.Length; i++)
            Assert.True(MathF.Abs(expected[i] - actual[i]) <= eps, $"[{i}] {expected[i]} != {actual[i]}");
    }

    // -- hardpoint kind mapping ------------------------------------------------

    [Theory]
    [InlineData("turwepemt1", "Turret")]
    [InlineData("rwepemt", "Weapon")]
    [InlineData("missemt", "Weapon")]
    [InlineData("wepmnt", "Weapon")]
    [InlineData("trail", "MainEngine")]
    [InlineData("rthrust", "MainEngine")]
    [InlineData("lsmoke", "Thruster")]
    [InlineData("rocket2", "Thruster")]
    [InlineData("cockpt", "Cockpit")]
    [InlineData("chaff", "Chaff")]
    [InlineData("garage1B", "DockingEntrance")]
    [InlineData("launch2", "DockingExit")]
    [InlineData("something_else", "Frame")]
    public void Mount_kind_follows_native_frame_name_substring_rules(string nativeName, string expected)
    {
        Assert.Equal(expected, MountKindMapper.KindFor(nativeName));
    }

    [Fact]
    public void Turret_prefix_wins_over_weapon_substring()
    {
        // turwepemt* contains "wepemt" but is a turret mount — the native files
        // use the "tur" prefix for turreted emitters (ss27/bom04).
        Assert.Equal("Turret", MountKindMapper.KindFor("turwepemt2"));
    }

    // -- cvh → mdl frame-space mapping ------------------------------------------

    private static (MdlFile mdl, CvhFile cvh) FrameSpaceFixture() => FrameSpaceFixture([]);

    private static (MdlFile mdl, CvhFile cvh) FrameSpaceFixture(IReadOnlyList<MdlFrame> extraMdlFrames)
    {
        // One frame authored in BOTH files: the .cvh stores it at (1,2,3) with
        // forward (0,0,1); the .mdl's FrameData stores the same frame at its
        // mdl-space position (1,2,−3) with forward (0,0,−1) — the pure mirror-z
        // mapping (x,y,z) → (x,y,−z) applies to position AND forward (verified
        // on real artwork, see the P812 report).
        var mdlFrames = new List<MdlFrame>
        {
            new() { Name = "wepemt", Position = [1f, 2f, -3f], Forward = [0f, 0f, -1f], Up = [0f, 1f, 0f] },
        };
        mdlFrames.AddRange(extraMdlFrames);
        var mdl = new MdlFile
        {
            Namespaces = [], Imports = [], Exports = ["object"],
            Meshes = [],
            Frames = mdlFrames,
            LodLevels = [], Lights = [], UnsupportedGeometryNodes = [],
        };
        var cvh = new CvhFile
        {
            Radius = 4f,
            EllipseEquation = new Vector3(1f, 1f, 1f),
            EllipseRadiusMultiplier = 4f,
            Hulls = [],
            Frames = [new CvhFrame("wepemt", new Vector3(1f, 2f, 3f), new Vector3(0f, 0f, 1f))],
        };
        return (mdl, cvh);
    }

    [Fact]
    public void Cvh_frames_map_into_mdl_space_and_cvh_wins_as_the_sim_authoritative_source()
    {
        var (mdl, cvh) = FrameSpaceFixture();
        var mounts = NativeModelAdapter.BuildMounts(mdl, cvh);

        var mount = Assert.Single(mounts);
        Assert.Equal("wepemt", mount.NativeName);
        Assert.Equal([1f, 2f, -3f], mount.Position!);           // (x, y, −z) of the cvh file value
        Assert.Equal([0f, 0f, -1f], mount.Forward!);            // (x, y, −z) of the cvh file value — same mirror
        Assert.Equal(["cvh", "mdl"], mount.Sources);           // cvh listed first: sim-authoritative
        Assert.Equal([1f, 2f, -3f], mount.MdlPosition!);        // the .mdl FrameData evidence, kept not merged
        Assert.Equal([0f, 0f, -1f], mount.MdlForward!);
    }

    [Fact]
    public void Mdl_only_frames_fill_mounts_the_cvh_lacks()
    {
        var (mdl, cvh) = FrameSpaceFixture(
            [new MdlFrame { Name = "trail", Position = [0f, 0f, -4f], Forward = [0f, 0f, -1f], Up = [-1f, 0f, 0f] }]);

        var mounts = NativeModelAdapter.BuildMounts(mdl, cvh);
        Assert.Equal(2, mounts.Count);
        var trail = mounts.Single(m => m.NativeName == "trail");
        Assert.Equal(["mdl"], trail.Sources);
        Assert.Equal([0f, 0f, -4f], trail.Position!);
        Assert.Equal("MainEngine", trail.Kind);
        Assert.Equal("HP_MainEngine_0", trail.NodeName);
    }

    [Fact]
    public void Mounts_are_numbered_per_kind_in_encounter_order()
    {
        var (_, cvh) = FrameSpaceFixture();
        var cvhFrames = new List<CvhFrame>(cvh.Frames)
        {
            new("garage1A", new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f)),
            new("garage1B", new Vector3(2f, 0f, 0f), new Vector3(0f, 0f, 1f)),
            new("launch1", new Vector3(3f, 0f, 0f), new Vector3(0f, 0f, 1f)),
            new("garage2A", new Vector3(4f, 0f, 0f), new Vector3(0f, 0f, 1f)),
        };
        cvh = new CvhFile
        {
            Radius = cvh.Radius,
            EllipseEquation = cvh.EllipseEquation,
            EllipseRadiusMultiplier = cvh.EllipseRadiusMultiplier,
            Hulls = cvh.Hulls,
            Frames = cvhFrames,
        };

        var mounts = NativeModelAdapter.BuildMounts(new MdlFile
        {
            Namespaces = [], Imports = [], Exports = [], Meshes = [], Frames = [], LodLevels = [], Lights = [],
            UnsupportedGeometryNodes = [],
        }, cvh);

        Assert.Equal("HP_DockingEntrance_0", mounts.Single(m => m.NativeName == "garage1A").NodeName);
        Assert.Equal("HP_DockingEntrance_1", mounts.Single(m => m.NativeName == "garage1B").NodeName);
        Assert.Equal("HP_DockingExit_0", mounts.Single(m => m.NativeName == "launch1").NodeName);
        Assert.Equal("HP_DockingEntrance_2", mounts.Single(m => m.NativeName == "garage2A").NodeName);
    }

    // -- sidecar ---------------------------------------------------------------

    [Fact]
    public void Sidecar_json_round_trips_the_collision_record()
    {
        var sidecar = SidecarDocument.Build(SyntheticModel(), "hull.mdl", "hull.cvh");
        var parsed = SidecarDocument.FromJson(sidecar.ToJson());

        Assert.Equal(SidecarDocument.FormatId, parsed.Format);
        var a = sidecar.Collision!;
        var b = parsed.Collision!;
        Assert.Equal(a.Radius, b.Radius);
        Assert.Equal(a.EllipseEquation, b.EllipseEquation);
        Assert.Equal(a.EllipseRadiusMultiplier, b.EllipseRadiusMultiplier);
        Assert.Equal(a.Hulls.Count, b.Hulls.Count);
        Assert.Equal(a.Hulls[0].Vertices.Count, b.Hulls[0].Vertices.Count);
        Assert.Equal(a.Hulls[0].Vertices[0].Adj, b.Hulls[0].Vertices[0].Adj);
        Assert.Equal(a.Hulls[0].Vertices[2].P, b.Hulls[0].Vertices[2].P);
        Assert.Equal(a.Frames.Count, b.Frames.Count);
        Assert.Equal(a.Frames[0].Name, b.Frames[0].Name);
        Assert.Equal(sidecar.Mounts.Count, parsed.Mounts.Count);
        Assert.Equal([64f, 32f, 16f], parsed.LodPixelRadii);
    }

    [Fact]
    public void Sidecar_records_the_native_cvh_load_flip_note()
    {
        var sidecar = SidecarDocument.Build(SyntheticModel(), "hull.mdl", "hull.cvh");
        Assert.Contains("HitTest.cpp", CollisionSidecar.NativeLoadFlipNote);
        Assert.Contains("clockwise-front", sidecar.IndexWinding);
        Assert.Contains("+Z nose", sidecar.ModelSpace);
    }

    // -- real-artwork round trips (fig30 fighter / bom04 bomber / ss27 garrison) --

    public static TheoryData<string> ReportHulls => new() { "fig30", "bom04", "ss27" };

    [SkippableTheory]
    [MemberData(nameof(ReportHulls))]
    [Trait("Requires", "Artwork")]
    public void Cvh_to_mdl_mirror_z_mapping_matches_the_real_framedata(string stem)
    {
        // The export's cvh→mdl frame mapping (x,y,z) → (x,y,−z) on position AND
        // forward must reproduce the .mdl's own FrameData for every frame the
        // two files share (the report's Table: the P812 space finding).
        Require.Artwork();
        var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(artRoot, stem + ".mdl")));
        var cvh = CvhReader.Parse(File.ReadAllText(Path.Combine(artRoot, stem + ".cvh")));

        var mounts = NativeModelAdapter.BuildMounts(mdl, cvh);
        var shared = mounts.Where(m => m.Sources.Contains("cvh") && m.MdlPosition is not null).ToList();
        Assert.NotEmpty(shared);
        foreach (var m in shared)
        {
            Assert.True(Delta(m.Position, m.MdlPosition!) < 1e-4,
                $"{stem} {m.NativeName} position: {Fmt(m.Position)} vs mdl {Fmt(m.MdlPosition!)}");
            Assert.True(UnitDelta(m.Forward, m.MdlForward!) < 0.01,
                $"{stem} {m.NativeName} forward: {Fmt(m.Forward)} vs mdl {Fmt(m.MdlForward!)}");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ReportHulls))]
    [Trait("Requires", "Artwork")]
    public void Native_hull_roundtrips_through_glb(string stem)
    {
        Require.Artwork();
        var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

        var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(artRoot, stem + ".mdl")));
        var cvh = CvhReader.Parse(File.ReadAllText(Path.Combine(artRoot, stem + ".cvh")));
        var model = NativeModelAdapter.FromNative(stem, mdl, cvh);

        using var ms = new MemoryStream();
        GlbWriter.Write(model, ms);
        ms.Position = 0;
        var sidecar = SidecarDocument.Build(model, Path.Combine(artRoot, stem + ".mdl"), Path.Combine(artRoot, stem + ".cvh"));
        using var sideStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sidecar.ToJson()));
        var reimported = GlbReader.Read(ms, sideStream);

        var (results, pass) = Compare.Run(model, reimported);
        Assert.True(pass, string.Join("; ", results.Where(r => !r.Pass).Select(r => $"{r.Name}: {r.Detail}")));

        // Inventory invariants the report quotes.
        Assert.Equal(mdl.Meshes.Count, reimported.Meshes.Count);
        Assert.Equal(mdl.Meshes.Sum(m => m.TriangleCount), reimported.Meshes.Sum(m => m.Indices.Length / 3));
        Assert.Equal(mdl.Frames.Count + cvh.Frames.Count
                     - mdl.Frames.Count(f => cvh.Frames.Any(c => c.Name.Equals(f.Name, StringComparison.Ordinal))),
                     reimported.Mounts.Count);
        Assert.Equal(cvh.Hulls.Sum(h => h.VertexCount),
                     reimported.Collision!.Hulls.Sum(h => h.Vertices.Count));
        Assert.All(reimported.Mounts, m => Assert.StartsWith("HP_", m.NodeName));
    }

    private static double Delta(float[] a, float[] b)
    {
        double worst = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    private static double UnitDelta(float[] a, float[] b)
    {
        static float[] Unit(float[] v)
        {
            float len = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return len < 1e-8f ? [0f, 0f, 0f] : [v[0] / len, v[1] / len, v[2] / len];
        }
        var ua = Unit(a);
        var ub = Unit(b);
        return Delta(ua, ub);
    }

    private static string Fmt(float[] v) => $"({v[0]:0.000},{v[1]:0.000},{v[2]:0.000})";
}
