using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-21/HUD-21b — the classic target pane's 3-D model preview. Headless tests
/// of the PURE rules (<see cref="TargetModelPreview"/>): the <c>SetTargetGeo</c>
/// gate with its REAL inputs (own-source climb, cluster, visibility, mesh),
/// native's max-vertex radius framing, the <c>TurnTo</c> signed-cos² up
/// fallback, the |dp|²&lt;1 default, concrete <c>PaneRect</c> numbers, the
/// recorded pane paint order, and a steady-state zero-allocation test.
/// HUD-21b replaces HUD-21's tautological tests (a static <c>DecorOrder</c>
/// array production never read, a null-input test that never called the draw
/// helper, a nose test that re-derived the eye distance, and <c>Eligible</c>
/// cases production bypassed with hard-coded <c>true</c>s).
/// </summary>
public class Hud21TargetModelPreviewTests
{
    // ── 1. The SetTargetGeo gate (WinTrek.cpp:8783-8818) ───────────────────

    [Fact]
    public void Eligible_accepts_an_in_cluster_visible_meshed_non_self_target()
    {
        // Target source ship #20, own source ship #10: different, so the native
        // GetSourceShip comparison (WinTrek.cpp:8796-8797) passes.
        foreach (var kind in new[] { TargetPreviewKind.Ship, TargetPreviewKind.Station, TargetPreviewKind.Asteroid })
            Assert.True(TargetModelPreview.Eligible(kind, targetSourceShipId: 20, ownSourceShipId: 10,
                    sameCluster: true, visible: true, hasMesh: true),
                $"{kind} should be eligible");
    }

