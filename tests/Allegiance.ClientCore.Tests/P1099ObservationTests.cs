using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1099ObservationTests
{
    private static Mission MissionFixture()
    {
        var core = Catalog.RepresentativeCore();
        var file = MissionMap.Load(IgcReader.ParseFile(Path.Combine(Catalog.ArtRoot, "m2xspiderw.igc")));
        var cluster = file.Clusters[0] with { ClusterId = 10 };
        var warp = file.Warps[0] with { ClusterId = 10, WarpId = 12, DestinationId = 13 };
        var rock = file.Asteroids[0] with { ClusterId = 10, AsteroidId = 100 };
        var type = core.StationTypes.Values.First(st => st.HasCapability(DataStationTypeIGC.SabmRestart));
        var station = file.Stations[0] with { ClusterId = 10, SideId = 0, StationId = 200, StationTypeId = type.StationTypeId };
        var map = MissionMap.FromLists([cluster, cluster with { ClusterId = 20 }],
            [warp, warp with { ClusterId = 20, WarpId = 13, DestinationId = 12 }],
            [station, station with { StationId = 201 }], [rock, rock with { AsteroidId = 101 }]);
        return Mission.Assemble(core, map, [new(0, "Alpha", 25), new(1, "Bravo", 3)], new NullIgcSite());
    }

    private sealed class Sequence
    {
        private long _now = 1;
        private readonly P1099ObservationOracle _oracle = new();
        private readonly SkirmishObservationSampler _sampler;
        private readonly SkirmishCamera _oldCamera = new(), _newCamera = new();
        private readonly ClientWorld _oldWorld, _newWorld;
        private string? _epoch;

        internal Sequence()
        {
            _sampler = new() { LocalMilliseconds = () => _now };
            _oldWorld = new() { NowSeconds = () => _now * .001 };
            _newWorld = new() { NowSeconds = () => _now * .001 };
        }

        internal SkirmishObservation Check(Mission mission, short focus, int direction = 0, Action? afterCapture = null)
        {
            _now += 100;
            var expected = _oracle.Read(mission, focus, () => _now);
            _sampler.Capture(mission, focus);
            afterCapture?.Invoke();
            var actual = _sampler.Build();
            CompareFrames(expected, actual);
            if (direction != 0) { _oldCamera.SelectMode(SkirmishCameraMode.Ship); _newCamera.SelectMode(SkirmishCameraMode.Ship); }
            _oldCamera.Update(expected, direction);
            _newCamera.Update(actual, direction);
            Assert.Equal((_oldCamera.SectorId, _oldCamera.ShipId, _oldCamera.StationId),
                (_newCamera.SectorId, _newCamera.ShipId, _newCamera.StationId));
            if (_epoch != actual.Epoch) { _oldWorld.Reset(); _newWorld.Reset(); _epoch = actual.Epoch; }
            _oldWorld.ApplyLocalObservation(expected.Objects, expected.Projectiles, expected.Missiles);
            actual.ApplyTo(_newWorld);
            Assert.Equal(_oldWorld.Ships.Keys.ToArray(), _newWorld.Ships.Keys.ToArray());
            foreach (var (id, ship) in _oldWorld.Ships) Assert.Equivalent(ship, _newWorld.Ships[id], strict: true);
            Assert.Equal(_oldWorld.Stations.Keys.ToArray(), _newWorld.Stations.Keys.ToArray());
            Assert.Equal(_oldWorld.Warps.Keys.ToArray(), _newWorld.Warps.Keys.ToArray());
            Assert.Equal(_oldWorld.Asteroids.Keys.ToArray(), _newWorld.Asteroids.Keys.ToArray());
            foreach (var (id, value) in _oldWorld.Stations)
                Assert.Equal(value.Encode(), _newWorld.Stations[id].Encode());
            foreach (var (id, value) in _oldWorld.Asteroids)
                Assert.Equal(value.Encode(), _newWorld.Asteroids[id].Encode());
            foreach (var (id, value) in _oldWorld.Warps)
                Assert.Equal(value.Encode(), _newWorld.Warps[id].Encode());
            Assert.Equal(_oldWorld.Probes.Keys.ToArray(), _newWorld.Probes.Keys.ToArray());
            Assert.Equal(_oldWorld.Mines.Keys.ToArray(), _newWorld.Mines.Keys.ToArray());
            Assert.Equal(_oldWorld.Missiles.Keys.ToArray(), _newWorld.Missiles.Keys.ToArray());
            Assert.Equal(_oldWorld.StationLive.ToArray(), _newWorld.StationLive.ToArray());
            Assert.Equal(_oldWorld.ScoreRoster.ToArray(), _newWorld.ScoreRoster.ToArray());
            Assert.Null(_newWorld.MyShipId);
            Assert.Null(_newWorld.MySideId);
            return actual;
        }
    }

    internal static void CompareFrames(SkirmishObservation expected, SkirmishObservation actual)
    {
        Assert.Equal((expected.Epoch, expected.Stage, expected.Seconds), (actual.Epoch, actual.Stage, actual.Seconds));
        Assert.Equal(expected.Sides, actual.Sides);
        Assert.Equal(expected.RecentKills.ToArray(), actual.RecentKills.ToArray());
        Assert.Equal(expected.Objects.Length, actual.Objects.Length);
        for (int i = 0; i < expected.Objects.Length; i++)
        {
            Assert.Equal(expected.Objects[i].GetType(), actual.Objects[i].GetType());
            Assert.Equal(expected.Objects[i].Encode(), actual.Objects[i].Encode());
            if (expected.Objects[i] is ExportWarp warp)
                Assert.Equal(warp.Warp, ((ExportWarp)actual.Objects[i]).Warp); // includes the non-wire mass limit
        }
        Assert.Equal(expected.Projectiles.Length, actual.Projectiles.Length);
        for (int i = 0; i < expected.Projectiles.Length; i++)
            Assert.Equivalent(expected.Projectiles[i], actual.Projectiles[i], strict: true);
        Assert.Equal(expected.Missiles.Length, actual.Missiles.Length);
        for (int i = 0; i < expected.Missiles.Length; i++)
            Assert.Equivalent(expected.Missiles[i], actual.Missiles[i], strict: true);
    }

    [SkippableFact]
    public void Ordered_samples_match_across_spawn_ejection_warp_removal_and_rotation()
    {
        Require.Artwork();
        var mission = MissionFixture();
        var core = mission.Core;
        var first = mission.Sectors[0];
        var next = mission.Sectors[1];
        var sequence = new Sequence();
        sequence.Check(mission, -1);
        mission.Stage = MissionStage.Started;
        mission.Update(mission.LastUpdate + .75);
        var hull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        Ship Spawn(short id, Sector sector)
        {
            var ship = new Ship(id, hull, new(id, 23, -7), new(57, -1, 9), Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return ship;
        }
        var a = Spawn(301, first);
        var b = Spawn(302, next);
        var c = Spawn(303, first);
        a.StationId = 200;
        c.ParentShip = b;
        var mine = new Mine(401, first, core.MineTypes.Values.First(), default, 3, a);
        var probe = new Probe(402, first, core.ProbeTypes.Values.First(), default, 2, 1, b);
        var missile = new Missile(403, next, core.MissileTypes.Values.First(), default, default, new(0, 0, 1), 3, b);
        first.Add(mine); first.Add(probe); next.Add(missile);
        var shot = new Projectile(404, first, core.ProjectileTypes.Values.First(), new(1, 2, 3), new(4, 5, 6), new(0, 1, 0), 4, a);
        first.Add(shot);
        mission.Sides[0].Buckets.First(b => b.Price > 0 && !b.Completed).SetTimeAndMoney(500, 1);
        var retained = sequence.Check(mission, first.Id);
        Assert.Single(retained.Projectiles);
        Assert.Single(retained.Missiles);
        Assert.Contains("funded", retained.Sides[0].Research);
        Assert.Single(retained.Objects.OfType<ExportShip>());
        var retainedBytes = retained.Objects.SelectMany(o => o.Encode()).ToArray();
        var pilot = new ClientWorld();
        retained.ApplyTo(pilot);
        pilot.Apply(new SetCluster { SectorId = next.Id, Ship = retained.Objects.OfType<SingleShipUpdate>().Single().Ship });
        Assert.Throws<InvalidOperationException>(() => retained.ApplyTo(pilot));
        a.StationId = null; c.ParentShip = null;
        sequence.Check(mission, next.Id, 1, () =>
        {
            first.Remove(a); mission.PlaceShip(a, next);
            b.Position = new(88, 33, 22);
            a.ChangeHull(core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)));
        });
        var ejection = sequence.Check(mission, next.Id, -1);
        Assert.True(ejection.RecentKills.ContainsKey(first.Id));
        Assert.Empty(ejection.Projectiles);
        next.Remove(b); c.SetFraction(0);
        first.Remove(mine); first.Remove(probe); next.Remove(missile); first.Remove(shot);
        var last = sequence.Check(mission, first.Id);
        Assert.Single(last.Objects.OfType<ExportShip>());
        Assert.Empty(last.Missiles);
        Assert.Empty(sequence.Check(MissionFixture(), first.Id).RecentKills);
        Assert.Equal(retainedBytes, retained.Objects.SelectMany(o => o.Encode()).ToArray());
    }

    [SkippableFact]
    public void Ordered_terrain_invalidates_on_replacement_station_edits_and_warp_removal()
    {
        Require.Artwork();
        var mission = MissionFixture();
        var sector = mission.Sectors[0];
        var sequence = new Sequence();
        var initial = sequence.Check(mission, sector.Id);
        Assert.Same(initial.TerrainVersion, sequence.Check(mission, sector.Id).TerrainVersion);
        var resetWorld = new ClientWorld();
        initial.ApplyTo(resetWorld);
        resetWorld.BeginNextMission();
        initial.ApplyTo(resetWorld);
        Assert.Equal(2, resetWorld.Stations.Count);
        var old = sector.Mineable[0];
        sector.RemoveAsteroid(old);
        mission.PlaceAsteroid(new Asteroid(old.Data with { Name = "Replacement", Radius = 123 }), sector);
        var station = sector.StationRuntimes[0];
        var replacement = sequence.Check(mission, sector.Id);
        Assert.NotSame(initial.TerrainVersion, replacement.TerrainVersion);
        station.SetName("Renamed");
        sequence.Check(mission, sector.Id);
        station.Capture(1);
        var captured = sequence.Check(mission, sector.Id);
        // Regeneration changes only the live fraction batch after capture.
        station.Regenerate(600);
        var regenerated = sequence.Check(mission, sector.Id);
        Assert.Same(captured.TerrainVersion, regenerated.TerrainVersion);
        Assert.NotEqual(captured.Objects.OfType<StationsUpdate>().First().Encode(),
            regenerated.Objects.OfType<StationsUpdate>().First().Encode());
        station.SetBaseStationType(mission.Core.StationTypes.Values.First(st => st.StationTypeId != station.Type.StationTypeId));
        sequence.Check(mission, sector.Id);
        sector.RemoveWarp(sector.Warps[0]);
        sequence.Check(mission, sector.Id);
        sector.RemoveStation(station);
        mission.PlaceStation(new Station(station.Data with { StationId = 209 }, station.Type), sector);
        sequence.Check(mission, sector.Id);
        // Same ids and collection sizes can belong to a new mission.
        sequence.Check(MissionFixture(), sector.Id);
    }
}

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P1099ObservationLiveTests
{
    [SkippableFact]
    public async Task Embedded_observer_matches_the_oracle_on_the_same_live_tick()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { StartAsSpectator = true, Seed = 1099, StartCountdownSeconds = 1 });
        var oracle = new P1099ObservationOracle();
        bool sawShips = false;
        for (int i = 0; i < 120 && (!sawShips || i < 10); i++)
        {
            host.OnSimulation(server =>
            {
                var mission = server.Missions[0];
                var sector = mission.Sectors[i % mission.Sectors.Count].Id;
                var before = Environment.TickCount64;
                var actual = host.Observe(sector);
                var expected = oracle.Read(mission, sector, () => before);
                var after = Environment.TickCount64;
                // Local receipt times are independent reads of the wall clock. Check the
                // full capture interval, then compare all other fields exactly.
                foreach (var p in actual.Projectiles) { Assert.InRange(p.ReceivedLocalMs, before, after); p.ReceivedLocalMs = before; }
                foreach (var m in actual.Missiles) { Assert.InRange(m.LastUpdatedLocalMs, before, after); m.LastUpdatedLocalMs = before; }
                P1099ObservationTests.CompareFrames(expected, actual);
                sawShips |= actual.Objects.OfType<ExportShip>().Any();
                return true;
            });
            await Task.Delay(100);
        }
        Assert.True(sawShips);
        Assert.Null(host.Failure);
    }
}
