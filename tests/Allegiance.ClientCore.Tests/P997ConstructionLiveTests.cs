using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P997ConstructionLiveTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Real_constructor_completion_hands_off_over_TCP_before_the_native_effect_timeout()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P997-owner", 99701);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P997-enemy", 99702);
        owner.SendPlayerReady(true, owner.World.MyShipId!.Value);
        enemy.SendPlayerReady(true, enemy.World.MyShipId!.Value);
        P660NextMatchLiveTests.Pump(server, () => server.Missions[0].Stage == MissionStage.Started
            && Read(owner, () => owner.World.InGame));
        var mission = server.Missions[0];
        var sector = mission.Sectors.First(s => s.StationRuntimes.Count == 0);
        var type = mission.Core.StationTypes.Values.First() with { SuccessorStationTypeId = -1 };
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = 30000, ClusterId = sector.Id, Position = [700, 850, 690],
            Radius = 50, HitPoints = 1000, Signature = 1, AabmCapabilities = type.AabmBuild,
        });
        mission.PlaceAsteroid(rock, sector);
        var constructor = mission.Core.DroneTypes.Values.First(drone => drone.PilotType == Ship.PilotTypeBuilder);
        var builder = server.SpawnNpcShip(mission, sector, 0, new Vector3(700, 850, 890), constructor.DroneTypeId);
        builder.BuilderStationType = type;
        Assert.True(builder.IsNpc);
        Assert.Equal(Ship.PilotTypeBuilder, builder.PilotType);
        Assert.Equal(constructor.HullTypeId, builder.HullType.HullId);
        lock (owner.Gate) owner.World.Apply(ExportAsteroid.FromAsteroid(rock.Data));
        mission.Site.CreateBuildingEffect(server.SimNow, rock, builder);
        P660NextMatchLiveTests.Pump(server, () => Read(owner,
            () => owner.World.ConstructionVisuals.ContainsKey(rock.Data.AsteroidId)));
        double started = server.SimNow;
        // Advance ordinary ticks at normal construction speed; no synthetic
        // station insertion and no presentation-side prune is called.
        P660NextMatchLiveTests.Pump(server, () => rock.ConstructedStation is not null, max: 2000);
        var station = rock.ConstructedStation!;
        P660NextMatchLiveTests.Pump(server, () => Read(owner,
            () => owner.World.Stations.ContainsKey(station.StationId)));
        lock (owner.Gate)
        {
            Assert.DoesNotContain(rock.Data.AsteroidId, owner.World.Asteroids.Keys);
            Assert.DoesNotContain(rock.Data.AsteroidId, owner.World.ConstructionVisuals.Keys);
            Assert.Contains(rock.Data.AsteroidId, owner.World.BuildingEffects.Keys);
        }
        Assert.True(server.SimNow - started < ConstructionPhase.Duration,
            "Must exercise wire retirement before the 40-second fallback expiry");
        Assert.True(builder.Dead || !sector.Models.Contains(builder));
    }

    private static bool Read(ConnectionSession session, Func<bool> read) => P660NextMatchLiveTests.Read(session, read);
}
