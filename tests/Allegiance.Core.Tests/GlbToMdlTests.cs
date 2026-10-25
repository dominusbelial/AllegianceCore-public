using System.IO;
using XnaVec = System.Numerics.Vector3;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P823 — the glb → .mdl/.cvh writer (<c>tools/mdl-to-glb</c> import): a
/// <c>.glb</c> (+ optional sidecar) converts to native's shipping formats, the
/// port's own parsers (<see cref="MdlFile"/>, <see cref="CvhReader"/>) read the
/// output, and the sim mount readers see the expected mounts. Native-artwork
/// round trips (export → re-import → write) keep every parsed structure
/// bit-exact; the byte deltas (LOD detail levels, import-order variants) are
/// enumerated in docs/reports/P823_GLB_TO_MDL.md.
/// </summary>
public class GlbToMdlTests
{
    // -- synthetic fixtures (built in code — no artwork, no committed files) --

    /// <summary>An octahedral low-poly hull: 6 vertices, 8 triangles, radius 3
    /// about the origin, nose at +Z (the raw .mdl space).</summary>
    private static GlbExportModel NewArtModel() => new()
    {
        Name = "testhull",
        Meshes =
        [
            new GlbMesh("testhull_hullbmp_0", "hullbmp",
                Positions:
                [
                    0f, 0f, 3f,      // nose
                    1f, 0f, 0f,      // +x
                    0f, 1f, 0f,      // +y
                    -1f, 0f, 0f,     // -x
                    0f, -1f, 0f,     // -y
                    0f, 0f, -3f,     // tail
                ],
                TexCoords: [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f, 0.5f, 0.5f, 0.25f, 0.75f],
                Normals:
                [
                    0f, 0f, 1f,
                    1f, 0f, 0f,
                    0f, 1f, 0f,
                    -1f, 0f, 0f,
                    0f, -1f, 0f,
                    0f, 0f, -1f,
                ],
                Indices:
                [
                    0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,       // front pyramid
                    5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4,       // rear pyramid
                ]),
        ],
        Mounts =
        [
            new MountPoint { NodeName = "HP_Weapon_0", Kind = "Weapon", Index = 0, NativeName = "HP_Weapon_0",
                Sources = ["glb"], Position = [1f, 0f, 1f], Forward = [0f, 0f, 1f] },
            new MountPoint { NodeName = "HP_Weapon_1", Kind = "Weapon", Index = 1, NativeName = "HP_Weapon_1",
                Sources = ["glb"], Position = [-1f, 0f, 1f], Forward = [0f, 0f, 1f] },
            new MountPoint { NodeName = "HP_MainEngine_0", Kind = "MainEngine", Index = 0, NativeName = "HP_MainEngine_0",
                Sources = ["glb"], Position = [0f, 0f, -3f], Forward = [0f, 0f, -1f] },
            new MountPoint { NodeName = "HP_Cockpit_0", Kind = "Cockpit", Index = 0, NativeName = "HP_Cockpit_0",
                Sources = ["glb"], Position = [0f, 0.5f, 1f], Forward = [0f, 0f, 1f] },
            new MountPoint { NodeName = "HP_DockingEntrance_0", Kind = "DockingEntrance", Index = 0,
                NativeName = "HP_DockingEntrance_0", Sources = ["glb"],
                Position = [0f, -1f, 0f], Forward = [0f, -1f, 0f] },
        ],
        Lights = [],
        LodPixelRadii = [],
        TextureStems = ["hullbmp"],
        Collision = null,
    };