    [Fact]
    public void Eligible_rejects_the_players_own_source_ship_and_own_turret()
    {
        // Native: `pmodel != trekClient.GetShip()->GetSourceShip()` AFTER the
        // target is replaced by its source ship. A gunner aiming at the carrier
        // they are turreting, or at their own turret, resolves to the SAME
        // source id and shows nothing.
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship,
            targetSourceShipId: 10, ownSourceShipId: 10, sameCluster: true, visible: true, hasMesh: true));
        // A turret whose source climb lands on the carrier (#10) is rejected too.
        int turretSource = 10;
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship,
            turretSource, ownSourceShipId: 10, sameCluster: true, visible: true, hasMesh: true));
        // A foreign ship (#20) is still fine from that same own source.
        Assert.True(TargetModelPreview.Eligible(TargetPreviewKind.Ship,
            20, ownSourceShipId: 10, sameCluster: true, visible: true, hasMesh: true));
    }

    [Fact]
    public void Eligible_rejects_every_other_excluded_fact()
    {
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship, 20, 10, sameCluster: false, visible: true, hasMesh: true));
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Asteroid, 20, 10, sameCluster: true, visible: false, hasMesh: true));
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Station, 20, 10, sameCluster: true, visible: true, hasMesh: false));
        // Stations/asteroids have no source ship (-1 on both sides of the gate
        // only when the target is a station: own stays a real ship id).
        Assert.True(TargetModelPreview.Eligible(TargetPreviewKind.Station, -1, 10, true, true, true));
    }

    [Fact]
    public void Eligible_only_knows_the_three_in_scope_kinds()
    {
        // P805 (row 409) — the kind set grew from the HUD-21 ship/station/asteroid
        // triple to include the aleph/probe/treasure/mine previews native's gate
        // also shows (WinTrek.cpp:8783-8818 is NOT kind-specific; warp surface
        // warpIGC.cpp:33, probe mesh probetypeigc.cpp:28-33, mine sprite
        // mineigc.cpp:103, treasure decal/model treasureIGC.cpp:118-171).
        var kinds = System.Enum.GetValues<TargetPreviewKind>();
        Assert.Equal(7, kinds.Length);
        Assert.Contains(TargetPreviewKind.Ship, kinds);
        Assert.Contains(TargetPreviewKind.Station, kinds);
        Assert.Contains(TargetPreviewKind.Asteroid, kinds);
        Assert.Contains(TargetPreviewKind.Warp, kinds);
        Assert.Contains(TargetPreviewKind.Probe, kinds);
        Assert.Contains(TargetPreviewKind.Treasure, kinds);
        Assert.Contains(TargetPreviewKind.Mine, kinds);
    }

    // ── 2. Own-source climb (the real gate inputs, HUD-21b fix 5) ──────────

    private const short Sector = 10;

    private static ServerShipUpdate Pose(short id, (float x, float y, float z) pos) =>
        ServerShipUpdate.FromValues(id, 0, new ControlData(0f, 0f, 0f, 1f), 1u,
            new Allegiance.Core.Collision.Vector3(pos.x, pos.y, pos.z), Orientation.Identity,
            new Allegiance.Core.Collision.Vector3(0f, 0f, 0f), [0f, 0f, 0f], 1f, 1f, 1f, 1f, 1f, 0f, 0f);

    private static ClientWorld WorldWith(params (short id, short parent)[] ships)
    {
        var w = new ClientWorld();
        foreach (var (id, parent) in ships)
        {
            w.Apply(new SetCluster { SectorId = Sector, Ship = Pose(id, (0f, 0f, 0f)) });
            w.Apply(new ShipStatusMessage
            {
                ShipId = id, SectorId = Sector, StationId = -1, ParentId = parent,
                State = parent >= 0 ? ShipStatusMessage.StateTurret : ShipStatusMessage.StateFlying,
            });
        }
        return w;
    }

    [Fact]
    public void Source_ship_climb_reaches_the_carrier_on_both_sides()
    {
        // carrier 10; enemy turret 11 aboard carrier 20; the player gunning from
        // own turret 12 aboard their own carrier 10.
        var world = WorldWith((10, (short)-1), (12, 10), (20, (short)-1), (11, 20));
        Assert.Equal(10, Game1.SourceShipId(world, world.Ships[12]));
        Assert.Equal(20, Game1.SourceShipId(world, world.Ships[11]));

        int ownSrc = Game1.SourceShipId(world, world.Ships[12]);
        // Aiming at your OWN turret (source = your carrier): nothing.
        int ownTurretSrc = Game1.SourceShipId(world, world.Ships[10]);
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship,
            ownTurretSrc, ownSrc, sameCluster: true, visible: true, hasMesh: true));
        // Aiming at the enemy turret: it previews the ENEMY CARRIER (source 20).
        int enemySrc = Game1.SourceShipId(world, world.Ships[11]);
        Assert.True(TargetModelPreview.Eligible(TargetPreviewKind.Ship,
            enemySrc, ownSrc, sameCluster: true, visible: true, hasMesh: true));
    }

    [Fact]
    public void Source_ship_climb_is_cycle_safe()
    {
        // A malformed wire parent cycle must not hang the frame.
        var world = WorldWith((1, 2), (2, 1));
        var id = Game1.SourceShipId(world, world.Ships[1]);
        Assert.Contains(id, new short[] { 1, 2 });
    }

    // ── 3. Radius-1 framing = native MAX VERTEX radius (HUD-21b fix 1) ──────

    private static MdlMesh Mesh(params Vector3[] points)
    {
        var pos = new float[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            pos[3 * i] = points[i].X;
            pos[3 * i + 1] = points[i].Y;
            pos[3 * i + 2] = points[i].Z;
        }
        return new MdlMesh
        {
            Positions = pos,
            TexCoords = new float[points.Length * 2],
            Normals = new float[points.Length * 3],
            Indices = [0, 1, 2],
        };
    }

    [Fact]
    public void Max_vertex_radius_of_a_cube_corner_set_is_the_space_diagonal()
    {
        const float h = 3f;      // half-edge
        var cube = Mesh(
            new(-h, -h, -h), new(h, -h, -h), new(-h, h, -h), new(h, h, -h),
            new(-h, -h, h), new(h, -h, h), new(-h, h, h), new(h, h, h));

        // Native TMeshGeo::GetRadius = max |vertex| (engine/geometry.cpp:408-418).
        Assert.Equal(MathF.Sqrt(3f) * h, NativeShipResolver.MaxVertexRadius([cube]), 4);
        // The farthest cube vertex maps to radius 1 under the preview scale.
        Assert.Equal(1f, MathF.Sqrt(3f) * h * TargetModelPreview.ScaleForMaxVertexRadius(MathF.Sqrt(3f) * h), 5);
    }

    [Fact]
    public void Max_vertex_radius_includes_lod_levels_like_native_LODGeo()
    {
        // Native LODGeo::GetRadius is the max over ALL children — base mesh and
        // every LOD level (engine/geometry.cpp:1748-1758).
        var baseMesh = Mesh(new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f));
        var lodMesh = Mesh(new(0f, 0f, 2.5f), new(0f, 1f, 0f), new(1f, 0f, 0f));
        var lods = new[] { new MdlLodLevel { PixelRadius = 32f, Meshes = [lodMesh] } };
        Assert.Equal(1f, NativeShipResolver.MaxVertexRadius([baseMesh]), 5);
        Assert.Equal(2.5f, NativeShipResolver.MaxVertexRadius([baseMesh], lods), 5);
        Assert.Equal(1f, NativeShipResolver.MaxVertexRadius([baseMesh], []), 5);
    }

    [Fact]
    public void Max_vertex_radius_differs_from_the_bounding_box_sphere_for_a_round_rock()
    {
        // A sphere-ish rock (vertices sampled on radius 2) is exactly the HUD-21
        // bug: the bounding-box-sphere merge (scene proxy) returns the box half
        // diagonal √3·r, while native's max-vertex radius is r — so a preview
        // normalised by the merge drew at 1/√3 ≈ 58 % of the pane.
        const float r = 2f;
        var pts = new List<Vector3>();
        for (int i = 0; i < 64; i++)
        {
            float a = MathHelper.TwoPi * i / 64f;
            pts.Add(new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r));   // equator
            pts.Add(new Vector3(0f, MathF.Cos(a) * r, MathF.Sin(a) * r));   // meridian
        }
        pts.Add(new(0f, r, 0f));
        pts.Add(new(0f, -r, 0f));
        var rock = Mesh([.. pts]);

        Assert.Equal(r, NativeShipResolver.MaxVertexRadius([rock]), 4);
        float merged = NativeShipResolver.MergedRadius([rock]);
        Assert.Equal(MathF.Sqrt(3f) * r, merged, 3);
        Assert.Equal(1f / MathF.Sqrt(3f), r / merged, 4);                  // the HUD-21 factor
    }

    [Fact]
    public void Preview_scale_puts_the_farthest_vertex_on_radius_one_inside_the_ortho_frame()
    {
        // The cube's farthest vertex, scaled by 1/MaxVertexRadius, lands on the
        // unit sphere and therefore inside the 2×2 orthographic frame (native
        // radius-1 normalisation, thinggeo.cpp:373-381 + the ortho camera).
        const float h = 4f;
        var cube = Mesh(
            new(-h, -h, -h), new(h, -h, -h), new(-h, h, -h), new(h, h, -h),
            new(-h, -h, h), new(h, -h, h), new(-h, h, h), new(h, h, h));
        float maxR = NativeShipResolver.MaxVertexRadius([cube]);
        float scale = TargetModelPreview.ScaleForMaxVertexRadius(maxR);
        Assert.Equal(1f / (MathF.Sqrt(3f) * h), scale, 5);

        var view = TargetModelPreview.View(new Vector3(0f, 1f, 5f), Vector3.Up, -Vector3.UnitZ);
        var vp = view * TargetModelPreview.Projection();
        float maxAbs = 0f;
        for (int i = 0; i < 8; i++)
        {
            float x = cube.Positions[3 * i] * scale;
            float y = cube.Positions[3 * i + 1] * scale;
            float z = cube.Positions[3 * i + 2] * scale;
            Assert.Equal(1f, new Vector3(x, y, z).Length(), 5);
            var clip = Vector4.Transform(new Vector4(x, y, z, 1f), vp);
            maxAbs = MathF.Max(maxAbs, MathF.Max(MathF.Abs(clip.X), MathF.Abs(clip.Y)));
        }
        Assert.InRange(maxAbs, -1f, 1f);          // every vertex is inside the 2×2 frame
        Assert.True(maxAbs < 1f);                 // a cube corner sits inside the frame...
        // ...while the radius-1 bounding sphere (what the scale normalises to)
        // touches all four frame edges — see Unit_sphere_projects_to_exactly_the_full_viewport.
    }

    [Fact]
    public void Unit_sphere_projects_to_exactly_the_full_viewport()
    {
        // Radius-1 normalisation (thinggeo.cpp:373-381,1482-1573, net scale
        // 1/maxVertexRadius) + the ortho 2×2 viewport (camera.cpp:46-69,
        // image.cpp:1164-1165) → a radius-1 sphere's silhouette fills the pane.
        var view = TargetModelPreview.View(new Vector3(0f, 1f, 5f), Vector3.Up, -Vector3.UnitZ);
        var vp = view * TargetModelPreview.Projection();

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i <= 60; i++)
        {
            float theta = MathHelper.TwoPi * i / 60f;
            for (int j = 0; j <= 30; j++)
            {
                float phi = MathHelper.Pi * j / 30f;
                var p = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi),
                                    MathF.Sin(phi) * MathF.Sin(theta));
                var clip = Vector4.Transform(new Vector4(p, 1f), vp);
                float nx = clip.X / clip.W, ny = clip.Y / clip.W;
                minX = MathF.Min(minX, nx); maxX = MathF.Max(maxX, nx);
                minY = MathF.Min(minY, ny); maxY = MathF.Max(maxY, ny);
            }
        }
        Assert.InRange(minX, -1.006f, -0.995f);
        Assert.InRange(maxX, 0.995f, 1.006f);
        Assert.InRange(minY, -1.006f, -0.995f);
        Assert.InRange(maxY, 0.995f, 1.006f);
    }

    // ── 4. TurnTo uses the SIGNED cos² (HUD-21b fix 4) ──────────────────────

    [Fact]
    public void TurnTo_keeps_the_players_up_at_cos_0_992_because_cos2_is_below_the_threshold()
    {
        // Orientation::TurnTo compares CosUp2 (SIGNED cos²) with ±0.99
        // (zlib/orientation.cpp:220-231, cosX2 :395-414). cos 0.992 → c² 0.984
        // < 0.99, so the player's up is kept — HUD-21 compared raw cos (0.992 >
        // 0.99) and flipped to −forward here, which was wrong.
        var dir = new Vector3(MathF.Sqrt(1f - 0.992f * 0.992f), 0.992f, 0f);
        Assert.Equal(0.992f, Vector3.Dot(dir, Vector3.Up), 5);
        Assert.Equal(Vector3.Up, TargetModelPreview.ViewUp(dir, Vector3.Up, -Vector3.UnitZ));

        // Just past native's effective |cos| = √0.99 ≈ 0.99499 (c² > 0.99) the
        // fallback kicks in, and the sign is preserved.
        // dot > +0.99 → −forward; own forward is −Z, so −forward = +Z.
        var upish = new Vector3(MathF.Sqrt(1f - 0.996f * 0.996f), 0.996f, 0f);
        Assert.Equal(Vector3.UnitZ, TargetModelPreview.ViewUp(upish, Vector3.Up, -Vector3.UnitZ));
        // dot < −0.99 → +forward = −Z.
        var downish = new Vector3(MathF.Sqrt(1f - 0.996f * 0.996f), -0.996f, 0f);
        Assert.Equal(-Vector3.UnitZ, TargetModelPreview.ViewUp(downish, Vector3.Up, -Vector3.UnitZ));
    }

    [Fact]
    public void ViewUp_falls_back_to_minus_or_plus_forward_exactly_at_vertical()
    {
        Assert.Equal(Vector3.UnitZ, TargetModelPreview.ViewUp(Vector3.Up, Vector3.Up, -Vector3.UnitZ));
        Assert.Equal(-Vector3.UnitZ, TargetModelPreview.ViewUp(-Vector3.Up, Vector3.Up, -Vector3.UnitZ));
    }

    // ── 5. A dead-ahead identity ship shows its nose to the camera ──────────

    [Fact]
    public void Dead_ahead_identity_ship_points_its_nose_toward_the_viewer()
    {
        // Native: the preview orientation is the player orientation TurnTo(dp),
        // with the target drawn in it; the camera sits at −dir·8 looking at the
        // origin (WinTrek.cpp:328-330,438-444). For a target dead ahead along +Z
        // the camera eye is (0,0,−8). The native renderers fold in the RY(π)
        // mesh flip (thinggeo.cpp:1501-1503), so the identity ship's nose lies
        // along −Z and must be CLOSER to the camera: view-space Z is larger.
        var dp = new Vector3(0f, 0f, 1f);
        var view = TargetModelPreview.View(dp, Vector3.Up, -Vector3.UnitZ);

        var nose = Vector3.Transform(Vector3.UnitZ, Matrix.CreateRotationY(MathHelper.Pi));
        var tail = -nose;
        Assert.Equal(-1f, nose.Z, 5);
        Assert.Equal(0f, nose.X, 5);              // (float trig: ~1e-7 residue)

        float noseViewZ = Vector3.Transform(nose, view).Z;
        float tailViewZ = Vector3.Transform(tail, view).Z;
        Assert.True(noseViewZ > tailViewZ,
            $"nose must be nearer the camera in view space (nose z={noseViewZ}, tail z={tailViewZ})");
        // The nose is at the eye-facing side of the model, the tail behind it.
        Assert.True((nose - new Vector3(0f, 0f, -8f)).Length() < (tail - new Vector3(0f, 0f, -8f)).Length());
    }

    // ── 6. |dp|² < 1 → the (0,1,5) default (WinTrek.cpp:438-444) ────────────

    [Fact]
    public void Tiny_dp_uses_the_default()
    {
        Assert.Equal(TargetModelPreview.DefaultDp, TargetModelPreview.EffectiveDp(new Vector3(0.5f, 0.5f, 0.5f)));
        Assert.Equal(new Vector3(1f, 0f, 0f), TargetModelPreview.EffectiveDp(new Vector3(1f, 0f, 0f)));
    }

    // ── 7. PaneRect: concrete numbers at 1920×1080 and 2560×1440 ───────────

    [Fact]
    public void PaneRect_at_1920x1080_is_the_concrete_native_96s_square()
    {
        // s = clamp(1080/680, .9, 2.1) limited by the minimap = 1.588235…;
        // x0 = 960 − 320s = 451.7647; y0 = 1080 − 25s = 1040.2941.
        // round(96s)=152; x = round(451.7647 + 270s) = 881;
        // top = round(1040.2941 − 9s − 152) = 874.
        Assert.Equal(new Rectangle(881, 874, 152, 152), TargetModelPreview.PaneRect(451.7647f, 1040.2941f, 1.5882353f));
    }

    [Fact]
    public void PaneRect_at_2560x1440_is_the_concrete_native_96s_square()
    {
        // s = 2.1 (height clamp); x0 = 1280 − 320·2.1 = 608;
        // y0 = 1440 − 25·2.1 = 1387.5; round(96·2.1)=202;
        // x = round(608 + 270·2.1) = 1175; top = round(1387.5 − 18.9 − 202) = 1167.
        Assert.Equal(new Rectangle(1175, 1167, 202, 202), TargetModelPreview.PaneRect(608f, 1387.5f, 2.1f));
    }

    // ── 7b. Camera basis from the flight view (Claude review fix 1) ─────────

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(25f, 0f, 0f)]       // pitched
    [InlineData(0f, 0f, 40f)]       // rolled
    [InlineData(25f, 0f, 40f)]      // pitched AND rolled
    [InlineData(0f, 90f, 0f)]       // 90° yaw
    [InlineData(-30f, 90f, 60f)]    // all three
    public void Camera_basis_reads_the_columns_of_a_CreateLookAt_view(float pitchDeg, float yawDeg, float rollDeg)
    {
        // Every flight view is Matrix.CreateLookAt(Zero, fwd, up) (CockpitCamera,
        // Director, FreeCam, ExternalCameras, CommandCamera). Build a non-trivial
        // orthonormal basis and check the helper recovers it.
        var rot = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(yawDeg),
            MathHelper.ToRadians(pitchDeg), MathHelper.ToRadians(rollDeg));
        var fwd = Vector3.TransformNormal(Vector3.Forward, rot);
        var up = Vector3.TransformNormal(Vector3.Up, rot);
        var view = Matrix.CreateLookAt(Vector3.Zero, fwd, up);

        TargetModelPreview.CameraBasis(view, out var gotUp, out var gotFwd);
        AssertVec(up, gotUp);
        AssertVec(fwd, gotFwd);
    }

    [Fact]
    public void Camera_basis_is_not_the_transposed_row_reading()
    {
        // HUD-21b read rows (M21..M23 / −M31..M33): with a 90° yaw the row
        // reading's forward is +X/−X swapped. Pin a concrete case.
        var fwd = Vector3.Right;          // yawed 90° to the right
        var up = Vector3.Up;
        var view = Matrix.CreateLookAt(Vector3.Zero, fwd, up);
        TargetModelPreview.CameraBasis(view, out var gotUp, out var gotFwd);
        AssertVec(Vector3.Right, gotFwd);
        AssertVec(Vector3.Up, gotUp);
        var rowFwd = new Vector3(-view.M31, -view.M32, -view.M33);
        Assert.True(Vector3.Distance(rowFwd, fwd) > 1f, "the row reading must differ for a yawed camera");
    }

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    // ── 8. Composition order through the real HUD-14 recording harness ──────

    /// <summary>HUD-14-style recorder: the production pane stage hooks call
    /// <see cref="HudRenderer.ElementDrawn"/>, and this subclass records them.
    /// The model hook is overridden (it needs a GPU texture) but calls base, so
    /// the null-input guard is still exercised by the other test.</summary>
    private class RecordingHud : HudRenderer
    {
        // Never constructed: the harness uses GetUninitializedObject (no
        // SpriteBatch/GPU). The base ctor is required by the compiler only.
        protected RecordingHud() : base(null!, null!, null!, null!) { }

        public static readonly List<string> Drawn = [];
        public int AdditivePasses;

        protected override void ElementDrawn(params string[] ids)
        {
            foreach (var id in ids) Drawn.Add(id);
        }

        // Native composes the oval/word art additively (hudBlendMode =
        // BlendModeAdd, dialog.mdl:34). Observe the pass instead of opening it.
        protected override void BeginAdditiveSpritePass()
        {
            AdditivePasses++;
            Drawn.Add("additive pass");
        }

        protected override void EndAdditiveSpritePass() { }

        /// <summary>Call the production decorative-stage sequence.</summary>
        public void PaintDecor(HudTargetPane tp, float x0, float y0, float s, Color side)
            => DrawTargetPaneDecor(tp, x0, y0, s, side);

        /// <summary>Call the production preview helper directly.</summary>
        public void PaintPreview(float x0, float y0, float s) => DrawTargetModelPreview(x0, y0, s);
    }

    /// <summary>Records the model STAGE (it needs a GPU texture to actually
    /// draw), so the stage order is observable without a GraphicsDevice.</summary>
    private sealed class StageRecorder : RecordingHud
    {
        protected override void DrawTargetModelPreview(float x0, float y0, float s)
            => Drawn.Add("Target model preview");

        protected override void DrawTargetExperience(HudTargetPane tp, float x0, float y0, float s)
            => Drawn.Add("Target kill bonus");
    }

    private static T Uninitialized<T>() where T : HudRenderer
    {
        var hud = (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        System.GC.SuppressFinalize(hud);
        return hud;
    }

    [Fact]
    public void Target_pane_decor_paints_in_native_reverse_group_order()
    {
        // Native GroupImage::AddImage PUSHES FRONT (engine/image.cpp:918-921)
        // and Render walks front→back (image.cpp:954-972), so the mdl list
        // DefaultNormalTargetDisplay (dialog.mdl:4217-4242) paints in REVERSE:
        // OvalColor, Oval, Word2, Word1, TargetExperienceImage, the model, then
        // HullGauge and ShieldGauge on top.
        RecordingHud.Drawn.Clear();
        var hud = Uninitialized<StageRecorder>();
        hud.PaintDecor(default, 451f, 1040f, 1.588f, Color.White);   // the production stage sequence

        int ovalColour = RecordingHud.Drawn.IndexOf("Target oval colour");
        int additive = RecordingHud.Drawn.IndexOf("additive pass");
        int oval = RecordingHud.Drawn.IndexOf("Target oval art");
        int bonus = RecordingHud.Drawn.IndexOf("Target kill bonus");
        int model = RecordingHud.Drawn.IndexOf("Target model preview");
        int hull = RecordingHud.Drawn.IndexOf("Target hull");
        int shield = RecordingHud.Drawn.IndexOf("Target shield");
        Assert.All(new[] { ovalColour, additive, oval, bonus, model, hull, shield }, i => Assert.True(i >= 0));
        // OvalColor is a plain ImportImage3D (dialog.mdl:4161): drawn in the
        // NORMAL alpha batch, before the additive pass opens.
        Assert.True(ovalColour < additive, "the oval colour must paint in the normal alpha batch before the additive pass");
        Assert.True(additive < oval, "the oval/word art is the additive stage");
        Assert.True(oval < bonus, $"oval art ({oval}) under the kill bonus ({bonus})");
        Assert.True(bonus < model, $"kill bonus ({bonus}) under the model ({model})");
        Assert.True(model < hull && hull < shield, "the gauges paint over the model, shield last");
        Assert.Equal(1, hud.AdditivePasses);
    }

    [Fact]
    public void Oval_colour_tint_is_premultiplied_for_the_alpha_batch()
    {
        // Native targetHudColor = side·0.85 with alpha 0.5 (WinTrek.cpp:8785-8786,
        // 8813-8816); the HUD batch is premultiplied, so rgb is pre-scaled by a.
        var c = HudRenderer.PremultipliedTint(new Color(200, 100, 50, 128));
        float a = 128f / 255f;
        Assert.Equal(128, c.A);
        Assert.InRange(c.R, (int)(200 * a) - 1, (int)(200 * a) + 1);
        Assert.InRange(c.G, (int)(100 * a) - 1, (int)(100 * a) + 1);
        Assert.InRange(c.B, (int)(50 * a) - 1, (int)(50 * a) + 1);
    }

    [Fact]
    public void Null_preview_input_draws_nothing()
    {
        // Call the REAL production helper with a null input: it must return
        // before touching the SpriteBatch (which is null on the harness object —
        // any draw attempt would throw, not silently pass).
        RecordingHud.Drawn.Clear();
        var hud = Uninitialized<RecordingHud>();
        Assert.Null(hud.TargetModelPreview);
        hud.PaintPreview(451f, 1040f, 1.588f);
        Assert.DoesNotContain("Target model preview", RecordingHud.Drawn);
        Assert.False(hud.PreviewWillDraw);
    }

    // ── 9. Resolved-art cache key (HUD-21b fix 2) ───────────────────────────

    [Fact]
    public void Preview_key_matches_only_the_same_target_and_hull_or_model()
    {
        var a = new TargetPreviewKey(TargetPreviewKind.Ship, 42, 17, null, null);
        Assert.True(a.Matches(new TargetPreviewKey(TargetPreviewKind.Ship, 42, 17, null, null)));
        // A different target re-resolves.
        Assert.False(a.Matches(new TargetPreviewKey(TargetPreviewKind.Ship, 43, 17, null, null)));
        // A hull change on the same ship id re-resolves.
        Assert.False(a.Matches(new TargetPreviewKey(TargetPreviewKind.Ship, 42, 18, null, null)));
        // A different kind re-resolves even with equal ids.
        Assert.False(a.Matches(new TargetPreviewKey(TargetPreviewKind.Asteroid, 42, 17, null, null)));
        // A station/asteroid model or texture swap re-resolves.
        var st = new TargetPreviewKey(TargetPreviewKind.Asteroid, 7, -1, "bgrnd03", "bgrnd03bmp");
        Assert.True(st.Matches(new TargetPreviewKey(TargetPreviewKind.Asteroid, 7, -1, "bgrnd03", "bgrnd03bmp")));
        Assert.False(st.Matches(new TargetPreviewKey(TargetPreviewKind.Asteroid, 7, -1, "bgrnd05", "bgrnd03bmp")));
        Assert.False(st.Matches(new TargetPreviewKey(TargetPreviewKind.Asteroid, 7, -1, "bgrnd03", "otherbmp")));
    }

    // ── 10. Steady state (unchanged target) allocates nothing ───────────────

    private sealed class FakeArt { }

    [Fact]
    public void Steady_state_preview_cache_step_allocates_nothing()
    {
        // Drives the PRODUCTION key → cache → gate step
        // (TargetPreviewCache.Step, called once per frame by
        // Game1.RenderTargetModelPreview) with an UNCHANGED target, plus the
        // per-frame compose math. The resolver allocates (as the real one does),
        // so any steady-state resolve would show up here.
        var cache = new TargetPreviewCache<FakeArt>(_ => new FakeArt());
        var key = new TargetPreviewKey(TargetPreviewKind.Station, 42, -1, "acs01", "acs01bmp");
        Vector3 dp = new(0f, 1f, 5f);
        var view = Matrix.CreateLookAt(Vector3.Zero, Vector3.Forward, Vector3.Up);
        object? last = null;

        void Frame()
        {
            // Rebuilt every frame from the same strings, like production.
            var k = new TargetPreviewKey(key.Kind, key.TargetId, key.HullId, key.ModelStem, key.TextureHint);
            last = cache.Step(k, targetSourceShipId: -1, ownSourceShipId: 10, sameCluster: true, visible: true);
            TargetModelPreview.CameraBasis(view, out var up, out var fwd);
            _ = TargetModelPreview.View(dp, up, fwd);
            _ = TargetModelPreview.Projection();
            _ = TargetModelPreview.Lighting(dp);
        }

        for (int i = 0; i < 200; i++) Frame();          // JIT warm-up (+ the one resolve)
        Assert.Equal(1, cache.ResolveCount);
        // Measure in rounds of 10 000 frames. A one-off runtime event (tiered
        // JIT / OSR promotion landing mid-round under a loaded test host) was
        // observed as a single ~6 KB blip in one round; a REAL per-frame
        // allocation would show in every round, so require one clean round.
        var rounds = new List<long>();
        for (int round = 0; round < 10; round++)
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) Frame();
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            rounds.Add(allocated);
            if (allocated == 0) break;
        }
        Assert.True(rounds[^1] == 0, "steady state allocated in every round: " + string.Join(",", rounds));
        Assert.Equal(1, cache.ResolveCount);
        Assert.NotNull(last);
    }

    [Fact]
    public void A_target_without_a_native_mesh_resolves_once_and_stays_empty()
    {
        int calls = 0;
        var cache = new TargetPreviewCache<FakeArt>(_ => { calls++; return null; });
        var key = new TargetPreviewKey(TargetPreviewKind.Asteroid, 7, -1, "nomesh", null);
        for (int i = 0; i < 100; i++)
            Assert.Null(cache.Step(key, -1, 10, sameCluster: true, visible: true));
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.ResolveCount);
    }

    [Fact]
    public void Cache_re_resolves_on_a_key_change_and_gates_with_the_real_inputs()
    {
        int calls = 0;
        var cache = new TargetPreviewCache<FakeArt>(_ => { calls++; return new FakeArt(); });
        var a = new TargetPreviewKey(TargetPreviewKind.Ship, 20, 17, null, null);
        var b = new TargetPreviewKey(TargetPreviewKind.Ship, 20, 18, null, null);   // hull swap
        Assert.NotNull(cache.Step(a, 20, 10, true, true));
        Assert.NotNull(cache.Step(a, 20, 10, true, true));
        Assert.Equal(1, calls);
        Assert.NotNull(cache.Step(b, 20, 10, true, true));
        Assert.Equal(2, calls);
        // The gate still applies on a cache hit: own source, other cluster, unseen.
        Assert.Null(cache.Step(b, 10, 10, true, true));
        Assert.Null(cache.Step(b, 20, 10, false, true));
        Assert.Null(cache.Step(b, 20, 10, true, false));
        Assert.Equal(2, calls);
    }

    // ── 11. Production wiring guards for the review fixes ───────────────────

    private static string SourceFile(params string[] relative)
    {
        string root = System.AppContext.BaseDirectory;
        while (!System.IO.Directory.Exists(System.IO.Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = System.IO.Path.GetDirectoryName(root) ?? throw new System.InvalidOperationException("Repository not found");
        var parts = new List<string> { root, "src", "Allegiance.ClientCore.Game" };
        parts.AddRange(relative);
        return System.IO.File.ReadAllText(System.IO.Path.Combine([.. parts]));
    }

    private static string MethodSource(string source, string signature)
    {
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new System.InvalidOperationException("unterminated " + signature);
    }

    [Fact]
    public void Production_gate_uses_the_real_source_ids_and_the_max_vertex_scale()
    {
        var src = SourceFile("Game1.Rendering.cs");
        var render = MethodSource(src, "private bool RenderTargetModelPreview(short me, Vector3 cameraEye, in Matrix view, RenderTarget2D? restoreTo)");
        // Fix 5 — the gate (inside TargetPreviewCache.Step) takes the CLIMBED ids.
        Assert.Contains("_previewCache.Step(key, targetSourceId, ownSourceId,", render);
        Assert.Contains("SourceShipId(world, ownEnt)", render);
        // Fix 1 — the preview scale is native's max-vertex radius.
        var resolve = MethodSource(src, "private PreviewArt? ResolvePreviewArt(TargetPreviewKey key)");
        Assert.Contains("TargetModelPreview.ScaleForMaxVertexRadius(geo.MaxVertexRadius)", resolve);
        Assert.DoesNotContain("MeshRadius)", resolve.Replace("MaxVertexRadius)", ""));
        // Fix 2 — one rebind in the renderer.
        Assert.DoesNotContain("GetRenderTargets", render);
        // Claude review — the camera basis is read from the view's COLUMNS.
        Assert.Contains("TargetModelPreview.CameraBasis(view, out var camUp, out var camFwd);", render);
        Assert.DoesNotContain("view.M21", render);
        // Fix 6 — the camera pose supplies dp (m_position), not the ship pose.
        Assert.Contains("var dp = targetPos - cameraEye;", render);
    }

    [Fact]
    public void Stale_preview_is_cleared_at_the_top_of_the_flying_branch()
    {
        var src = SourceFile("Game1.Rendering.cs");
        int flying = src.IndexOf("else // Flying", System.StringComparison.Ordinal);
        Assert.True(flying >= 0);
        int clear = src.IndexOf("_hudTargetModelPreview = null;", flying, System.StringComparison.Ordinal);
        int sceneRef = src.IndexOf("if (TrySceneReference(world, out short me, out var myPose)", flying, System.StringComparison.Ordinal);
        Assert.True(clear > flying && clear < sceneRef,
            "the preview must be cleared before the scene-reference/generation gate can fail");
    }
}
