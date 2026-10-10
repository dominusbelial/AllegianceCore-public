using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P425 — LIVE proof that the MonoGame client's PRODUCTION order sender
/// (<see cref="ConnectionSession.SendOrderChange"/>, the exact method the
/// Insert key handler <c>Game1.TryIssueOrderToCommandee</c> calls) drives the
/// real direct-drone Build over the real wire. A real in-process server hosts a
/// commander client (a <see cref="ConnectionSession"/>) on side 0 and a real
/// Builder DRONE (PilotType==6) on the same side; the client sends the exact
/// ORDER_CHANGE the issue-order key resolves (<c>ShipID</c> = the drone,
/// <c>commandID=c_cidBuild</c>, <c>objectType=OT_asteroid</c>, <c>objectID</c> =
/// the commanded rock — the <see cref="Game1.CommandOrderResolver"/> payload) and
/// the full P422 arc plays out: construction registered on the commanded rock,
/// then on completion the rock → station transformation fires and the DRONE is
/// consumed — all driven by the client's own sender, not a test harness.
///
/// A second leg proves the server's real refusal is SURFACED to the commanding
/// client as a private "Negative." chat (the "don't leave the player guessing"
/// requirement) when the order is illegal (wrong-type rock).
///
/// The [Collection("LiveNetwork")] tag keeps this sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class CommandedDroneBuildKeyLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");
    private static readonly MethodInfo SpawnNpcShipMethod =
        typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.SpawnNpcShip method not found");

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 924u, DwCookie = 0x924u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97800000000000002ul, DrmHash = "p425client",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static bool PositionsMatch(float[] a, Vector3 b) =>
        a is { Length: 3 } &&
        Math.Abs(a[0] - b.X) < 0.01f && Math.Abs(a[1] - b.Y) < 0.01f && Math.Abs(a[2] - b.Z) < 0.01f;

    private static SimVec RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

    private static (Srv Server, Mission Mission, Sector Sector, Ship Commander, short BuilderDroneId, DataStationTypeIGC StationType, Asteroid Rock, Asteroid WrongRock, short Side)
        Boot(out ConnectionSession client)
    {
        var server = new Srv();
        var core = StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc")));
        Mission? mission = null;
        Sector? sector = null;
        Ship? commander = null;
        short builderDroneId = 0;
        DataStationTypeIGC? stationType = null;
        Asteroid? rock = null;
        Asteroid? wrongRock = null;
        short side = 0;

        ShipSpawnerField.SetValue(server, (StaticCore _, Mission m, short id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            side = m.Sides[0].Id;
            builderDroneId = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeBuilder).Key;
            stationType = core.StationTypes.Values.First(s =>
                s.ClassId == 0 && (s.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);

            rock = new Asteroid(MkAsteroid(6401, new Vector3(615f, 425f, 455f), 60, sector.Id, stationType!.Value.AabmBuild));
            short wrongCap = stationType.Value.AabmBuild switch
            {
                DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),
                (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
                (short)(DataAsteroidIGC.AabmSpecial << 0) => (short)(DataAsteroidIGC.AabmSpecial << 1),
                (short)(DataAsteroidIGC.AabmSpecial << 1) => (short)(DataAsteroidIGC.AabmSpecial << 0),
                _ => DataAsteroidIGC.AabmBuildable,
            };
            wrongRock = new Asteroid(MkAsteroid(6402, new Vector3(626f, 421f, 448f), 60, sector.Id, wrongCap));
            m.PlaceAsteroid(rock, sector);
            m.PlaceAsteroid(wrongRock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            commander = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity) { SideId = side };
            m.PlaceShip(commander, sector);
            return (commander, sector);
        });

        server.Boot(Config("P425 Commanded Drone Build"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        client = Connect(server, "COMMANDER");
        var c = client;   // `client` is an out param — copy to a local for the lambda
        int seated = PumpUntil(server, () => c.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "commander seated");

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(commander);
        Assert.True(builderDroneId != 0); Assert.NotNull(stationType); Assert.NotNull(rock); Assert.NotNull(wrongRock);
        return (server!, mission!, sector!, commander!, builderDroneId, stationType!.Value, rock!, wrongRock!, side);
    }

    private static Ship SpawnBuilderDrone(Srv srv, Mission mission, Sector sector, short side, short builderDroneId, DataStationTypeIGC stationType, Vector3 pos)
    {
        var drone = (Ship)SpawnNpcShipMethod.Invoke(srv, new object[] { mission, sector, side, pos, (short?)builderDroneId })!;
        drone.BuilderStationType = stationType;
        drone.IsBuilding = true;   // reserved — suppress the P165 autonomous re-decide (see Core DirectNpcBuildOrderLiveTests)
        return drone;
    }

    /// <summary>⭐ LIVE: the MonoGame client's production sender drives a real
    /// Builder DRONE (not the client's own ship) to build the commanded rock —
    /// the exact payload the Insert key's <see cref="Game1.CommandOrderResolver"/>
    /// computes. Construction registers on the commanded rock; ticking to
    /// completion the rock → station transformation fires and the DRONE is
    /// consumed — the full P422 arc from the client.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_client_commands_builder_drone_to_build_creating_real_station()
    {
        Require.Artwork();
        var (srv, mission, sector, commander, builderDroneId, stationType, rock, _, side) = Boot(out var client);
        using (srv)
        using (client)
        {
            var rp = RockPos(rock);
            var drone = SpawnBuilderDrone(srv, mission, sector, side, builderDroneId, stationType, new SimVec(rp.X + 300f, rp.Y, rp.Z));
            Assert.NotEqual(commander.Id, drone.Id);
            Assert.Empty(sector.BuildingEffects);

            // The client's PRODUCTION sender — the exact method Game1's Insert
            // key handler calls with the CommandOrderResolver's payload.
            client.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            int started = PumpUntil(srv, () => sector.BuildingEffects.Count > 0, 2000);
            Assert.True(started < 2000, "construction did not register");
            var effect = Assert.Single(sector.BuildingEffects);
            Assert.Same(rock, effect.Target);
            Assert.Equal(stationType, effect.StationType);
            Console.WriteLine($"P425-CLIENT-START: commander {commander.Id} (MonoGame client) directed DRONE {drone.Id} BUILD accepted → construction of '{stationType.Name}' at rock {rock.Data.AsteroidId}");

            int tc = 0;
            const int MaxTicks = 4600;
            while (tc < MaxTicks && sector.BuildingEffects.Count > 0) { srv.TickSim(); tc++; Thread.Sleep(1); }
            Assert.True(tc < MaxTicks, $"build must complete within {MaxTicks} ticks (elapsed {tc})");

            Assert.DoesNotContain(rock, sector.Mineable);
            Assert.DoesNotContain(drone, sector.Models);   // the DRONE consumed
            var built = sector.StationRuntimes.Where(s => PositionsMatch(s.Data.Position, rp)).ToList();
            var builtStation = Assert.Single(built);
            Assert.Equal(stationType.StationTypeId, builtStation.Type.StationTypeId);
            Assert.Equal(side, builtStation.SideId);
            Console.WriteLine($"P425-CLIENT-BUILD: completed → station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {stationType.StationTypeId}, side {side}) at {rp}, drone {drone.Id} consumed");
        }
    }

    /// <summary>⭐ LIVE: an illegal direct-drone Build (a wrong-type rock) is
    /// refused by the server AND the refusal is SURFACED to the commanding
    /// MonoGame client as a private "Negative." chat (world.Events.Chats) — the
    /// "don't leave the player guessing why nothing happened" requirement.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_client_illegal_drone_build_refusal_reaches_client_chat()
    {
        Require.Artwork();
        var (srv, mission, sector, commander, builderDroneId, stationType, _, wrongRock, side) = Boot(out var client);
        using (srv)
        using (client)
        {
            var rp = RockPos(wrongRock);
            var drone = SpawnBuilderDrone(srv, mission, sector, side, builderDroneId, stationType, new SimVec(rp.X + 300f, rp.Y, rp.Z));

            client.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, wrongRock.Data.AsteroidId);
            int got = PumpUntil(srv, () => sector.BuildingEffects.Count > 0
                                       || client.World.Events.Chats.Any(c => c.Message.StartsWith("Negative.", System.StringComparison.Ordinal)), 2000);
            Assert.True(got < 2000, "expected either construction or a refusal ack");

            Assert.Empty(sector.BuildingEffects);               // refused — capability mismatch
            Assert.Contains(wrongRock, sector.Mineable);        // wrong-type rock untouched
            // The refusal surfaced to the commanding client among any ambient
            // broadcast chatter: a private "Negative." chat naming the reason.
            var ack = Assert.Single(client.World.Events.Chats, c => c.Message.StartsWith("Negative.", System.StringComparison.Ordinal));
            Console.WriteLine($"P425-CLIENT-REJECT: illegal drone build refused → client received \"{ack.Message}\"");
        }
    }
}
