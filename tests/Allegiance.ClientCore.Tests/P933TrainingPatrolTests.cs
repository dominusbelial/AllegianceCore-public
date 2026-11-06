using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using V = Allegiance.Core.Collision.Vector3;
using N = System.Numerics.Vector3;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P933TrainingPatrolTests : IDisposable
{
    public P933TrainingPatrolTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }

    private static (TrainingHost Host, Ship Actor, Sector Sector) Arena(bool free = false)
    {
        Require.Artwork();
        var host = TrainingHost.Start(free ? TrainingHost.FreeFlight : TrainingHost.PracticeArena);
        TrainingMission lesson = free ? new Mission10(new TrainingHostSite(host)) : new Mission6(new TrainingHostSite(host));
        host.SetActiveMission(lesson);
        host.PlayerShip.StationId = host.HomeStationId;
        var sector = host.PlayerShip.Sector!;
        foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
        foreach (var rock in sector.Mineable.ToArray()) sector.RemoveAsteroid(rock);
        foreach (var warp in sector.Warps.ToArray()) sector.RemoveWarp(warp);
        foreach (var model in sector.Models.Where(m => m is not Ship).ToArray()) sector.Terminate(model);
        host.CreateDrone("patrol", 7300, 315, 1, Ship.PilotTypeWingman, 0, -1, 1);
        host.PlaceDrone(7300, sector.Id, new N(0, 0, 5000), N.UnitX, N.UnitZ, null, 0);
        return (host, host.Drones[7300], sector);
    }

    private static V Pick(TrainingHost host, Ship actor)
    {
        actor.CompleteGotoOrder();
        Assert.True(host.Mission.Site.HandlePickDefaultOrder(actor));
        var buoy = Assert.IsType<Buoy>(actor.AcceptedShipTarget);
        Assert.Equal(DataBuoyIGC.CBuoyWaypoint, buoy.Data.Type);
        Assert.False(buoy.Data.Visible);
        Assert.Equal(0x80, actor.WingmanBehaviour & 0x80);
        return buoy.Position;
    }

    private static Probe Probe(TrainingHost host, Sector sector, V point) =>
        new(7310, sector, host.Mission.Core.ProbeTypes.Values.First(), point, host.Mission.LastUpdate, 1);

    private static Asteroid Rock(TrainingHost host, Sector sector, V point)
    {
        var data = host.Mission.Map.Asteroids.First();
        var rock = new Asteroid(data with { AsteroidId = 7311, ClusterId = sector.Id,
            Position = [point.X, point.Y, point.Z], Radius = 100, Ore = 0, AabmCapabilities = 0 });
        host.Mission.PlaceAsteroid(rock, sector);
        return rock;
    }

    private static V Correct(Ship actor, V center, float radius) =>
        center + (actor.Position - center).Normalize() * (radius + actor.Radius + 10);

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Mixed_pickable_insertion_order_and_reentry_determine_the_last_correction(bool probeFirst)
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        var probe = Probe(host, sector, actor.Position - new V(1, 0, 0));
        if (probeFirst) sector.Add(probe);
        var rock = Rock(host, sector, actor.Position - new V(0, 1, 0));
        if (!probeFirst) sector.Add(probe);
        Assert.Equal(probeFirst ? Correct(actor, new V(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]), rock.Radius)
            : Correct(actor, probe.Position, probe.ProbeType.Radius), Pick(host, actor));
        sector.Remove(probe); sector.Add(probe);
        Assert.Equal(Correct(actor, probe.Position, probe.ProbeType.Radius), Pick(host, actor));
        sector.Remove(probe);
        Assert.Equal(Correct(actor, new V(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]), rock.Radius), Pick(host, actor));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("missile")] [InlineData("treasure")] [InlineData("hidden buoy")] [InlineData("visible buoy")]
    public void Complete_nonship_pickable_membership_corrects_patrol_points(string kind)
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        V center = actor.Position - new V(1, 0, 0);
        float radius;
        switch (kind)
        {
            case "missile":
                var missile = new Missile(7320, sector, host.Mission.Core.MissileTypes.Values.First(), center,
                    V.Zero, new V(1, 0, 0), host.Mission.LastUpdate, host.PlayerShip);
                sector.Add(missile); radius = missile.MissileType.Radius; break;
            case "treasure":
                var data = DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]) with
                    { P0 = [center.X, center.Y, center.Z], Lifespan = 1000, CreateNow = true };
                sector.Add(new Treasure(7320, sector, data, host.Mission.LastUpdate, host.Mission));
                radius = Treasure.PickupRadius; break;
            default:
                bool visible = kind == "visible buoy";
                host.CreateWaypoint(7320, new N(center.X, center.Y, center.Z), sector.Id, DataBuoyIGC.CBuoyWaypoint, visible);
                host.AddBuoyConsumer(7320); radius = visible ? 20 : 2; break;
        }
        Assert.Equal(Correct(actor, center, radius), Pick(host, actor));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void NoHit_excludes_only_the_identical_launcher_and_removed_buoys_leave_the_list()
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        var probe = Probe(host, sector, actor.Position - new V(0, 1, 0)); sector.Add(probe);
        V center = actor.Position - new V(1, 0, 0);
        var type = host.Mission.Core.MissileTypes.Values.First();
        var own = new Missile(7330, sector, type, center, V.Zero, new V(1, 0, 0), host.Mission.LastUpdate, actor);
        sector.Add(own);
        Assert.Equal(Correct(actor, probe.Position, probe.ProbeType.Radius), Pick(host, actor));
        // Same numeric ID is deliberately a different pointer, as in GetNoHit.
        var twin = new Ship(actor.Id, actor.HullType, V.Zero, V.Zero, Orientation.Identity);
        var other = new Missile(7331, sector, type, center, V.Zero, new V(1, 0, 0), host.Mission.LastUpdate, twin);
        sector.Add(other);
        Assert.Equal(Correct(actor, center, type.Radius), Pick(host, actor));
        sector.Remove(other);
        host.CreateWaypoint(7332, new N(center.X, center.Y, center.Z), sector.Id, DataBuoyIGC.CBuoyWaypoint, false);
        host.AddBuoyConsumer(7332);
        Assert.Equal(Correct(actor, center, 2), Pick(host, actor));
        host.Mission.GetBuoy(7332)!.ReleaseConsumer();
        Assert.Null(host.Mission.GetBuoy(7332));
        Assert.Equal(Correct(actor, probe.Position, probe.ProbeType.Radius), Pick(host, actor));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Ships_and_mines_are_excluded_and_projectiles_chaff_are_not_pickable()
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        var probe = Probe(host, sector, actor.Position - new V(0, 1, 0)); sector.Add(probe);
        V center = actor.Position - new V(1, 0, 0);
        var ship = new Ship(7340, actor.HullType, center, V.Zero, Orientation.Identity) { SideId = 1 };
        host.Mission.PlaceShip(ship, sector);
        var mine = new Mine(7341, sector, host.Mission.Core.MineTypes.Values.First(), center, host.Mission.LastUpdate);
        var projectile = new Projectile(7342, sector, host.Mission.Core.ProjectileTypes.Values.First(), center,
            V.Zero, new V(1, 0, 0), host.Mission.LastUpdate);
        var chaff = new Chaff(7343, sector, host.Mission.Core.ChaffTypes.Values.First(), center, V.Zero, host.Mission.LastUpdate);
        sector.Add(mine); sector.Add(projectile); sector.Add(chaff);
        Assert.Contains(ship, sector.PickableModels); Assert.Contains(mine, sector.PickableModels);
        Assert.DoesNotContain(projectile, sector.PickableModels); Assert.DoesNotContain(chaff, sector.PickableModels);
        Assert.Equal(Correct(actor, probe.Position, probe.ProbeType.Radius), Pick(host, actor));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Coincident_centers_use_native_retail_return_without_the_debug_assertion()
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        var first = Probe(host, sector, actor.Position - new V(1, 0, 0)); sector.Add(first);
        var coincident = new Buoy(new([actor.Position.X, actor.Position.Y, actor.Position.Z], sector.Id,
            DataBuoyIGC.CBuoyWaypoint, 7350, false));
        host.Mission.AddBuoy(coincident); coincident.AddConsumer();
        Assert.Equal(actor.Position, Pick(host, actor));
        sector.Remove(first);
        Seed(host, 933); Assert.Equal(actor.Position, Pick(host, actor));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(false)] [InlineData(true)]
    public void Live_arena_tick_picks_through_complete_candidates_for_each_lesson(bool free)
    {
        var (host, actor, sector) = Arena(free); using var owned = host;
        V before = actor.Position;
        var probe = Probe(host, sector, before - new V(0, 1, 0)); sector.Add(probe);
        var treasureData = DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]) with
            { P0 = [before.X - 1, before.Y, before.Z], Lifespan = 1000, CreateNow = true };
        var treasure = new Treasure(7360, sector, treasureData, host.Mission.LastUpdate, host.Mission); sector.Add(treasure);
        var own = new Missile(7361, sector, host.Mission.Core.MissileTypes.Values.First(), before - new V(0, 0, 1),
            V.Zero, new V(1, 0, 0), host.Mission.LastUpdate, actor); sector.Add(own);
        V expected = default;
        int calls = 0;
        var site = Assert.IsType<Allegiance.Server.ServerHost>(host.Mission.Site);
        var choose = site.TrainingDefaultOrder!;
        site.TrainingDefaultOrder = choosing =>
        {
            if (ReferenceEquals(choosing, actor))
            {
                calls++;
                expected = Correct(choosing, treasure.Position, Treasure.PickupRadius);
            }
            return choose(choosing);
        };
        Assert.Null(actor.AcceptedShipTarget);
        host.StepLight(); // actual Ship.DecideWingmanDefaultOrder -> host callback -> arena policy
        Assert.True(calls > 0);
        var buoy = Assert.IsType<Buoy>(actor.AcceptedShipTarget);
        Assert.Equal(expected, buoy.Position);
        Assert.Equal(OrderChange.CidGoto, actor.AcceptedShipCommandId);
        Assert.Equal(0x80, actor.WingmanBehaviour & 0x80);
        Assert.Contains(buoy, sector.PickableModels);
        Assert.False(actor.Dead);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Imported_map_record_order_is_preserved_across_types_and_static_removal()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.PracticeArena);
        var source = host.Mission.Map;
        var cluster = source.Clusters[0];
        var station = source.Stations.First(s => s.ClusterId == cluster.ClusterId);
        var rock = source.Asteroids.First() with { ClusterId = cluster.ClusterId };
        var warp = source.Warps.First() with { ClusterId = cluster.ClusterId };
        // Force a different cross-type order from Mission.Assemble's typed passes.
        (IgcObjectType Type, byte[] Data)[] records = [
            (IgcObjectType.Cluster, cluster.Encode()), (IgcObjectType.Station, station.Encode()),
            (IgcObjectType.Warp, warp.Encode()), (IgcObjectType.Asteroid, rock.Encode())];
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(records.Sum(r => r.Data.Length + IgcRecord.HeaderSize));
            foreach (var record in records) { writer.Write((short)record.Type); writer.Write(record.Data.Length); writer.Write(record.Data); }
        }
        var map = MissionMap.Load(IgcReader.Parse(stream.ToArray()));
        using var mission = Mission.Assemble(host.Mission.Core, map,
            host.Mission.Sides.Select(s => new SideSpec(s.Id, s.Name, s.Civilization.CivilizationId)).ToArray(), new NullSite());
        var sector = mission.Sectors.Single();
        var st = sector.StationRuntimes.Single(); var gate = sector.Warps.Single(); var asteroid = sector.Mineable.Single();
        Assert.Equal(new IModelIgc[] { st, gate, asteroid }, sector.PickableModels);
        sector.RemoveWarp(gate); sector.RemoveStation(st); sector.RemoveAsteroid(asteroid);
        Assert.Empty(sector.PickableModels);
    }

    private sealed class NullSite : IIgcSite;

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Station_helium_then_two_random_candidates_keep_native_order_for_seeded_selection()
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        var original = host.Mission.Map.Stations.First();
        var type = host.Mission.Core.StationTypes[original.StationTypeId];
        Station AddStation(short id, short side, V point, bool seen)
        {
            var station = new Station(original with { StationId = id, SideId = side, ClusterId = sector.Id,
                Position = [point.X, point.Y, point.Z] }, type);
            host.Mission.PlaceStation(station, sector);
            station.SideVisibility.SetSideVisibility(1, seen);
            return station;
        }
        var first = AddStation(7370, 0, new V(2000, 0, 5000), true);
        AddStation(7371, 1, new V(3000, 0, 5000), true); // friendly station is not a candidate
        var second = AddStation(7372, 0, new V(4000, 0, 5000), true);
        AddStation(7373, 0, new V(100000, 0, 5000), false); // neither side knowledge nor personal scan
        var rockData = host.Mission.Map.Asteroids.First();
        var rock1 = new Asteroid(rockData with { AsteroidId = 7374, ClusterId = sector.Id,
            Position = [0, 2000, 5000], AabmCapabilities = 1 });
        var rock2 = new Asteroid(rockData with { AsteroidId = 7375, ClusterId = sector.Id,
            Position = [0, 3000, 5000], AabmCapabilities = 1 });
        host.Mission.PlaceAsteroid(rock1, sector); Rock(host, sector, new V(0, -2000, 5000));
        host.Mission.PlaceAsteroid(rock2, sector);
        var covered = new HashSet<int>();
        for (int seed = 0; seed < 100 && covered.Count < 6; seed++)
        {
            var random = new Random(seed);
            V[] candidates = [first.Center + new V(300, 300, 250), second.Center + new V(300, 300, 250),
                new V(0, 2000, 5350), new V(0, 3000, 5350),
                new V(random.NextSingle() * 6000 - 3000, random.NextSingle() * 6000 - 3000, random.NextSingle() * 2000 - 1000),
                new V(random.NextSingle() * 6000 - 3000, random.NextSingle() * 6000 - 3000, random.NextSingle() * 2000 - 1000)];
            int index = random.Next(6); covered.Add(index);
            Seed(host, seed); Assert.Equal(candidates[index], Pick(host, actor));
        }
        Assert.Equal(6, covered.Count);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Navigation_buoys_join_at_creation_and_leave_when_their_plan_is_replaced()
    {
        var (host, actor, sector) = Arena(); using var owned = host;
        actor.SetGoto(new GotoOrder(GotoKind.Waypoint, sector.Id, new V(1000, 0, 5000)));
        var buoy = Assert.IsType<Buoy>(actor.GetNpcPlan().Target);
        Assert.Same(buoy, sector.PickableModels.Last());
        var probe = Probe(host, sector, new V(2000, 0, 5000)); sector.Add(probe);
        Assert.True(sector.PickableModels.ToList().IndexOf(buoy) < sector.PickableModels.ToList().IndexOf(probe));
        actor.SetGoto(null);
        Assert.DoesNotContain(buoy, sector.PickableModels);
    }

    private static void Seed(TrainingHost host, int seed) => host.ArenaPatrolRandom = new Random(seed);

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Same_seed_replays_both_random_points_and_the_candidate_index(bool free)
    {
        var (host, actor, _) = Arena(free); using var owned = host;
        const int seed = 933;
        var random = new Random(seed);
        V[] expected = Enumerable.Range(0, 2).Select(_ => new V(random.NextSingle() * 6000 - 3000,
            random.NextSingle() * 6000 - 3000, random.NextSingle() * 2000 - 1000)).ToArray();
        V point = expected[random.Next(2)];
        Seed(host, seed); Assert.Equal(point, Pick(host, actor));
        Seed(host, seed); Assert.Equal(point, Pick(host, actor));
    }
}
