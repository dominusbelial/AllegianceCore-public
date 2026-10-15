using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P699StationBayTests
{
    [Fact]
    public void Station_appends_fighter_bays_after_capital_prefix_using_native_slot_numbers()
    {
        var model = CvhReader.Parse("""
            10 10 10 10 1
            0
            capgarage1A
            1 2 3
            0 0 -1
            garage1A
            99 0 0
            0 0 -1
            garage2A
            4 5 6
            0 0 -1
            """);
        var bay = HullLandingBay.ReconstructStation(model, 20);
        Assert.Equal(1, bay.CapLandSlots);
        Assert.Equal(2, bay.LandSlots.Count);
        Assert.Equal(new Vector3(-2, 4, 6), bay.LandSlots[0].Planes[0].Position);
        Assert.Equal(new Vector3(-8, 10, 12), bay.LandSlots[1].Planes[0].Position);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Real_garrison_has_fighter_bays_but_no_capital_bays_and_center_is_not_a_bay()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var type = core.StationTypes[15];
        var cache = new StationLandingBayCache(n => File.Exists(Path.Combine(art, n + ".cvh"))
            ? CvhReader.Parse(File.ReadAllText(Path.Combine(art, n + ".cvh"))) : null);
        var bay = cache.Bay(type)!;
        Assert.NotNull(bay);
        Assert.Equal(2, bay.LandSlots.Count);
        Assert.Equal(0, bay.CapLandSlots);
        Assert.Same(bay, cache.Bay(type));
        var scaled = cache.Bay(type with { Radius = type.Radius / 2 })!;
        Assert.Equal(bay.Scale / 2, scaled.Scale);
        Assert.NotSame(bay, scaled);
        Assert.Null(cache.Bay(type with { ModelName = "P699_MISSING_MODEL" }));
        Assert.Same(bay, cache.Bay(type));
        var station = new Station(new([0, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 1],
            0, 0, 1, type.StationTypeId, 100, 100, "Garrison"), type);
        var hull = core.HullTypes[444];
        Assert.True(hull.HasCapability(DataHullTypeIGC.HabmFighter));
        var pod = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.False(station.InGarage(pod, Vector3.Zero, bay));
        // Interior of ss24a's first garage, derived from its five frame planes.
        var entry = bay.LandSlots[0].Planes[0];
        var inside = entry.Position + entry.Direction.Normalize() * 20;
        Assert.True(station.InGarage(pod, inside, bay));
        var capital = new Ship(2, hull with { HabmCapabilities = DataHullTypeIGC.HabmLifepod },
            inside, Vector3.Zero, Orientation.Identity);
        Assert.False(station.InGarage(capital, inside, bay));
    }

    private sealed class DockingSite : IIgcSite
    {
        public bool DockWithStationEvent(IShipIgc ship, IStationIgc station)
        {
            ((Ship)ship).StationId = ((DockStation)station).Station.StationId;
            return true;
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Bay_entry_halfspace_does_not_dock_a_pod_far_outside_station_collision_range()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var mission = Mission.Assemble(core, MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "XXV_simple.igc"))),
            [new SideSpec(0, "Alpha", 25), new SideSpec(1, "Bravo", 3)], new DockingSite());
        var sector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == 0));
        sector.StationLandingBays = new StationLandingBayCache(n => CvhReader.Parse(File.ReadAllText(Path.Combine(art, n + ".cvh"))));
        var station = sector.StationRuntimes.First(st => st.SideId == 0);
        var bay = sector.StationLandingBays.Bay(station.Type)!;
        var entry = bay.LandSlots[0].Planes[0];
        var far = station.Center + (entry.Position + entry.Direction.Normalize() * 5000) * station.Orientation;
        var pod = new Ship(31000, core.HullTypes[444], far, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(pod, sector);
        Assert.True(station.InGarage(pod, far, bay), "the bay's entry half-space extends into space");
        sector.Update(.05);
        Assert.Null(pod.StationId);
        pod.Position = station.Center + (entry.Position + entry.Direction.Normalize() * 20) * station.Orientation;
        sector.Update(.1);
        Assert.Equal(station.StationId, pod.StationId);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(115)]
    public void Pod_enters_real_bay_from_oblique_cruise_before_native_oxygen_deadline(short typeId)
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var hull = core.HullTypes[444];
        var type = core.StationTypes[typeId];
        var bay = HullLandingBay.ReconstructStation(CvhReader.Parse(File.ReadAllText(Path.Combine(art, type.ModelName + ".cvh"))), type.Radius);
        var station = new Station(new([0, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 1],
            0, 0, 1, typeId, 100, 100, "Garrison"), type);
        var direction = new Vector3(1, 1, 1).Normalize();
        var orient = new Orientation(); orient.Set(-direction, new Vector3(0, 1, 0));
        var pod = new Ship(1, hull, direction * 2500, -direction * hull.Speed, orient);
        var bays = bay.LandSlots.Select(s => new DockApproach.DockBay(s.Planes[0].Position, s.Planes[0].Direction.Normalize())).ToArray();
        for (double t = 0; t < core.Constants!.FloatConstants[31]; t += .05)
        {
            float speed = pod.Velocity.Length();
            float rest = speed * speed / (2 * hull.Thrust / hull.Mass);
            var restPosition = pod.Position + pod.Velocity * (speed > .1f ? rest / speed : 0);
            var step = DockApproach.DockStation(pod.Position, pod.Velocity, pod.Orientation, Vector3.Zero,
                bays, DockApproach.StationBaysForClass(true, bays, bay.CapLandSlots), rest, restPosition,
                type.Radius + hull.Length / 2 + 10, 0, 0, false);
            pod.Controls = DockApproach.Steer(step, pod.Position, pod.Orientation, pod.Velocity, rest);
            pod.ExecuteMove(t, t + .05); pod.Position += pod.Velocity * .05f;
            if (station.InGarage(pod, pod.Position, bay)) return;
        }
        Assert.Fail($"pod never entered garrison type {typeId}'s bay within native endurance");
    }
}