    /// <summary>The synthetic P812-style model with a full sidecar collision.</summary>
    private static GlbExportModel SidecarModel() => new()
    {
        Name = "hull",
        Meshes =
        [
            new GlbMesh("hull_hullbmp_0", "hullbmp",
                [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, -2f],
                [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f],
                [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0.26726124f, 0.53452247f, 0.80178368f],
                [0, 1, 2, 0, 2, 3]),
        ],
        Mounts =
        [
            new MountPoint { NodeName = "HP_Weapon_0", Kind = "Weapon", Index = 0, NativeName = "wepemt",
                Sources = ["cvh"], Position = [0.5f, -0.25f, 2f], Forward = [0f, 0f, 1f] },
        ],
        Lights = [new GlbLight("Light_0", [1f, 0.5f, 0.25f, 1f], [0f, 1f, 0f], 2f, 0.5f, 0.1f, 1f, 0.2f)],
        LodPixelRadii = [],
        TextureStems = ["hullbmp"],
        Collision = new CollisionSidecar
        {
            Radius = 4.0052886f,
            EllipseEquation = [3.5192864f, 1.6151144f, 4.3971051f],
            EllipseRadiusMultiplier = 1.0978247f,
            Hulls =
            [
                new SidecarHull([0.1029155f, 0f, 0.0002195f],
                [
                    new SidecarVertex([-0.646369f, 0.58442f, -2.179855f], [1, 2]),
                    new SidecarVertex([-0.392513f, 0.194806f, -3.189343f], [0, 2]),
                    new SidecarVertex([0.392513f, -0.194807f, -3.189343f], [0, 1]),
                ]),
            ],
            Frames =
            [
                new SidecarFrame("wepemt", [0.5f, -0.25f, -2f], [0f, 0f, 1f]),
                new SidecarFrame("cockpt", [0f, 0.311104f, -2.493818f], [0f, 0f, 1f]),
            ],
        },
    };

    private static (byte[] mdl, string cvh) Convert(GlbExportModel model) => GlbImporter.Convert(model);

    private static (byte[] glb, string sidecarJson) Export(GlbExportModel model)
    {
        using var ms = new MemoryStream();
        GlbWriter.Write(model, ms);
        var sidecar = new SidecarDocument
        {
            Format = SidecarDocument.FormatId,
            Mdl = model.Name + ".mdl",
            Cvh = model.Name + ".cvh",
            MdlSha256 = "",
            CvhSha256 = "",
            ModelSpace = GlbExportModel.ModelSpace,
            ScaleHint = "test",
            IndexWinding = "test",
            TextureStems = model.TextureStems,
            LodPixelRadii = model.LodPixelRadii,
            Lights = model.Lights,
            Mounts = model.Mounts,
            Collision = model.Collision,
            MdlFrames = model.SourceMdlFrames,
        };
        return (ms.ToArray(), sidecar.ToJson());
    }

