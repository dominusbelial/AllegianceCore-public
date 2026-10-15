using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P694StationLaunchTests
{
    private sealed class CentreRandom : Random
    {
        public override double NextDouble() => .5;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_slots_rotate_all_six_with_rapid_launch_offset_jitter_and_reset_turn_rates()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var m = Mission.Assemble(core, map, [new SideSpec(0, "A", 1), new SideSpec(1, "B", 3)], new NullIgcSite());
        var sector = m.Sectors[0];
        var station = m.Sectors.SelectMany(s => s.StationRuntimes).First();
        var hull = core.HullTypes.Values.First(h => h.Length > 0);
        var ship = new Ship(30000, hull, Vector3.Zero, Vector3.Zero, new Orientation());
        m.PlaceShip(ship, sector);
        var slots = Enumerable.Range(0, 6).Select(i => new HullLaunchSlot(new Vector3(i * 100, 0, 0),
            new Vector3(0, 1, 0))).ToArray();
        var bay = new HullLandingBayGeometry { Scale = 1, LaunchSlots = slots, LandSlots = [] };
        var orientation = station.Orientation;
        float speed = core.Constants.FloatConstants[4];
        var forward = slots[0].Direction * orientation;
        for (int i = 0; i < 8; i++)
        {
            for (int axis = 0; axis < 3; axis++) ship.SetTurnRate(axis, 2);
            double now = i * .05;
            station.Launch(ship, bay, now, new CentreRandom());
            var expected = station.Center + slots[i % 6].Position * orientation
                + forward * (hull.Length * .5f + speed * (i == 0 ? .5f : .85f) + 5);
            Assert.Equal(expected, ship.Position);
            Assert.Equal(forward * speed, ship.Velocity);
            Assert.Equal(now, ship.LastLaunchTime);
            Assert.All(ship.TurnRates, rate => Assert.Equal(0, rate));
            Assert.True((ship.Orientation.GetForward() - forward).Length() < .00001);
        }
        station.Launch(ship, bay, 1, new CentreRandom()); // isolated launch -> .5 offset
        var isolated = station.Center + slots[2].Position * orientation
            + forward * (hull.Length * .5f + speed * .5f + 5);
        Assert.Equal(isolated, ship.Position);
        station.Launch(ship, bay, 1.05, new Random(694));
        var basePosition = station.Center + slots[3].Position * orientation
            + forward * (hull.Length * .5f + speed * .85f + 5);
        var jitter = ship.Position - basePosition;
        Assert.InRange(jitter.X, -5, 5); Assert.InRange(jitter.Y, -5, 5); Assert.InRange(jitter.Z, -5, 5);
        Assert.NotEqual(Vector3.Zero, jitter);
        var fallback = new Station(station.Data, station.Type);
        var right = orientation.GetRight(); var up = orientation.GetUp();
        var directions = new[] { right, up, -right, -up, right, right, up };
        foreach (var direction in directions)
        {
            fallback.Launch(ship, null, 2, new CentreRandom());
            Assert.Equal(fallback.Center + direction * (fallback.Radius + hull.Length * .5f + speed * .5f + 5), ship.Position);
            Assert.Equal(direction * speed, ship.Velocity);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_station_launch_frames_scale_by_radius()
    {
        Require.Artwork();
        var model = CvhReader.Parse(File.ReadAllText(Path.Combine(TestArtRoot.Value, "ss24a.cvh")));
        var a = HullLandingBay.ReconstructStation(model, 200);
        var b = HullLandingBay.ReconstructStation(model, 400);
        Assert.NotEmpty(a.LaunchSlots);
        Assert.Equal(a.LaunchSlots.Count, b.LaunchSlots.Count);
        for (int i = 0; i < a.LaunchSlots.Count; i++)
        {
            Assert.Equal(a.LaunchSlots[i].Position * 2, b.LaunchSlots[i].Position);
            Assert.Equal(a.LaunchSlots[i].Direction, b.LaunchSlots[i].Direction);
        }
        // Full fallback behavior is also exercised through the real server's
        // existing no-artificial-launch-slot undock/ripcord/NPC tests.
    }
}
