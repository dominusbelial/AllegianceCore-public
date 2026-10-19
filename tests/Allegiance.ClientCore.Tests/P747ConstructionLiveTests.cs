using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P747ConstructionLiveTests
{
    private static void Barrier(Srv server, ConnectionSession peer)
    {
        peer.TakePingEchoes(); peer.SendPing();
        P660NextMatchLiveTests.Pump(server, () => peer.TakePingEchoes().Count > 0);
    }

    private static void Start(Srv server, ConnectionSession owner, ConnectionSession enemy)
    {
        owner.SendPlayerReady(true, owner.World.MyShipId!.Value);
        enemy.SendPlayerReady(true, enemy.World.MyShipId!.Value);
        P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started
            && P660NextMatchLiveTests.Read(owner, () => owner.World.InGame)
            && P660NextMatchLiveTests.Read(enemy, () => enemy.World.InGame));
    }

    private static (Asteroid Rock, Ship Builder, Sector Sector) Build(Srv server)
    {
        var mission = server.Missions[0];
        var sector = mission.Sectors.First(s => s.StationRuntimes.Count == 0);
        var type = mission.Core.StationTypes.Values.First() with { SuccessorStationTypeId = -1, Signature = .0001f };
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = 30000, ClusterId = sector.Id, Position = [10000, 10000, 10000],
            Radius = 50, HitPoints = 1000, Signature = 1, Rotation = [0, 0, 1, 0], AabmCapabilities = type.AabmBuild
        });
        mission.PlaceAsteroid(rock, sector);
        var builder = new Ship(30001, mission.Core.HullTypes.Values.First() with { HabmCapabilities = 0 },
            new Vector3(10000, 10000, 10200), Vector3.Zero, Orientation.Identity) { SideId = 0, BuilderStationType = type };
        mission.PlaceShip(builder, sector);
        return (rock, builder, sector);
    }

    // FedSrv/FedSrv.CPP:13733-13767: creation is owner/current-eye scoped; interruption is mission-wide.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Construction_callbacks_deliver_creation_only_to_informed_sides_and_interrupt_over_TCP(bool currentEye)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P747-owner", 74701);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P747-enemy", 74702);
        Assert.Equal((short)0, owner.World.MySideId!.Value); Assert.Equal((short)1, enemy.World.MySideId!.Value);
        Start(server, owner, enemy);
        var w = Build(server); w.Rock.SideVisibility.SetSideVisibility(1, currentEye);
        w.Builder.Mission!.Site.CreateBuildingEffect(server.SimNow, w.Rock, w.Builder);
        var effect = Assert.Single(w.Sector.BuildingEffects);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.BuildingEffects.ContainsKey(w.Rock.Data.AsteroidId)));
        Barrier(server, enemy);
        lock (owner.Gate)
            Assert.Equal(effect.PositionStart, owner.World.BuildingEffects[w.Rock.Data.AsteroidId].PositionStart);
        Assert.Equal(currentEye, P660NextMatchLiveTests.Read(enemy,
            () => enemy.World.BuildingEffects.ContainsKey(w.Rock.Data.AsteroidId)));
        w.Builder.Mission.Site.KillBuildingEffectEvent(effect);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => !owner.World.BuildingEffects.ContainsKey(w.Rock.Data.AsteroidId)));
        Barrier(server, enemy);
        Assert.False(P660NextMatchLiveTests.Read(enemy, () => enemy.World.BuildingEffects.ContainsKey(w.Rock.Data.AsteroidId)));
        Assert.Contains(w.Rock, w.Sector.Mineable); Assert.True(effect.IsTerminated);
    }

    [SkippableFact]
    public void Direct_BuildStation_callback_places_and_exports_the_station_and_retires_the_owners_rock_over_TCP()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P747-direct-owner", 74721);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P747-direct-enemy", 74722);
        Start(server, owner, enemy); var w = Build(server); var mission = w.Builder.Mission!;
        int damageType = Array.FindIndex(mission.Core.Constants.DamageConstants, row => row[Asteroid.DefenseType] > 0);
        float factor = mission.Core.Constants.DamageConstants[damageType][Asteroid.DefenseType];
        w.Rock.ReceiveDamage((byte)damageType, 250 / factor, server.SimNow, Vector3.Zero, Vector3.Zero, null);
        lock (owner.Gate) owner.World.Apply(ExportAsteroid.FromAsteroid(w.Rock.Data));
        mission.Site.BuildStation(w.Rock, mission.Sides[0], w.Builder.BuilderStationType!.Value, server.SimNow, new bool[6]);
        var station = w.Rock.ConstructedStation!;
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.Stations.ContainsKey(station.StationId) && !owner.World.Asteroids.ContainsKey(w.Rock.Data.AsteroidId)));
        lock (owner.Gate)
        {
            Assert.Equal((byte)180, owner.World.Stations[station.StationId].BpHull);
            Assert.Equal(new float[] { 0, 0, 1, 0 }, owner.World.Stations[station.StationId].Rotation);
        }
        Assert.Contains(w.Builder, w.Sector.Models); // quiet constructor consumption belongs to AsteroidUpdate
    }

    // Native consumed-rock fog: completion leaves an unobserved rock until a ship eyes its old location.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Completion_preserves_an_unobserved_rock_until_its_side_scoped_discovery_receipt_over_TCP(bool stationDiscovery)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P747-owner", 74711);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P747-enemy", 74712);
        Start(server, owner, enemy);
        var w = Build(server); var mission = w.Builder.Mission!;
        // Seed prior map knowledge, then test real production completion/removal messages.
        lock (owner.Gate) owner.World.Apply(ExportAsteroid.FromAsteroid(w.Rock.Data));
        lock (enemy.Gate) enemy.World.Apply(ExportAsteroid.FromAsteroid(w.Rock.Data));
        mission.ShowcaseResearchTimeScale = 1000;
        mission.Site.CreateBuildingEffect(server.SimNow, w.Rock, w.Builder);
        P660NextMatchLiveTests.Pump(server, () => w.Rock.ConstructedStation is not null);
        var station = w.Rock.ConstructedStation!;
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.Stations.ContainsKey(station.StationId) && !owner.World.Asteroids.ContainsKey(w.Rock.Data.AsteroidId)));
        Barrier(server, enemy);
        Assert.True(P660NextMatchLiveTests.Read(enemy, () => enemy.World.Asteroids.ContainsKey(w.Rock.Data.AsteroidId)));
        Assert.False(P660NextMatchLiveTests.Read(enemy, () => enemy.World.Stations.ContainsKey(station.StationId)));
        if (stationDiscovery)
        {
            // A probe discovers the station without triggering the ship-only old-rock eye pass.
            var type = mission.Core.ProbeTypes.Values.First() with { Ammo = 0, Lifespan = 1000, ScannerRange = 1000 };
            w.Sector.Add(new Probe(30002, w.Sector, type, station.Center + new Vector3(0, 0, 1), server.SimNow, 1));
        }
        else
            mission.Site.KillAsteroidEvent(w.Rock.Data.AsteroidId, w.Sector.Id, mission.Sides[1]);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(enemy,
            () => !enemy.World.Asteroids.ContainsKey(w.Rock.Data.AsteroidId)));
        if (stationDiscovery)
            Assert.True(P660NextMatchLiveTests.Read(enemy, () => enemy.World.Stations.ContainsKey(station.StationId)));
        Assert.DoesNotContain(w.Rock, w.Sector.Mineable);
    }
}

public sealed class P747ConstructionClientTests
{
    [Theory]
    [InlineData(0, 1, .5f)] [InlineData(5, 25.5f, .5f)] [InlineData(10, 50, .5f)]
    [InlineData(15, 80, .5f)] [InlineData(20, 110, .5f)] [InlineData(25, 110, .75f)]
    [InlineData(30, 110, 1)] [InlineData(35, 105, .5f)]
    public void Received_export_uses_native_phase_state_and_survives_the_station_transformation(double seconds, float radius, float alpha)
    {
        var world = new ClientWorld();
        world.Apply(new ExportBuildingEffect { Effect = new(0, 50, 100, new Vector3(0, 0, 100), Vector3.Zero, 0, 50, 1, 1, 1, 1) });
        var effect = world.BuildingEffects[50];
        long now = effect.ReceivedLocalMs + (long)(seconds * 1000);
        var phase = effect.Phase(now);
        Assert.Equal(radius, phase.Radius, 3); Assert.Equal(alpha, phase.InnerAlpha, 3);
        world.PruneExpiredBuildingEffects(now); Assert.Single(world.BuildingEffects);
        world.PruneExpiredBuildingEffects(effect.ReceivedLocalMs + 40000); Assert.Empty(world.BuildingEffects);
    }
}
