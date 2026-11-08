using Allegiance.Core;
using Allegiance.ClientCore.Flight;
using Allegiance.TestInfra;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit.Abstractions;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P958SimulationRandomTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Production_mission_retains_ambient_random()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.FromLists([], [], [], []);
        Assert.Same(Random.Shared, Mission.Assemble(core, map, [], new ServerHost()).Random);
        var random = new Random(958);
        Assert.Same(random, Mission.Assemble(core, map, [], new ServerHost(), random: random).Random);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Homed_pod_applies_native_dodge_before_station_seek_near_arrival_aleph()
    {
        Require.Artwork();
        using var server = new Allegiance.Server.Server
        {
            SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(1),
        };
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P958 approach", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        var sector = mission.Sectors.Single(s => s.Id == 1050);
        var authored = sector.StationRuntimes.First(st => st.SideId == 0);
        var type = mission.Core.StationTypes[mission.Sides[0].Civilization.InitialStationTypeId];
        sector.RemoveStation(authored);
        var home = new Station(authored.Data with { StationTypeId = type.StationTypeId }, type);
        mission.PlaceStation(home, sector);
        // Sample seed 1's homed pod 20089 pose near the arrival mouth and
        // put approaching launch traffic on its path. Native first-priority
        // Dodge: common.cpp:1999-2020;
        // imminent-contact and away thrust: common.cpp:1693-1790.
        var velocity = new Vector3(38.89617f, -38.287033f, -3.1838913f);
        var orientation = new Orientation(); orientation.Set(velocity.Normalize());
        var pod = new Ship(20089, mission.Core.HullTypes[444],
            new(-1364.6183f, 4522.487f, 257.2976f), velocity, orientation) { SideId = 0 };
        mission.PlaceShip(pod, sector);
        var launchHull = mission.Core.HullTypes.Values.First(h => h.Speed > 0 && h.Length < 100
            && !h.HasCapability(DataHullTypeIGC.HabmLifepod) && !h.HasCapability(DataHullTypeIGC.HabmRescue));
        var traffic = new Ship(25034, launchHull, pod.Position + velocity.Normalize() * 150,
            velocity.Normalize() * -250, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(traffic, sector);
        var bays = sector.StationLandingBays!.Bay(home.Type)!.LandSlots.Select(s =>
            new DockApproach.DockBay(s.Planes[0].Position * home.Orientation,
                s.Planes[0].Direction.Normalize() * home.Orientation)).ToArray();
        var target = new NativeGotoPlan.Target(Ship.NativeKey(NativeGotoPlan.Kind.Station, home.StationId),
            sector.Id, NativeGotoPlan.Kind.Station, NativeGotoPlan.Objective.Enter, home.Center, home.Radius);
        var navigation = P699bPodLoadTests.NativeControls(pod, target, bays);
        Assert.True(navigation.Step.Has(DockApproach.Mask.DodgeShips));
        Assert.Equal(-1, navigation.Controls.Throttle);
        Assert.NotEqual(0, navigation.Buttons & ShipControlState.MoveButtonsMask);
        // The old harness consumed the station mask without running Dodge.
        float rest = DockApproach.StoppingDistance(velocity.Length(), pod.EffectiveThrust(), pod.Mass,
            pod.EffectiveMaxSpeed(), pod.HullType.BackMultiplier);
        Assert.Equal(1, DockApproach.Steer(navigation.Step, pod.Position, pod.Orientation, velocity,
            rest, dt: .05f, turnState: DockApproach.TurnState.FromShip(pod)).Throttle);
        Assert.Null(PodReturnNavigation.NextWarp(mission.Warps.ToDictionary(w => w.Warp.WarpId, w => w.Warp),
            sector.Id, home.Data.ClusterId, pod.Position, home.Center));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Warp_approach_dodges_the_rock_that_caused_seed23_repeated_impacts()
    {
        Require.Artwork();
        using var server = new Allegiance.Server.Server
        {
            SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(23),
        };
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P958 rock", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        var sector = mission.Sectors.Single(s => s.Id == 1062);
        var rock = sector.Mineable.Single(a => a.Data.AsteroidId == 3610);
        var warp = sector.Warps.Single(w => w.Warp.WarpId == 2175);
        var rockPosition = new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);
        var direction = (warp.Position - rockPosition).Normalize();
        var hull = mission.Core.HullTypes[113];
        var orientation = new Orientation(); orientation.Set(direction);
        var pod = new Ship(20101, hull, rockPosition - direction * (rock.Radius + hull.Length / 2 + 150),
            direction * 60, orientation) { SideId = 1 };
        mission.PlaceShip(pod, sector);
        var target = new NativeGotoPlan.Target(Ship.NativeKey(NativeGotoPlan.Kind.Warp, warp.Warp.WarpId),
            sector.Id, NativeGotoPlan.Kind.Warp, NativeGotoPlan.Objective.Enter, warp.Position, warp.Radius);
        // Fast means no braking for the warp itself; native still runs Dodge
        // and tangent diversion around other bodies (common.cpp:1999-2020,2136-2226).
        var navigation = P699bPodLoadTests.NativeControls(pod, target, []);
        Assert.True(navigation.Step.Has(DockApproach.Mask.Fast));
        Assert.False(navigation.Step.Has(DockApproach.Mask.NoDodge));
        Assert.Equal(-1, navigation.Controls.Throttle);
        Assert.NotEqual(0, navigation.Buttons & ShipControlState.MoveButtonsMask);
        Assert.Equal(1, AutopilotSteering.Pursue(pod.Position, pod.Orientation, warp.Position).Throttle);
    }

    [Fact]
    public void Coincident_separation_uses_injected_stream_with_unchanged_geometry()
    {
        var a = new Collider(null, Vector3.Zero, Vector3.Zero, Orientation.Identity, null, 10, false);
        var b = new Collider(null, Vector3.Zero, Vector3.Zero, Orientation.Identity, null, 10, false);
        var first = Separate.SeparateColliders(a, b, 0, true, HitShape.Sphere, HitShape.Sphere, random: new Random(958));
        var same = Separate.SeparateColliders(a, b, 0, true, HitShape.Sphere, HitShape.Sphere, random: new Random(958));
        var different = Separate.SeparateColliders(a, b, 0, true, HitShape.Sphere, HitShape.Sphere, random: new Random(959));
        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
        Assert.Equal(20.5f, (first.Position1 - first.Position2).Length(), 4);
        Assert.Equal(1, first.Normal.Length(), 5);
    }
}

[Trait("Tier", "Live")]
public sealed class P958PodDeterminismTests(ITestOutputHelper output)
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_seed_reproduces_every_pod_outcome_route_and_time(bool rescueTraffic)
    {
        var harness = new P699bPodLoadTests(output);
        var first = harness.Simulate(rescueTraffic, P699bPodLoadTests.SimulationSeed);
        var second = harness.Simulate(rescueTraffic, P699bPodLoadTests.SimulationSeed);
        var different = harness.Simulate(rescueTraffic, P699bPodLoadTests.SimulationSeed + 1);
        Assert.Equal(200, first.Results.Length);
        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal(first.BayUsage.OrderBy(p => p.Key), second.BayUsage.OrderBy(p => p.Key));
        Assert.NotEqual(first.Signature, different.Signature);
    }
}
