using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;
using Vector3 = Microsoft.Xna.Framework.Vector3;
using CVector = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P1016HardpointTests
{
    private static (DataHullTypeIGC Hull, CvhFile Model, LoadoutV2Model Loadout) Fixture(string name)
    {
        // Called only after Require.Artwork: no static corpus initializers.
        var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!;
        var hull = catalog.Hulls.First(h => h.Name == name);
        var model = CvhReader.Parse(File.ReadAllText(Path.Combine(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork), hull.ModelName + ".cvh")));
        return (hull, model, new(new ShopModel(catalog, hull.HullId, [], 10000), turretGunner: _ => "Seat pilot"));
    }

    private static LoadoutV2PreviewCamera Camera(DataHullTypeIGC hull, float angle = 0)
    {
        float radius = .5f * hull.Length;
        return new(hull.HullId, Matrix.CreateRotationY(angle),
            Matrix.CreateLookAt(new(0, 0, 2.4f * radius), Vector3.Zero, Vector3.Up),
            Matrix.CreatePerspectiveFieldOfView(DockedHullPreviewFraming.FovRadians, 1.5f, 1, 10000));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("Fighter")]
    [InlineData("Bomber")]
    [InlineData("Cruiser")]
    public void Real_frames_project_to_independently_calculated_pixels(string name)
    {
        Require.Artwork();
        var (hull, cvh, loadout) = Fixture(name);
        var projection = new LoadoutV2Hardpoints(hull, cvh);
        var rect = new TouchRect(310, 120, 900, 600);
        foreach (var row in loadout.SlotRows.Where(s => s.Slot.Type == EquipmentType.Weapon))
        {
            var frame = cvh.Frames.Single(f => f.Name == hull.Hardpoints[row.Mount].FrameName);
            float scale = .5f * hull.Length / cvh.Radius;
            var p = new Vector3(-frame.Position.X, frame.Position.Y, frame.Position.Z) * scale;
            float z = 2.4f * .5f * hull.Length - p.Z;
            float cot = 1 / MathF.Tan(DockedHullPreviewFraming.FovRadians / 2);
            var expected = new Vector2(rect.X + (1 + p.X * cot / (1.5f * z)) * rect.Width / 2,
                rect.Y + (1 - p.Y * cot / z) * rect.Height / 2);
            var marker = Assert.IsType<LoadoutV2HardpointMarker>(projection.Project(row, Camera(hull), rect));
            Assert.InRange(Vector2.Distance(expected, marker.Point), 0, .001f);
            Assert.Equal(!hull.Hardpoints[row.Mount].Fixed, marker.IsTurret);
            Assert.Equal(marker.IsTurret ? "Seat pilot" : null, marker.GunnerName);
            Assert.Equal(marker.IsTurret ? 49 : 0, marker.Arc.Count);
            Assert.False(marker.ArcLimitKnown); // Native PCore001 has no minDot.
        }
        if (name == "Fighter") Assert.All(hull.Hardpoints, h => Assert.True(h.Fixed));
        else Assert.True(hull.MaxWeapons > hull.MaxFixedWeapons);
        if (name == "Cruiser") Assert.True(hull.MaxWeapons - hull.MaxFixedWeapons >= 3);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("Fighter")]
    [InlineData("Bomber")]
    [InlineData("Cruiser")]
    public void Missing_frames_and_nonweapon_slots_never_invent_a_marker(string name)
    {
        Require.Artwork();
        var (hull, cvh, loadout) = Fixture(name);
        var rect = new TouchRect(0, 0, 900, 600);
        var rows = loadout.SlotRows;
        foreach (var row in rows)
            Assert.Null(new LoadoutV2Hardpoints(hull, null).Project(row, Camera(hull), rect));
        var noFrames = new CvhFile { Radius = cvh.Radius, EllipseEquation = cvh.EllipseEquation,
            EllipseRadiusMultiplier = cvh.EllipseRadiusMultiplier, Hulls = cvh.Hulls, Frames = [] };
        foreach (var row in rows)
            Assert.Null(new LoadoutV2Hardpoints(hull, noFrames).Project(row, Camera(hull), rect));
        var projection = new LoadoutV2Hardpoints(hull, cvh);
        Assert.Contains(rows, s => s.Slot.Type != EquipmentType.Weapon);
        foreach (var row in rows.Where(s => s.Slot.Type != EquipmentType.Weapon))
            Assert.Null(projection.Project(row, Camera(hull), rect));
        Assert.Null(projection.Project(rows[0], Camera(hull) with { HullId = -1 }, rect));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Authored_missing_Bomber_frame_does_not_get_the_simulation_center_fallback()
    {
        Require.Artwork();
        var root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var catalog = ShopCatalog.TryLoad(root)!;
        var hull = catalog.Hulls.Single(h => h.HullId == 124); // Bomber / bom400, missing rwepemt.
        var cvh = CvhReader.Parse(File.ReadAllText(Path.Combine(root, hull.ModelName + ".cvh")));
        var model = new LoadoutV2Model(new ShopModel(catalog, hull.HullId, [], 0));
        Assert.Equal("rwepemt", hull.Hardpoints[1].FrameName);
        Assert.DoesNotContain(cvh.Frames, f => f.Name == "rwepemt");
        Assert.Null(new LoadoutV2Hardpoints(hull, cvh).Project(model.SlotRows[1], Camera(hull), new(0, 0, 900, 600)));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Selection_consumes_existing_hover_focus_and_open_picker_without_changing_shop()
    {
        Require.Artwork();
        var (_, _, model) = Fixture("Bomber");
        var shop = model.Shop;
        var chosen = shop.ChosenParts.ToArray();
        var frame = LoadoutV2Presentation.Build(model, null, null);
        Assert.Equal(-1, LoadoutV2Hardpoints.SelectedSlot(frame, null));
        shop.ToggleTab();
        frame = LoadoutV2Presentation.Build(model, null, null);
        Assert.Equal(0, LoadoutV2Hardpoints.SelectedSlot(frame, null));
        var layout = frame.Layout(1920, 1080, 1);
        var turret = layout.Elements.First(e => e.Kind == LoadoutV2Layout.Kind.Slot && model.SlotRows[e.Index].IsTurret);
        var hover = layout.HitTest(turret.Bounds.X + 3, turret.Bounds.Y + 3);
        Assert.Equal(turret.Index, LoadoutV2Hardpoints.SelectedSlot(frame, hover));
        var shield = layout.Elements.First(e => e.Kind == LoadoutV2Layout.Kind.Slot && model.SlotRows[e.Index].Slot.Type == EquipmentType.Shield);
        Assert.Equal(shield.Index, LoadoutV2Hardpoints.SelectedSlot(frame, shield));
        shop.Activate();
        frame = LoadoutV2Presentation.Build(model, null, null);
        Assert.Equal(-1, frame.SlotFocus);
        Assert.Equal(0, LoadoutV2Hardpoints.SelectedSlot(frame, null));
        Assert.Equal(chosen, shop.ChosenParts);
        Assert.Equal(10000, shop.MyMoney);
    }

    [Fact]
    public void Homogeneous_projection_handles_offsets_camera_clip_and_nonfinite_points()
    {
        var rect = new TouchRect(100, 200, 600, 400);
        var matrix = Matrix.CreateLookAt(new(0, 0, 10), Vector3.Zero, Vector3.Up)
            * Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver2, 1.5f, 1, 100);
        Assert.Equal(new Vector2(400, 400), LoadoutV2Hardpoints.ScreenPoint(Vector3.Zero, matrix, rect));
        var p = LoadoutV2Hardpoints.ScreenPoint(new(3, 2, 0), matrix, rect)!.Value;
        Assert.InRange(Vector2.Distance(new(460, 360), p), 0, .001f);
        Assert.Null(LoadoutV2Hardpoints.ScreenPoint(new(0, 0, 20), matrix, rect));
        Assert.Null(LoadoutV2Hardpoints.ScreenPoint(new(0, 0, -200), matrix, rect));
        Assert.Null(LoadoutV2Hardpoints.ScreenPoint(new(float.NaN, 0, 0), matrix, rect));
        Assert.Null(LoadoutV2Hardpoints.ScreenPoint(Vector3.Zero, matrix, new(0, 0, 0, 400)));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Arc_uses_real_frame_direction_and_optional_minDot_without_affecting_fixed_guns()
    {
        Require.Artwork();
        var (hull, cvh, model) = Fixture("Bomber");
        var row = model.SlotRows.First(s => s.IsTurret);
        var projection = new LoadoutV2Hardpoints(hull, cvh);
        var camera = Camera(hull);
        var rect = new TouchRect(0, 0, 900, 600);
        var marker = projection.Project(row, camera, rect, .5f)!;
        Assert.True(marker.ArcLimitKnown);
        var raw = cvh.Frames.Single(f => f.Name == hull.Hardpoints[row.Mount].FrameName);
        var direction = Vector3.Normalize(new(raw.Forward.X, -raw.Forward.Y, -raw.Forward.Z));
        var muzzle = HullWeaponPositions.Compute(hull, cvh)[row.Mount];
        var origin = new Vector3(muzzle.X, muzzle.Y, muzzle.Z);
        float length = hull.Length * .23f;
        var expected = LoadoutV2Hardpoints.ScreenPoint(origin + direction * length,
            camera.World * camera.View * camera.Projection, rect);
        Assert.Equal(expected, marker.AxisEnd);
        var up = MathF.Abs(Vector3.Dot(direction, Vector3.Up)) < .9f ? Vector3.Up : Vector3.UnitZ;
        var right = Vector3.Normalize(Vector3.Cross(direction, up));
        // acos(.5) = 60 degrees: axial=.5L, radial=sqrt(.75)L.
        var rim = origin + direction * (length * .5f) + right * (length * MathF.Sqrt(.75f));
        Assert.Equal(LoadoutV2Hardpoints.ScreenPoint(rim, camera.World * camera.View * camera.Projection, rect), marker.Arc[0]);
        Assert.NotEqual(marker.Point, marker.AxisEnd);
        Assert.False(projection.Project(row, camera, rect, float.NaN)!.ArcLimitKnown);
        var fixedRow = model.SlotRows.First(s => s.Slot.Type == EquipmentType.Weapon && !s.IsTurret);
        Assert.Empty(projection.Project(fixedRow, camera, rect, .5f)!.Arc);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Occluded_mounts_are_retained_and_dim_state_changes_with_orbit()
    {
        Require.Artwork();
        var (hull, cvh, model) = Fixture("Cruiser");
        var projection = new LoadoutV2Hardpoints(hull, cvh);
        var markers = Enumerable.Range(0, 16).SelectMany(i => model.SlotRows
            .Where(s => s.Slot.Type == EquipmentType.Weapon)
            .Select(s => projection.Project(s, Camera(hull, i * MathHelper.TwoPi / 16), new(0, 0, 900, 600))!)).ToArray();
        Assert.All(markers, m => Assert.NotNull(m));
        Assert.Contains(markers, m => m.Occluded);
        Assert.Contains(markers, m => !m.Occluded);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void A_real_origin_frame_is_not_confused_with_a_missing_frame()
    {
        Require.Artwork();
        var (hull, cvh, model) = Fixture("Fighter");
        var frames = new CvhFile { Radius = cvh.Radius, EllipseEquation = cvh.EllipseEquation,
            EllipseRadiusMultiplier = cvh.EllipseRadiusMultiplier, Hulls = [],
            Frames = [new(hull.Hardpoints[0].FrameName, CVector.Zero, new(0, 0, 1))] };
        var marker = new LoadoutV2Hardpoints(hull, frames).Project(model.SlotRows[0], Camera(hull), new(0, 0, 900, 600));
        Assert.NotNull(marker);
        Assert.Equal(new Vector2(450, 300), marker.Point);
    }
}
