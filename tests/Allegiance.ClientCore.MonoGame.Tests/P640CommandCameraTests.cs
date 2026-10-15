using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P640CommandCameraTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_native_orientations_frame_focus_with_a_rotation_only_view(bool topDown)
    {
        var camera = new CommandCamera { TopDown = topDown };
        var center = new Vector3(9000, -3000, 2000);
        camera.Center(center, 40);
        var (view, projection, eye) = camera.Matrices(16f / 9);
        Assert.Equal(Vector3.Zero, view.Translation);
        Assert.Equal(6000, (eye - center).Length(), 2);
        var clip = Vector4.Transform(new Vector4(center - eye, 1), view * projection);
        Assert.True(clip.W > 0);
        Assert.InRange(MathF.Abs(clip.X / clip.W), 0, .0001f);
        Assert.InRange(MathF.Abs(clip.Y / clip.W), 0, .0001f);
        if (topDown) Assert.Equal(Vector3.UnitZ, Vector3.Normalize(eye - center));
        else
        {
            var backward = Vector3.Normalize(eye - center);
            Assert.InRange(backward.Y, -.708f, -.707f);
            Assert.InRange(backward.Z, .707f, .708f);
        }
    }

    [Fact]
    public void Pan_stays_in_sector_plane_roll_rotates_axes_zoom_and_focus_are_bounded()
    {
        var camera = new CommandCamera();
        camera.Center(Vector3.Zero, 10);
        camera.Step(.1f, 1, 1, 0, 0);
        Assert.Equal(600, camera.Focus.X, 3);
        Assert.Equal(-600, camera.Focus.Y, 3);
        Assert.Equal(0, camera.Focus.Z);
        camera.Step(100, 10, 0, 10, 1);
        Assert.Equal(CommandCamera.MaxDistance, camera.Distance);
        Assert.InRange(camera.Focus.Length(), 0, 20000.01f);
        camera.Step(100, 0, 0, -10, 0);
        Assert.Equal(CommandCamera.MinDistance, camera.Distance);
        Assert.True(MathF.Abs(camera.Roll) <= MathF.PI);
        camera.Center(Vector3.Zero, 100);
        Assert.Equal(205, camera.Distance);
        camera.Center(Vector3.Zero, 0, explicitPosition: true);
        Assert.Equal(10000, camera.Distance);
    }

    [Fact]
    public void Tracking_preserves_pan_offset_and_obeys_target_radius()
    {
        var camera = new CommandCamera();
        camera.Center(Vector3.Zero, 1);
        camera.Step(.1f, 1, 0, 0, 0);
        camera.Follow(new Vector3(50, 60, 70), 5000);
        Assert.Equal(new Vector3(600, 0, 0), camera.Focus); // tracking off
        camera.Tracking = true;
        camera.Follow(new Vector3(50, 60, 70), 5000);
        Assert.Equal(new Vector3(650, 60, 70), camera.Focus);
        Assert.Equal(10005, camera.Distance);
    }

    [Fact]
    public void Roll_rotates_native_world_Z_plane_and_preserves_three_quarter_elevation()
    {
        var camera = new CommandCamera();
        camera.Center(Vector3.Zero, 1);
        camera.Step(MathF.PI / 2, 0, 0, 0, 1);
        Assert.InRange(camera.Eye.X / camera.Distance, .707f, .708f);
        Assert.InRange(MathF.Abs(camera.Eye.Y), 0, .001f);
        Assert.InRange(camera.Eye.Z / camera.Distance, .707f, .708f);
        Assert.Equal(0, camera.Right.Z);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("commandtd")]
    [InlineData("dronefollow")]
    [InlineData("dronecockpit")]
    public void Proof_flags_select_production_camera_and_real_sector_highlight(string mode)
    {
        var plan = Allegiance.ClientCore.MonoGame.LiveProof.AutoProofPlan.Parse(["--camera", mode, "--auto-highlight", "current"], out _);
        Assert.Equal(mode, plan.Camera);
        Assert.Equal("current", plan.AutoHighlightSector);
        Assert.True(plan.AnyActive);
    }

    [Fact]
    public void Command_rectangle_selects_only_friendly_visible_ships_with_reversed_corners_and_edge_overlap()
    {
        var candidates = new Game1.CandidateObject[]
        {
            new(new SimVec(.1f, .1f, 0), Game1.TargetKind.Ship, 2, "wingman", 0, 0),
            new(new SimVec(.21f, .1f, 0), Game1.TargetKind.Ship, 3, "edge", 0, 0),
            new(new SimVec(.1f, .1f, 0), Game1.TargetKind.Ship, 4, "enemy", 1, 0),
            new(new SimVec(.1f, .1f, 0), Game1.TargetKind.Station, 5, "station", 0, 0),
            new(new SimVec(.8f, .1f, 0), Game1.TargetKind.Ship, 6, "outside", 0, 0),
            new(new SimVec(.1f, .1f, -1), Game1.TargetKind.Ship, 7, "behind", 0, 0),
            new(new SimVec(.1f, .1f, 0), Game1.TargetKind.Ship, 1, "self", 0, 0),
        };
        var selected = Game1.ResolveCommandRectangle(candidates, p => p.Z < 0 ? null : (p.X, p.Y),
            new Vector2(.2f, .2f), Vector2.Zero, 0, 1);
        Assert.Equal(new short[] { 2, 3 }, selected.Select(s => s.Id));
    }

    [Fact]
    public void Command_wheel_zoom_is_bounded_and_does_not_change_camera_focus()
    {
        var camera = new CommandCamera();
        camera.Center(new Vector3(1, 2, 3), 1);
        camera.ZoomWheel(1);
        Assert.Equal(4800, camera.Distance, 2);
        camera.ZoomWheel(-1);
        Assert.Equal(5760, camera.Distance, 2);
        camera.ZoomWheel(1000);
        Assert.Equal(CommandCamera.MinDistance, camera.Distance);
        camera.ZoomWheel(-1000);
        Assert.Equal(CommandCamera.MaxDistance, camera.Distance);
        Assert.Equal(new Vector3(1, 2, 3), camera.Focus);
    }

    [Fact]
    public void Grid_matches_native_disk_major_lines_and_height_drop_feet_camera_relative()
    {
        var eye = new Vector3(3000, 4000, 5000);
        var position = new Vector3(100, 200, 50);
        var geometry = CommandGridRenderer.Geometry([position], eye);
        Assert.Equal(4 * 41 + 6, geometry.Length);
        foreach (var vertex in geometry.Take(4 * 41))
        {
            var absolute = vertex.Position + eye;
            Assert.Equal(0, absolute.Z);
            Assert.InRange(absolute.Length(), 0, 7500.01f);
        }
        Assert.Equal(new Color(0, 0, 128), geometry[0].Color);
        Assert.Equal(new Color(0, 0, 64), geometry[4].Color);
        Assert.Equal(position - eye, geometry[164].Position);
        Assert.Equal(new Vector3(100, 200, 0) - eye, geometry[165].Position);
        Assert.Equal(new Color(200, 180, 20), geometry[164].Color);
        Assert.Equal(30, (geometry[166].Position - geometry[167].Position).Length());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_radar_bracket_projects_with_the_scene_eye(bool topDown)
    {
        var camera = new CommandCamera { TopDown = topDown };
        var focus = new Vector3(1000, 2000, 3000);
        camera.Center(focus, 10);
        var (view, projection, eye) = camera.Matrices(1280f / 720);
        var bracket = RadarProjection.Project(focus, eye, view, projection, 1280, 720, 14);
        Assert.Equal(RadarBracketLayout.RadarPlacement.OnScreen, bracket.Placement);
        Assert.Equal(640, bracket.Point.X, 3);
        Assert.Equal(360, bracket.Point.Y, 3);
    }

    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        foreach (short id in new short[] { 2, 3 })
        {
            world.Apply(new ExportShip { ShipId = id, SideId = 0 });
            world.Apply(new ShipStatusMessage { ShipId = id, ParentId = -1, StationId = -1, SectorId = 1 });
            world.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.FromValues(id, 0, default, 1,
                new SimVec(100, 200, 300), Allegiance.Core.Sim.Orientation.Identity, SimVec.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        }
        return world;
    }

    [Fact]
    public void Follow_resolves_source_carrier_and_rejects_enemy_unknown_dead_wrong_sector_and_missing_pose()
    {
        var world = World();
        Assert.Equal(2, CommandCamera.FollowSubject(world, 2, 1)!.ShipId);
        Assert.Null(CommandCamera.FollowSubject(world, 2, 9));
        Assert.Null(CommandCamera.FollowSubject(world, 99, 1));
        world.Apply(new ShipStatusMessage { ShipId = 2, ParentId = 3, SectorId = 1, State = ShipStatusMessage.StateTurret });
        Assert.Equal(3, CommandCamera.FollowSubject(world, 2, 1)!.ShipId);
        world.Apply(new ShipStatusMessage { ShipId = 3, ParentId = -1, SectorId = 1, Unknown = true });
        Assert.Null(CommandCamera.FollowSubject(world, 2, 1));
        world = World();
        world.Apply(new ExportShip { ShipId = 2, SideId = 1 });
        Assert.Null(CommandCamera.FollowSubject(world, 2, 1));
        Assert.NotNull(CommandCamera.FollowSubject(world, 2, 1, sameSideOnly: false));
        world = World();
        world.Apply(new ShipStatusMessage { ShipId = 2, ParentId = 99, SectorId = 1 });
        Assert.Null(CommandCamera.FollowSubject(world, 2, 1));
        world = World();
        world.Apply(new KillShip { ShipId = 2, BDeathCredit = true });
        Assert.Null(CommandCamera.FollowSubject(world, 2, 1));
        world.Apply(new ExportShip { ShipId = 4, SideId = 0 });
        Assert.Null(CommandCamera.FollowSubject(world, 4, 1));
    }

    [Theory]
    [InlineData("ViewCommand", "FollowCam", Keys.F3, Keys.LeftControl)]
    [InlineData("OpenKeyBindings", "ToggleCommand", Keys.F6, Keys.LeftShift)]
    [InlineData("FreeCam", "DroneFollow", Keys.F8, Keys.LeftControl)]
    [InlineData("OrbitCam", "DroneCockpit", Keys.F9, Keys.LeftControl)]
    [InlineData("ViewTarget", "TrackCommandView", Keys.F7, Keys.LeftControl)]
    public void Modified_commands_do_not_also_open_the_plain_F_key_action(string plain, string modified, Keys key, Keys modifier)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.True(map.IsDownMostSpecific(new KeyboardState(key), plain));
        Assert.False(map.IsDownMostSpecific(new KeyboardState(key, modifier), plain));
        Assert.True(map.IsDownMostSpecific(new KeyboardState(key, modifier), modified));
        Assert.Null(map.SetBinding(modified, new KeyBinding(Keys.F24, InputModifiers.None)));
        Assert.True(map.IsDownMostSpecific(new KeyboardState(Keys.F24), modified));
        Assert.False(map.IsDownMostSpecific(new KeyboardState(key, modifier), modified));
        Assert.Contains(map.ToHelpGroups().SelectMany(g => g.Entries), e => e.Key == "F24");
    }

    [Theory]
    [InlineData(0, -.6666667f)]
    [InlineData(8, 0)]
    [InlineData(50, 0)]
    [InlineData(99, .5833333f)]
    public void Edge_pan_uses_native_gutter(int cursor, float expected) =>
        Assert.Equal(expected, Game1.CommandEdgePan(cursor, 100), 5);
}