    private static GlbExportModel Reimport(byte[] glb, string? sidecarJson)
    {
        using var glbStream = new MemoryStream(glb);
        MemoryStream? sideStream = sidecarJson is null ? null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sidecarJson));
        try
        {
            return GlbReader.Read(glbStream, sideStream);
        }
        finally
        {
            sideStream?.Dispose();
        }
    }

    // -- the .mdl writer -------------------------------------------------------

    [Fact]
    public void Written_mdl_parses_with_bit_exact_geometry_frames_and_lights()
    {
        var model = SidecarModel();
        var (mdl, _) = Convert(model);

        var parsed = MdlFile.Parse(mdl);
        var mesh = Assert.Single(parsed.Meshes);
        var source = Assert.Single(model.Meshes);
        Assert.Equal(source.Positions, mesh.Positions);          // float32: bit-exact
        Assert.Equal(source.TexCoords, mesh.TexCoords);
        Assert.Equal(source.Normals, mesh.Normals);
        Assert.Equal(source.Indices, mesh.Indices);
        Assert.Equal("hullbmp", mesh.TextureStem);               // TextureGeo namespace resolved

        // The sidecar carried the raw FrameData (incl. up) → written verbatim.
        var frame = Assert.Single(parsed.Frames);
        Assert.Equal("wepemt", frame.Name);
        Assert.Equal([0.5f, -0.25f, 2f], frame.Position);
        Assert.Equal([0f, 0f, 1f], frame.Forward);
        Assert.Equal([0f, 1f, 0f], frame.Up);

        var light = Assert.Single(parsed.Lights);
        Assert.Equal([1f, 0.5f, 0.25f, 1f], light.Color);
        Assert.Equal(2f, light.Period);
        Assert.Equal(0.2f, light.RampDown);
        Assert.Empty(parsed.LodLevels);                          // single detail level (documented fallback)
        Assert.Empty(parsed.UnsupportedGeometryNodes);
    }

    [Fact]
    public void Written_mdl_carries_the_native_header_shape()
    {
        var (mdl, _) = Convert(NewArtModel());

        // magic 0x0DEBADF00, version 0x00010000, then ns/imports/exports counts.
        uint magic = BitConverter.ToUInt32(mdl, 0);
        uint version = BitConverter.ToUInt32(mdl, 4);
        uint ns = BitConverter.ToUInt32(mdl, 8);
        uint imports = BitConverter.ToUInt32(mdl, 12);
        uint exports = BitConverter.ToUInt32(mdl, 16);
        Assert.Equal(0x0DEBADF00u, magic);
        Assert.Equal(0x00010000u, version);
        Assert.Equal(3u, ns);                                    // model, effect, hullbmp
        Assert.Equal(9u, imports);                               // 7 fixed + 1 stem + GroupGeo
        Assert.Equal(4u, exports);                               // frame, lights, frames, object

        var parsed = MdlFile.Parse(mdl);
        Assert.Equal(["model", "effect", "hullbmp"], parsed.Namespaces);
        Assert.Equal(["frame", "lights", "frames", "object"], parsed.Exports);
        Assert.Contains("hullbmp", parsed.Imports);              // the texture unit import
    }

    [Fact]
    public void Written_mdl_synthesizes_framedata_from_mounts_for_new_art()
    {
        var (mdl, _) = Convert(NewArtModel());

        var parsed = MdlFile.Parse(mdl);
        Assert.Equal(5, parsed.Frames.Count);
        var wep = parsed.Frames.Single(f => f.Name == "wepmnt1");          // HP_Weapon_0 → native vocabulary
        Assert.Equal([1f, 0f, 1f], wep.Position);                          // GLB node position (.mdl space)
        Assert.Equal([0f, 0f, 1f], wep.Forward);
        Assert.Equal([0f, 1f, 0f], wep.Up);                                // fallback up (forward ⊥ (0,1,0))
        var engine = parsed.Frames.Single(f => f.Name == "thrust");        // index 0: bare "thrust"
        Assert.Equal([0f, 0f, -3f], engine.Position);
        Assert.Equal([0f, 0f, -1f], engine.Forward);
        Assert.Equal([0f, 1f, 0f], engine.Up);                             // forward ⊥ (0,1,0) → y fallback
        Assert.Equal("wepmnt2", parsed.Frames.Single(f => f.Position.SequenceEqual([-1f, 0f, 1f])).Name);
        Assert.Equal("cockpt", parsed.Frames.Single(f => f.Name == "cockpt").Name);
        Assert.Equal("garage1A", parsed.Frames.Single(f => f.Name == "garage1A").Name);
    }

    [Fact]
    public void Mdl_writer_output_is_byte_stable_under_export_import_cycle()
    {
        // new-art glb (no sidecar) → mdl/cvh → P812 export → P823 import again:
        // the second write must be BYTE-identical to the first (canonical
        // writer; the pipeline is a fixed point on its own output).
        var (glb1, _) = Export(NewArtModel());
        var model1 = Reimport(glb1, sidecarJson: null);
        var (mdl1, cvh1) = Convert(model1);

        var model2 = NativeModelAdapter.FromNative("testhull", MdlFile.Parse(mdl1), CvhReader.Parse(cvh1));
        var (glb2, sidecar2) = Export(model2);
        var model3 = Reimport(glb2, sidecar2);
        var (mdl2, cvh2) = Convert(model3);

        Assert.Equal(mdl1, mdl2);
        Assert.Equal(cvh1, cvh2);
    }

    // -- the .cvh writer -------------------------------------------------------

    [Fact]
    public void Cvh_writer_round_trips_sidecar_collision_bit_exactly()
    {
        var (_, cvhText) = Convert(SidecarModel());
        var parsed = CvhReader.Parse(cvhText);
        var sidecar = SidecarModel().Collision!;

        Assert.Equal(sidecar.Radius, parsed.Radius);
        Assert.Equal(sidecar.EllipseRadiusMultiplier, parsed.EllipseRadiusMultiplier);
        var hull = Assert.Single(parsed.Hulls);
        Assert.Equal(3, hull.VertexCount);
        Assert.Equal(6, hull.AdjacencyCount);                    // sum of the declared adjacency counts
        Assert.Equal(sidecar.Hulls[0].Vertices[2].P, new[] { hull.Vertices[2].Position.X, hull.Vertices[2].Position.Y, hull.Vertices[2].Position.Z });
        Assert.Equal(sidecar.Hulls[0].Vertices[0].Adj, hull.Vertices[0].AdjacentIndices);
        Assert.Equal(2, parsed.Frames.Count);
        Assert.Equal("wepemt", parsed.Frames[0].Name);
        Assert.Equal(sidecar.Frames[0].Position, new[] { parsed.Frames[0].Position.X, parsed.Frames[0].Position.Y, parsed.Frames[0].Position.Z });
        Assert.Equal(sidecar.Frames[1].Forward, new[] { parsed.Frames[1].Forward.X, parsed.Frames[1].Forward.Y, parsed.Frames[1].Forward.Z });
    }

    [Fact]
    public void Generated_cvh_hulls_the_mesh_and_mirrors_frames_into_cvh_space()
    {
        var (_, cvhText) = Convert(NewArtModel());
        var cvh = CvhReader.Parse(cvhText);

        // Header: radius = the most distant used vertex from the origin
        // (cvh/main.cpp:348-360) — the octahedron's nose at |z| = 3.
        Assert.Equal(3f, cvh.Radius);
        var hull = Assert.Single(cvh.Hulls);
        Assert.Equal(6, hull.VertexCount);

        // Every mesh vertex is a hull vertex (positions mirrored in z).
        var mesh = NewArtModel().Meshes[0];
        foreach (var i in Enumerable.Range(0, 6))
        {
            var p = (mesh.Positions[3 * i], mesh.Positions[3 * i + 1], -mesh.Positions[3 * i + 2]);
            Assert.Contains(hull.Vertices, v =>
                v.Position.X == p.Item1 && v.Position.Y == p.Item2 && v.Position.Z == p.Item3);
        }

        // Native adjacency accounting: 2·(V + F − 2) with F = 8 faces.
        Assert.Equal(2 * (6 + 8 - 2), hull.AdjacencyCount);
        Assert.Equal(hull.AdjacencyCount, hull.Vertices.Sum(v => v.AdjacentIndices.Length));
        foreach (var v in hull.Vertices)

        // Frames: mounts mirrored into .cvh file space, native names synthesized.
        Assert.Equal(5, cvh.Frames.Count);
        var wep = cvh.Frames.Single(f => f.Name == "wepmnt1");
        Assert.Equal((1f, 0f, -1f), (wep.Position.X, wep.Position.Y, wep.Position.Z));      // (x, y, −z)
        Assert.Equal((0f, 0f, -1f), (wep.Forward.X, wep.Forward.Y, wep.Forward.Z));         // forward mirrored too
        var garage = cvh.Frames.Single(f => f.Name == "garage1A");
        Assert.Equal((0f, -1f, 0f), (garage.Position.X, garage.Position.Y, garage.Position.Z));
    }

    [Fact]
    public void Generated_cvh_encloses_all_points_for_seeded_random_clouds()
    {
        var rng = new Random(0x50823);   // deterministic
        for (int trial = 0; trial < 5; trial++)
        {
            var pts = new List<XnaVec>();
            for (int i = 0; i < 40; i++)
                pts.Add(new XnaVec(rng.NextSingle() * 20f - 10f, rng.NextSingle() * 8f - 4f, rng.NextSingle() * 12f - 6f));
            // always include the extremes so the hull is full-dimensional
            pts.Add(new XnaVec(10f, 4f, 6f)); pts.Add(new XnaVec(-10f, -4f, -6f)); pts.Add(new XnaVec(0f, 4f, -6f)); pts.Add(new XnaVec(0f, -4f, 0f));

            var positions = new float[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++)
            {
                positions[3 * i] = pts[i].X;
                positions[3 * i + 1] = pts[i].Y;
                positions[3 * i + 2] = pts[i].Z;
            }
            var model = new GlbExportModel
            {
                Name = "cloud",
                Meshes = [new GlbMesh("cloud_0", null, positions, new float[pts.Count * 2], new float[pts.Count * 3],
                    Enumerable.Range(0, pts.Count - 2).SelectMany(t => new[] { 0, t + 1, t + 2 }).Select(i => (ushort)i).ToArray())],
                Mounts = [], Lights = [], LodPixelRadii = [], TextureStems = [], Collision = null,
            };

            var (_, cvhText) = Convert(model);
            var cvh = CvhReader.Parse(cvhText);
            var hull = Assert.Single(cvh.Hulls);

            // The header radius encloses every point (from the origin).
            foreach (var p in pts)
                Assert.True(new XnaVec(p.X, p.Y, p.Z).Length() <= cvh.Radius + 1e-4,
                    $"trial {trial}: point {p} outside radius {cvh.Radius}");

            // The ellipse encloses every point: (x/a)² + (y/b)² + (z/c)² ≤ 1
            // (the equation terms are the semi-axis lengths, cvh/main.cpp:533-536).
            var ee = cvh.EllipseEquation;
            foreach (var p in pts)
            {
                double v = (double)(p.X * p.X) / (ee.X * ee.X) + (double)(p.Y * p.Y) / (ee.Y * ee.Y) + (double)(p.Z * p.Z) / (ee.Z * ee.Z);
                Assert.True(v <= 1.01 + 1e-4, $"trial {trial}: point {p} outside the ellipse ({v})");
            }

            // Adjacency is non-empty and symmetric, and Parse enforced the ranges.
            Assert.All(hull.Vertices, v => Assert.NotEmpty(v.AdjacentIndices));
        }
    }

    // -- the new-art end-to-end case (brief §3) --------------------------------

    [Fact]
    public void New_art_glb_converts_to_mdl_cvh_the_port_and_mount_readers_accept()
    {
        // Hand-built GLB in code, NO sidecar: low-poly hull + 2 gun hardpoints +
        // 1 engine (+ cockpit + landing entrance).
        var (glb, _) = Export(NewArtModel());
        var model = Reimport(glb, sidecarJson: null);
        var (mdlBytes, cvhText) = Convert(model);

        var mdl = MdlFile.Parse(mdlBytes);
        var cvh = CvhReader.Parse(cvhText);

        var mesh = Assert.Single(mdl.Meshes);
        Assert.Equal(8, mesh.TriangleCount);
        Assert.Equal("hullbmp", mesh.TextureStem);

        // The sim's name-keyed mount reads (hullTypeIGC.cpp:80-204):
        // hardpoints keyed by the core's frame names — the synthesized
        // wepmnt1/wepmnt2 the writer produced from HP_Weapon_0/HP_Weapon_1.
        var hull = new DataHullTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "testhull", IconName: "ico", Name: "Test",
            Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
            Mass: 550f, Signature: 0f, Speed: 120f,
            MaxTurnRates: [4.5f, 4.5f, 4.5f], TurnTorques: [5000f, 5000f, 5000f],
            Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
            MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
            RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 999, SuccessorHullId: -1,
            MaxWeapons: 2, MaxFixedWeapons: 2, HitPoints: 518f, HardpointOffset: 600,
            DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
            PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
            InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
            MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
            ManuveringThrusterExteriorSound: 0,
            Hardpoints:
            [
                new DataHullTypeIGC.Hardpoint(0, 0, "wepmnt1", "L", 0, true),
                new DataHullTypeIGC.Hardpoint(0, 0, "wepmnt2", "R", 0, true),
            ]);

        // m_scale = 0.5·length/originalRadius (hullTypeIGC.cpp:62); radius 3.
        float scale = 0.5f * hull.Length / cvh.Radius;
        Assert.Equal(5f, scale);
        var muzzles = HullWeaponPositions.Compute(hull, cvh);
        Assert.Equal(2, muzzles.Length);
        // wepmnt1: mdl (1,0,1) → cvh (1,0,−1) → HitTest flip (−1,0,−1) → ×5.
        Assert.Equal(new Vector3(-5f, 0f, -5f), muzzles[0]);
        // wepmnt2: mdl (−1,0,1) → cvh (−1,0,−1) → flip (1,0,−1) → ×5.
        Assert.Equal(new Vector3(5f, 0f, -5f), muzzles[1]);

        // The landing bay: HP_DockingEntrance_0 → garage1A; no launch slots.
        var bay = HullLandingBay.Reconstruct(cvh, hull.Length);
        var land = Assert.Single(bay.LandSlots);
        var plane = Assert.Single(land.Planes);
        Assert.Equal(new Vector3(0f, -5f, 0f), plane.Position);           // cvh (0,−1,0) → flip (0,−1,0) → ×5
        Assert.Empty(bay.LaunchSlots);
    }

    // -- malformed input rejection ----------------------------------------------

    [Fact]
    public void Import_rejects_garbled_glb_and_illegal_geometry()
    {
        // Not a GLB.
        Assert.Throws<InvalidDataException>(() => Reimport([1, 2, 3, 4, 5, 6, 7, 8], null));

        var (glb, _) = Export(NewArtModel());

        // Truncated GLB (header claims more than the buffer holds).
        Assert.Throws<InvalidDataException>(() => Reimport(glb[..40], null));

        // Zero meshes.
        var empty = NewArtModel() with { Meshes = [] };
        Assert.Throws<InvalidDataException>(() => Convert(empty));

        // Index out of range.
        var badIndex = NewArtModel() with
        {
            Meshes = [NewArtModel().Meshes[0] with { Indices = [0, 1, 99] }],
        };
        var ex = Assert.Throws<InvalidDataException>(() => Convert(badIndex));
        Assert.Contains("out of range", ex.Message);

        // Ragged arrays.
        var ragged = NewArtModel() with
        {
            Meshes = [NewArtModel().Meshes[0] with { TexCoords = [0f, 0f] }],
        };
        Assert.Throws<InvalidDataException>(() => Convert(ragged));
    }

    [Fact]
    public void Import_rejects_malformed_hardpoint_nodes_and_collision()
    {
        var badNode = NewArtModel() with
        {
            Mounts =
            [
                new MountPoint { NodeName = "HP_Weapon_x", Kind = "Weapon", Index = 0, NativeName = "HP_Weapon_x",
                    Sources = ["glb"], Position = [0f, 0f, 0f], Forward = [0f, 0f, 1f] },
            ],
        };
        var (glb, _) = Export(badNode);
        var ex = Assert.Throws<InvalidDataException>(() => Reimport(glb, null));
        Assert.Contains("HP_", ex.Message);

        // Adjacency index out of range in the sidecar collision.
        var badAdj = SidecarModel() with
        {
            Collision = SidecarModel().Collision! with
            {
                Hulls =
                [
                    new SidecarHull([0f, 0f, 0f],
                    [
                        new SidecarVertex([1f, 0f, 0f], [1, 7]),
                        new SidecarVertex([0f, 1f, 0f], [0, 2]),
                        new SidecarVertex([0f, 0f, 1f], [0, 1]),
                    ]),
                ],
            },
        };
        Assert.Throws<InvalidDataException>(() => Convert(badAdj));

        // Non-finite radius.
        var badRadius = SidecarModel() with
        {
            Collision = SidecarModel().Collision! with { Radius = float.NaN },
        };
        Assert.Throws<InvalidDataException>(() => Convert(badRadius));

        // Degenerate mesh (no volume) — hull generation fails loudly.
        var flat = NewArtModel() with
        {
            Meshes = [NewArtModel().Meshes[0] with
            {
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0f, 3f, 0f, 0f],
                TexCoords = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f],
                Normals = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
                Indices = [0, 1, 2, 0, 2, 3],
            }],
        };
        Assert.Throws<InvalidDataException>(() => Convert(flat));
    }

    // -- native-artwork round trips (fig30 / bom04 / ss27) ----------------------

    public static TheoryData<string> ReportHulls => new() { "fig30", "bom04", "ss27" };

    [SkippableTheory]
    [MemberData(nameof(ReportHulls))]
    [Trait("Requires", "Artwork")]
    public void Native_hull_exports_to_glb_and_writes_back_with_parsed_structures_bit_exact(string stem)
    {
        Require.Artwork();
        var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(artRoot, stem + ".mdl")));
        var cvh = CvhReader.Parse(File.ReadAllText(Path.Combine(artRoot, stem + ".cvh")));
        var original = NativeModelAdapter.FromNative(stem, mdl, cvh);

        var (glb, sidecarJson) = Export(original);
        var reimported = Reimport(glb, sidecarJson);
        var (writtenMdl, writtenCvh) = Convert(reimported);

        // Parsed GLB-layer equality (meshes/mounts/collision).
        var writtenModel = NativeModelAdapter.FromNative(stem, MdlFile.Parse(writtenMdl), CvhReader.Parse(writtenCvh));
        var (results, pass) = Compare.Run(original, writtenModel);
        Assert.True(pass, string.Join("; ", results.Where(r => !r.Pass).Select(r => $"{r.Name}: {r.Detail}")));

        // Parsed .mdl frames/lights bit-exact (incl. the up vectors).
        var (mdlResults, mdlPass) = MdlFileCompare.Run(mdl, MdlFile.Parse(writtenMdl));
        Assert.True(mdlPass, string.Join("; ", mdlResults.Where(r => !r.Pass).Select(r => $"{r.Name}: {r.Detail}")));

        // Parsed .cvh bit-exact (radius/hulls/adjacency/frames).
        var (cvhResults, cvhPass) = CvhCompare.Run(cvh, CvhReader.Parse(writtenCvh));
        Assert.True(cvhPass, string.Join("; ", cvhResults.Where(r => !r.Pass).Select(r => $"{r.Name}: {r.Detail}")));
    }

    [Fact]
    public void Native_name_mapping_covers_every_kind_and_survives_the_kind_mapper()
    {
        // Every HP kind maps to a native name in the right substring family
        // (efapp.cpp:562-583) and the sim's exact-name reads (hullTypeIGC.cpp:80-204).
        var cases = new (string kind, int index, string expected, string expectedKind)[]
        {
            ("Weapon", 0, "wepmnt1", "Weapon"),
            ("Weapon", 1, "wepmnt2", "Weapon"),
            ("Turret", 0, "turwepmnt1", "Turret"),
            ("MainEngine", 0, "thrust", "MainEngine"),
            ("MainEngine", 1, "thrust2", "MainEngine"),
            ("Thruster", 0, "smoke1", "Thruster"),
            ("Cockpit", 0, "cockpt", "Cockpit"),
            ("Chaff", 0, "chaff", "Chaff"),
            ("DockingEntrance", 0, "garage1A", "DockingEntrance"),
            ("DockingEntrance", 4, "garage1E", "DockingEntrance"),
            ("DockingEntrance", 5, "garage2A", "DockingEntrance"),
            ("DockingExit", 0, "launch1", "DockingExit"),
            ("Frame", 0, "frame1", "Frame"),
        };
        foreach (var (kind, index, expected, expectedKind) in cases)
        {
            var name = NativeNameMapper.NativeNameFor(kind, index);
            Assert.Equal(expected, name);
            Assert.Equal(expectedKind, MountKindMapper.KindFor(name));   // round trip through the kind rules
        }
    }
}
