using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>
/// P1067 — the browser "Watch" camera: the desktop director snapshot rebuilt
/// from the P1065 sector index and visibility gate, the cancel-on-input rule,
/// sector-change re-acquisition, and the docked / no-subject rules.
/// </summary>
public sealed class P1067WatchTests
{
    private static readonly DataHullTypeIGC Hull = default(DataHullTypeIGC) with
    { HullId = 1, ModelName = "fig21", Length = 24, Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8] };

    private static ServerShipUpdate Pose(short id, float x = 100) => ServerShipUpdate.FromModel(id,
        new Ship(id, Hull, new SimVec(x, 0, 0), SimVec.Zero, Orientation.Identity), 100);

    private static Ship At(short id, float x, float hullFraction = 1f)
    {
        var ship = new Ship(id, Hull, new SimVec(x, 0, 0), SimVec.Zero, Orientation.Identity);
        ship.SetFraction(hullFraction);
        return ship;
    }

    /// <summary>Own ship 1 (side 0, sector 1) plus the given remote ships, all
    /// delivered through the real wire paths (HEAVY_SHIPS_UPDATE for poses).</summary>
    private static ClientWorld World(params (short Id, short Side, Ship Ship)[] remotes)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0, CharacterName = "Pilot" });
        world.Apply(new PlayerInfo { ShipId = 1, ISide = 0, CharacterName = "Pilot" });   // MySideId
        foreach (var (id, side, _) in remotes)
            world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = side, CharacterName = "Ship" + id });
        if (remotes.Length > 0)
            world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero, remotes.Select(r => r.Ship).ToArray()));
        return world;
    }

    private static void Set(ShipEntity ship, string property, object? value)
        => typeof(ShipEntity).GetProperty(property)!.SetValue(ship, value);

    private static DirectorSnapshot Build(DirectorWatch watch, ClientWorld world, float x = 100)
        => watch.BuildSnapshot(world, new Vector3(x, 0, 0));

    private static (Matrix View, Matrix Proj, Vector3 Eye, bool ShotChanged) Advance(
        DirectorWatch watch, DirectorSnapshot snapshot, double dt = 0.5)
        => watch.Update(dt, snapshot, 12f, 24f, 16f / 9f);

    [Fact]
    public void Snapshot_frames_visible_sector_ships_with_desktop_interest_and_engagement_midpoints()
    {
        var world = World((2, 1, At(2, 200f, 0.5f)), (3, 0, At(3, 1000f)));
        var watch = new DirectorWatch(7);
        var snapshot = Build(watch, world);

        var ships = watch.Subjects.Where(s => s.Category == SubjectCategory.Ship).ToArray();
        Assert.Equal(new[] { "Ship2", "Ship3" }, ships.Select(s => s.Label).OrderBy(l => l, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(watch.Subjects, s => s.Label == "Pilot");   // the own parked ship is never a subject
        // Desktop weight: 20 + (1 - hull) * 60 (Game1.Director.cs:409).
        Assert.Equal(DirectorWatch.ShipInterestBase + 0.5f * DirectorWatch.ShipInterestHull, ships[0].Interest);
        // Opposing pair 2 <-> 3 is 800 units apart: an Engagement midpoint at 600.
        var midpoint = Assert.Single(watch.Subjects, s => s.Category == SubjectCategory.Engagement
            && Vector3.Distance(s.Position, new Vector3(600f, 0f, 0f)) < 1f);
        Assert.Equal(DirectorWatch.EngagementInterestBase, midpoint.Interest);   // paired from Ship3's hull (1.0)
        // Desktop parity quirk kept deliberately: the enemy ship pairs with itself
        // (distance 0 < 1500), producing one Engagement at its own position.
        Assert.Equal(2, watch.Subjects.Count(s => s.Category == SubjectCategory.Engagement));
    }

    [Fact]
    public void Snapshot_engagement_needs_the_desktop_1500_unit_range()
    {
        var world = World((2, 1, At(2, 200f)), (3, 1, At(3, 2000f)));
        var watch = new DirectorWatch(7);
        Build(watch, world);
        // Only the two self-pairs remain; the 1800-unit gap adds no midpoint.
        Assert.Equal(2, watch.Subjects.Count(s => s.Category == SubjectCategory.Engagement));
        Assert.DoesNotContain(watch.Subjects, s => s.Category == SubjectCategory.Engagement
            && s.Position == new Vector3(1100f, 0f, 0f));
    }

    [Theory]
    [InlineData("IsDead")]
    [InlineData("IsGone")]
    [InlineData("Unknown")]
    [InlineData("ParentId")]
    [InlineData("State")]
    [InlineData("StationId")]
    [InlineData("PoseSectorId")]
    [InlineData("SectorId")]
    [InlineData("Latest")]
    public void Snapshot_uses_the_stricter_p1065_visibility_gate(string field)
    {
        var world = World((2, 1, At(2, 200f)));
        var other = world.Ships[2];
        Set(other, field, field switch
        {
            "ParentId" => (short)1, "State" => ShipStatusMessage.StateDocked,
            "StationId" => (short)4, "PoseSectorId" or "SectorId" => (short)9,
            "Latest" => null, _ => true,
        });
        var watch = new DirectorWatch(7);
        Build(watch, world);

        Assert.DoesNotContain(watch.Subjects, s => s.Category == SubjectCategory.Ship);
        // The same roster BattleShipScene hands the contact/hull pass.
        var scene = new BattleShipScene(new Dictionary<short, DataHullTypeIGC> { [1] = Hull });
        scene.Gather(world, 1, null);
        Assert.Equal(new short[] { 1 }, scene.Ships.Select(s => s.ShipId));   // only the own ship remains
    }

    [Fact]
    public void Explosion_subjects_score_recency_the_desktop_way()
    {
        var world = World();
        // ClientWorld stamps ReceivedLocalMs from NowSeconds, so shifting the clock
        // fixes the age the snapshot's TickCount64 reading sees.
        double now = Environment.TickCount64 / 1000.0;
        world.NowSeconds = () => now - 12;
        world.Apply(new ExplosionEvent
        {
            Type = ExplosionType.SmallShip, ClusterId = 1,
            Position = new SimVec(500, 0, 0), Velocity = SimVec.Zero,
        });
        world.NowSeconds = () => now - 1;
        world.Apply(new ExplosionEvent
        {
            Type = ExplosionType.SmallShip, ClusterId = 1,
            Position = new SimVec(-500, 0, 0), Velocity = SimVec.Zero,
        });
        world.NowSeconds = () => now - 12;
        world.Apply(new ExplosionEvent
        {
            Type = ExplosionType.SmallShip, ClusterId = 9,   // another sector: never filmed
            Position = new SimVec(0, 500, 0), Velocity = SimVec.Zero,
        });

        var watch = new DirectorWatch(7);
        Build(watch, world);
        var explosions = watch.Subjects.Where(s => s.Category == SubjectCategory.Explosion).ToArray();
        Assert.Equal(2, explosions.Length);
        float recent = explosions.Single(s => s.Position.X < 0).Interest;
        float old = explosions.Single(s => s.Position.X > 0).Interest;
        Assert.Equal(DirectorWatch.ExplosionInterestMin, old);                 // 12 s old -> the 40 floor
        Assert.InRange(recent, DirectorWatch.ExplosionInterestMin, DirectorWatch.ExplosionInterestMax);
        Assert.True(recent > old);
    }

    [Fact]
    public void Station_and_aleph_subjects_are_added_and_the_cap_is_24()
    {
        var world = World();
        world.Apply(new ExportStation
        {
            Station = new DataStationIGC([300f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 1f],
                ClusterId: 1, SideId: 0, StationId: 4, StationTypeId: 1, BpHull: 240, BpShield: 240, Name: "Garrison"),
        });
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(WarpId: 5, DestinationId: 6, Radius: 100, TextureName: "aleph", IconName: "aleph",
                Name: "Aleph", Position: [-300f, 0f, 0f], Forward: [0f, 0f, 1f], Rotation: [0f, 0f, 0f, 1f],
                Signature: 1f, ClusterId: 1),
        });
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(WarpId: 7, DestinationId: 8, Radius: 100, TextureName: "aleph", IconName: "aleph",
                Name: "Far aleph", Position: [-300f, 0f, 0f], Forward: [0f, 0f, 1f], Rotation: [0f, 0f, 0f, 1f],
                Signature: 1f, ClusterId: 9),
        });
        var watch = new DirectorWatch(7);
        Build(watch, world);
        Assert.Single(watch.Subjects, s => s.Category == SubjectCategory.Station);
        Assert.Single(watch.Subjects, s => s.Category == SubjectCategory.Warp);
        Assert.Equal(DirectorWatch.StationInterest, watch.Subjects.Single(s => s.Category == SubjectCategory.Station).Interest);
        Assert.Equal(DirectorWatch.WarpInterest, watch.Subjects.Single(s => s.Category == SubjectCategory.Warp).Interest);
    }

    [Fact]
    public void Twenty_four_subject_cap_keeps_the_first_candidates()
    {
        var remotes = Enumerable.Range(2, 30).Select(id => ((short)id, (short)0, At((short)id, id * 50f))).ToArray();
        var world = World(remotes);
        var watch = new DirectorWatch(7);
        Build(watch, world);
        Assert.Equal(DirectorWatch.SubjectCap, watch.Subjects.Count);
        Assert.All(watch.Subjects, s => Assert.Equal(SubjectCategory.Ship, s.Category));
    }

    [Fact]
    public void Sector_change_forces_a_re_acquisition_on_that_frame()
    {
        var world = World((2, 1, At(2, 200f)));
        var watch = new DirectorWatch(11);
        watch.Start(11);

        var first = Build(watch, world);
        Assert.True(first.SectorChanged);        // first snapshot from -1
        Advance(watch, first);
        int changes = watch.Planner.ShotChanges;
        int subject = watch.Planner.SubjectIndex;

        Assert.False(Build(watch, world).SectorChanged);
        Advance(watch, Build(watch, world));
        Assert.Equal(changes, watch.Planner.ShotChanges);   // 0.5 s of a 4-8 s dwell: no new shot

        // The pilot warps: the own ship's sector is now 9, with a ship there.
        world.Apply(new ShipStatusMessage { ShipId = 1, HullId = 1, SectorId = 9, StationId = -1, ParentId = -1, StateTime = 300 });
        world.Apply(new ExportShip { ShipId = 3, HullId = 1, SideId = 1, CharacterName = "Ship3" });
        world.Apply(HeavyShipsUpdate.FromShips(301, SimVec.Zero, [At(3, 400f)]));

        var changed = Build(watch, world);
        Assert.True(changed.SectorChanged);
        Advance(watch, changed);
        Assert.True(watch.Planner.ShotChanges > changes);   // re-acquired on the change frame
        Assert.InRange(watch.Planner.SubjectIndex, 0, changed.Subjects.Count - 1);
        Assert.DoesNotContain(watch.Subjects, s => s.Label == "Ship2");   // the old sector is gone
    }

    [Fact]
    public void Docked_pilot_films_the_station_and_reports_the_dock_transition()
    {
        var world = World();
        world.Apply(new ExportStation
        {
            Station = new DataStationIGC([300f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 1f],
                ClusterId: 1, SideId: 0, StationId: 4, StationTypeId: 1, BpHull: 240, BpShield: 240, Name: "Garrison"),
        });
        var watch = new DirectorWatch(3);
        var snapshot = Build(watch, world);
        Assert.False(snapshot.Docked);

        Set(world.Ships[1], "StationId", (short)4);   // the server docked the pilot
        var docked = Build(watch, world);
        Assert.True(docked.Docked);
        Assert.True(docked.SectorChanged);            // dock counts as a transition

        Advance(watch, docked);
        Assert.Equal(ShotKind.Station, watch.Planner.Kind);
        Assert.Equal(SubjectCategory.Station, docked.Subjects[watch.Planner.SubjectIndex].Category);
    }

    [Fact]
    public void No_subjects_keeps_filming_the_own_ship_instead_of_stalling()
    {
        var world = World();
        var watch = new DirectorWatch(5);
        var snapshot = Build(watch, world);
        Assert.Empty(snapshot.Subjects);

        var (view, proj, eye, changed) = Advance(watch, snapshot);
        Assert.True(changed);
        Assert.Equal(-1, watch.Planner.SubjectIndex);   // chase the parked ship
        Assert.False(float.IsNaN(eye.X) || float.IsNaN(eye.Y) || float.IsNaN(eye.Z));
        Assert.True(float.IsFinite(view.M11) && float.IsFinite(view.M22) && float.IsFinite(view.M33));
        Assert.True(float.IsFinite(proj.M11) && float.IsFinite(proj.M22) && float.IsFinite(proj.M33));

        int changes = watch.Planner.ShotChanges;
        Advance(watch, snapshot, dt: 10);               // past any dwell: the cycle keeps moving
        Assert.True(watch.Planner.ShotChanges > changes);
    }

    [Theory]
    [InlineData("ThrustForward", Keys.W)]
    [InlineData("ThrustBackward", Keys.S)]
    [InlineData("ThrustLeft", Keys.Q)]
    [InlineData("ThrustRight", Keys.E)]
    [InlineData("RollLeft", Keys.A)]
    [InlineData("RollRight", Keys.D)]
    [InlineData("YawLeft", Keys.Left)]
    [InlineData("YawRight", Keys.Right)]
    public void Any_flight_control_cancels_watch_like_the_desktop(string action, Keys key)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var watch = new DirectorWatch(9);
        watch.Start(9);
        Assert.True(map.IsDownMostSpecific(new KeyboardState(key), action));

        Assert.True(watch.CancelOnFlightInput(new KeyboardState(key), map.IsDownMostSpecific));
        Assert.False(watch.Active);
        // Once cancelled, no further frame reports a cancellation.
        Assert.False(watch.CancelOnFlightInput(new KeyboardState(key), map.IsDownMostSpecific));
    }

    [Fact]
    public void Fire_and_throttle_keys_do_not_cancel_matching_the_desktop_predicate()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var watch = new DirectorWatch(9);
        watch.Start(9);

        Assert.False(watch.CancelOnFlightInput(new KeyboardState(Keys.Space), map.IsDownMostSpecific));   // fire
        Assert.False(watch.CancelOnFlightInput(new KeyboardState(Keys.Back), map.IsDownMostSpecific));    // throttle zero
        Assert.True(watch.Active);
    }

    [Fact]
    public void Same_seed_replays_the_same_shot_sequence()
    {
        var world = World((2, 1, At(2, 200f)), (3, 0, At(3, 300f)));
        var a = new DirectorWatch(4242); var b = new DirectorWatch(4242);
        a.Start(4242); b.Start(4242);
        var snapshotA = Build(a, world);
        var snapshotB = Build(b, world);
        for (int frame = 0; frame < 120; frame++)
        {
            var ea = Advance(a, snapshotA);
            var eb = Advance(b, snapshotB);
            Assert.Equal(a.Planner.Kind, b.Planner.Kind);
            Assert.Equal(a.Planner.SubjectIndex, b.Planner.SubjectIndex);
            Assert.Equal(a.Planner.ShotChanges, b.Planner.ShotChanges);
            Assert.Equal(ea.Eye, eb.Eye);
        }
        Assert.True(a.Planner.ShotChanges > 1);
    }

    [Fact]
    public void Director_drifts_its_own_orientation_and_never_mutates_the_ships()
    {
        var world = World((2, 1, At(2, 200f)));
        var shipOrientation = new Orientation();
        shipOrientation.Pitch(0.25f); shipOrientation.Yaw(0.5f);
        var beforeForward = shipOrientation.GetForward();
        var watch = new DirectorWatch(21);
        watch.Start(21, shipOrientation);

        DirectorSnapshot snapshot = Build(watch, world);
        var first = Advance(watch, snapshot);           // starts behind the ship
        for (int i = 0; i < 120; i++) Advance(watch, snapshot);
        var later = Advance(watch, snapshot);

        // The free orientation is a copy: the ship's own attitude is untouched.
        Assert.Equal(beforeForward.X, shipOrientation.GetForward().X, 4);
        Assert.Equal(beforeForward.Y, shipOrientation.GetForward().Y, 4);
        Assert.Equal(beforeForward.Z, shipOrientation.GetForward().Z, 4);
        // ...and it actually drifts, so orbit/backing shots are not static.
        Assert.NotEqual(first.Eye, later.Eye);
    }

    [Fact]
    public void Toggle_starts_with_the_seed_stops_and_restarts_cleanly()
    {
        var watch = new DirectorWatch();
        Assert.False(watch.Active);
        Assert.True(watch.Toggle(1234));
        Assert.True(watch.Active);
        Assert.Equal(1234, watch.Seed);
        Assert.False(watch.Toggle(999));
        Assert.False(watch.Active);
        Assert.True(watch.Toggle(999));
        Assert.Equal(999, watch.Seed);
        watch.Stop();
        Assert.False(watch.Active);
        Assert.Equal(DirectorWatch.HudLabel, "WATCH");
    }

    [Fact]
    public void Watch_hud_tag_uses_the_desktop_camera_label_both_styles_draw()
    {
        var model = HudModelAdapter.Create(null, CameraMode.Chase, cameraLabel: DirectorWatch.HudLabel);
        Assert.Equal("WATCH", model.CameraLabel);
    }
}