using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P425 — the DIRECT command of a same-side NPC/drone to BUILD from the client.
/// A Builder is never player-piloted (native spawns it as a command-driven
/// drone, FedSrv.CPP:2220,11351); commanding one means the commander sends a
/// real <c>ORDER_CHANGE</c> naming the DRONE as <c>ShipID</c> (not themselves),
/// and the drone obeys immediately — no accept/reject (distinct from P414/P424's
/// queued flow for commanding other REAL players). These live-loopback tests
/// prove the whole arc over the REAL wire against the REAL in-process server,
/// with the commanding peer (a bot) and a real Builder drone on the SAME side:
/// <list type="number">
/// <item><b>Legal full arc</b> — a real Builder drone (PilotType==6) with a
/// configured station type is commanded to build a compatible rock; the server
/// resolves the drone as the commandee (same side), validates the shared
/// legality body, and starts the timed 40 s construction; ticking to completion
/// the rock → station transformation fires, the DRONE is consumed, and the
/// station appears on the drone's side — the exact P422 arc, but driven by
/// commanding an owned NPC rather than flying it yourself.</item>
/// <item><b>Non-Builder drone refused</b> — a non-Builder-pilot drone is refused
/// (native's Builder-only gate), and the refusal is SURFACED to the commanding
/// player's own client as a private "Negative." chat (native's
/// SendChat(CHAT_INDIVIDUAL, "Negative.") command-refusal ack).</item>
/// <item><b>Capability-mismatch rock refused</b> — not every rock is buildable.</item>
/// <item><b>No-station-type drone refused</b> — a Builder drone with nothing to
/// build is refused (with the surfacing ack).</item>
/// </list>
/// Harness mirrors P165/P422 (booted in-process server + seated bot commander).
/// </summary>
[Collection("LiveNetwork")]
public sealed class DirectNpcBuildOrderLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P425 Direct Drone Build Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 873u, DwCookie = 0x873u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66500000000000003ul, DrmHash = "p425drone",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
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

    private static Vector3 RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

    /// <summary>Boot the in-process server + seated commander bot (a plain
    /// non-Builder peer on side 0 — the COMMANDER, not the builder) and assemble
    /// the single-sector fixture: a real compatible rock + a wrong-type rock, the
    /// Builder drone type id (PilotType==6) + a non-Builder drone type id for the
    /// negative gate, and a real buildable techbase station type.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Ship Commander, short BuilderDroneId, short NonBuilderDroneId, DataStationTypeIGC StationType, Asteroid Rock, Asteroid WrongRock, short Side)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        var core = Core();
        Mission? mission = null;
        Sector? sector = null;
        Ship? commander = null;
        short builderDroneId = 0;
        short nonBuilderDroneId = 0;
        DataStationTypeIGC? stationType = null;
        Asteroid? rock = null;
        Asteroid? wrongRock = null;
        short side = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            side = m.Sides[0].Id;   // the commander's side (side 0)

            // A real Builder drone type (PilotType==6) + a real NON-Builder drone
            // type (for the negative gate — native refuses a non-c_ptBuilder).
            var bld = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeBuilder);
            builderDroneId = bld.Key;
            nonBuilderDroneId = core.DroneTypes.First(kv => kv.Value.PilotType != Ship.PilotTypeBuilder).Key;

            // A real buildable techbase station type (Garrison-class, buildable).
            stationType = core.StationTypes.Values.First(s =>
                s.ClassId == 0 && (s.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);

            // Compatible rock + wrong-type rock (disjoint capability).
            rock = new Asteroid(MkAsteroid(6321, new Vector3(620f, 420f, 450f), 60, sector.Id, stationType!.Value.AabmBuild));
            short wrongCap = stationType.Value.AabmBuild switch
            {
                DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),  // carbon 0x40
                (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
                (short)(DataAsteroidIGC.AabmSpecial << 0) => (short)(DataAsteroidIGC.AabmSpecial << 1),
                (short)(DataAsteroidIGC.AabmSpecial << 1) => (short)(DataAsteroidIGC.AabmSpecial << 0),
                _ => DataAsteroidIGC.AabmBuildable,
            };
            Assert.Equal(0, stationType.Value.AabmBuild & wrongCap);   // provably disjoint
            wrongRock = new Asteroid(MkAsteroid(6322, new Vector3(630f, 417f, 442f), 60, sector.Id, wrongCap));
            m.PlaceAsteroid(rock, sector);
            m.PlaceAsteroid(wrongRock, sector);

            // The commander peer: a plain fighter (non-Builder) on side 0.
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            commander = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = side };
            m.PlaceShip(commander, sector);
            return (commander, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180)
        bot = new FltBot();
        var b = bot;
        b.Connect(server.BoundPort, "p425_drone");
        b.SendLogonRequest(Request("p425_drone"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(commander);
        Assert.True(builderDroneId != 0); Assert.True(nonBuilderDroneId != 0);
        Assert.NotNull(stationType); Assert.NotNull(rock); Assert.NotNull(wrongRock);
        return (server!, mission!, sector!, commander!, builderDroneId, nonBuilderDroneId, stationType!.Value, rock!, wrongRock!, side);
    }

    /// <summary>Spawn a real Builder drone on the COMMANDER'S own side (so
    /// FindSameSideShip resolves it as the commandee), with a configured station
    /// type, sitting near the compatible rock in the same sector.
    /// WaitsForBuildOrder suppresses autonomous build selection while the
    /// commander's order is in flight; IsBuilding reflects actual drilling.</summary>
    private static Ship SpawnBuilderDrone(Srv srv, Mission mission, Sector sector, short side, short builderDroneId, DataStationTypeIGC stationType, Vector3 pos)
    {
        var drone = srv.SpawnNpcShip(mission, sector, side, pos, builderDroneId);
        Assert.Equal(Ship.PilotTypeBuilder, drone.PilotType);   // gate premise: really a Builder drone
        drone.BuilderStationType = stationType;                  // what it constructs (P285 stand-in for the stocked station type)
        drone.WaitsForBuildOrder = true;                         // native stand-by until the commander orders a build
        return drone;
    }

    /// <summary>⭐ Primary leg: the commander (a plain peer) commands a real
    /// Builder DRONE (not itself) to build the compatible rock over the real
    /// wire — ORDER_CHANGE { ShipID = the DRONE, commandID=c_cidBuild,
    /// OT_asteroid }. The server resolves the drone as the commandee, validates
    /// the shared legality body, starts the timed 40 s construction, and ticking
    /// to completion the rock → station fires, the DRONE is consumed, and the
    /// station appears on the drone's side — the full P422 arc driven by
    /// commanding an owned NPC.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Commander_directs_builder_drone_to_build_creating_real_station()
    {
        Require.Artwork();
        var (srv, mission, sector, commander, builderDroneId, _, stationType, rock, _, side) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var rp = RockPos(rock);
            var drone = SpawnBuilderDrone(srv, mission, sector, side, builderDroneId, stationType, new Vector3(rp.X + 300f, rp.Y, rp.Z));
            Assert.NotEqual(commander.Id, drone.Id);   // a different ship — commanding SOMEONE ELSE
            Assert.False(drone.IsBuilding);            // waiting for the commander's order, not drilling yet
            Assert.Empty(sector.BuildingEffects);

            // ── The commander sends the direct Build order over the real wire,
            //    ShipID = the DRONE (not self) — the exact payload the MonoGame
            //    Insert key sends for a Builder-role commandee + asteroid target.
            bot.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0, 2000);
            var effect = Assert.Single(sector.BuildingEffects);
            Assert.Same(rock, effect.Target);                 // built at the commanded rock
            Assert.Equal(stationType, effect.StationType);    // the drone's configured station type
            Assert.True(drone.IsBuilding);                    // the drone committed
            Console.WriteLine($"P425-START: commander {commander.Id} directed DRONE {drone.Id} (side {side}) BUILD accepted → construction of '{stationType.Name}' started at asteroid {rock.Data.AsteroidId}");

            // ── Tick to completion (~40 s) → rock → station, DRONE consumed ──
            int tc = 0;
            const int MaxTicks = 4600;
            while (tc < MaxTicks && sector.BuildingEffects.Count > 0) { srv.TickSim(); tc++; Thread.Sleep(1); }
            Assert.True(tc < MaxTicks, $"build must complete within {MaxTicks} ticks (elapsed {tc})");

            Assert.DoesNotContain(rock, sector.Mineable);     // the rock became a station
            Assert.DoesNotContain(drone, sector.Models);      // the DRONE consumed itself
            Assert.Empty(sector.BuildingEffects);
            Assert.False(drone.IsBuilding);

            var built = sector.StationRuntimes.Where(s => PositionsMatch(s.Data.Position, rp)).ToList();
            var builtStation = Assert.Single(built);
            Assert.Equal(stationType.StationTypeId, builtStation.Type.StationTypeId);
            Assert.Equal(side, builtStation.SideId);          // the drone's side
            Assert.Contains(builtStation.Data, sector.Stations);
            Console.WriteLine($"P425-BUILD: completed → rock {rock.Data.AsteroidId} gone, station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {stationType.StationTypeId}, side {side}) at {rp}, drone {drone.Id} consumed");
        }
    }

    /// <summary>⭐ Negative: commanding a NON-Builder-pilot drone to build is
    /// refused (native's Builder-only gate), AND the refusal is SURFACED to the
    /// commanding player's own client as a private "Negative." chat (native's
    /// SendChat(CHAT_INDIVIDUAL, "Negative.") command-refusal ack).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Commander_directing_non_builder_drone_to_build_is_refused_and_acknowledged()
    {
        Require.Artwork();
        var (srv, mission, sector, _, builderDroneId, nonBuilderDroneId, stationType, rock, _, side) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var rp = RockPos(rock);
            // A real NON-Builder drone on the commander's side (still same-side
            // resolvable) — with a station type configured so the ONLY failing
            // gate is the Builder-role one. Reserved like the Builder drone so
            // no autonomous decision interferes.
            var drone = srv.SpawnNpcShip(mission, sector, side, new Vector3(rp.X + 300f, rp.Y, rp.Z), nonBuilderDroneId);
            Assert.NotEqual(Ship.PilotTypeBuilder, drone.PilotType);
            drone.BuilderStationType = stationType;
            drone.IsBuilding = true;

            bot.ResetChatReceived();
            bot.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0 || bot.Chats.Any(), 2000);

            Assert.Empty(sector.BuildingEffects);   // refused — not a Builder drone
            // The refusal reached the commanding client: a private "Negative."
            // chat naming the reason.
            var ack = Assert.Single(bot.Chats);
            Assert.StartsWith("Negative.", ack.Message, StringComparison.Ordinal);
            Assert.Contains("non-Builder", ack.Message, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"P425-REJECT: commander directing non-Builder drone {drone.Id} to build refused — surfaced to commander: \"{ack.Message}\"");
        }
    }

    /// <summary>⭐ Negative: a Builder drone ordered to build a WRONG-TYPE rock
    /// (capability mismatch) is refused — native HasCapability(m_abmOrders),
    /// shipIGC.h:1702 — with the refusal acked.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Commander_directing_builder_drone_at_wrong_type_rock_is_refused()
    {
        Require.Artwork();
        var (srv, mission, sector, _, builderDroneId, _, stationType, _, wrongRock, side) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var rp = RockPos(wrongRock);
            var drone = SpawnBuilderDrone(srv, mission, sector, side, builderDroneId, stationType, new Vector3(rp.X + 300f, rp.Y, rp.Z));

            bot.ResetChatReceived();
            bot.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, wrongRock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0 || bot.Chats.Any(), 2000);

            Assert.Empty(sector.BuildingEffects);          // refused — capability mismatch
            Assert.Contains(wrongRock, sector.Mineable);   // wrong-type rock untouched
            var ack = Assert.Single(bot.Chats);
            Assert.StartsWith("Negative.", ack.Message, StringComparison.Ordinal);
            Console.WriteLine($"P425-REJECT: Builder drone {drone.Id} at wrong-type rock {wrongRock.Data.AsteroidId} refused — \"{ack.Message}\"");
        }
    }

    /// <summary>⭐ Negative: a Builder drone with NO configured station type
    /// (nothing to build) is refused — with the refusal acked.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Commander_directing_builder_drone_with_no_station_type_is_refused()
    {
        Require.Artwork();
        var (srv, mission, sector, _, builderDroneId, _, _, rock, _, side) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var rp = RockPos(rock);
            var drone = srv.SpawnNpcShip(mission, sector, side, new Vector3(rp.X + 300f, rp.Y, rp.Z), builderDroneId);
            Assert.Equal(Ship.PilotTypeBuilder, drone.PilotType);
            Assert.Null(drone.BuilderStationType);   // nothing to build

            bot.ResetChatReceived();
            bot.SendOrderChange(drone.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
            Pump(srv, () => sector.BuildingEffects.Count > 0 || bot.Chats.Any(), 2000);

            Assert.Empty(sector.BuildingEffects);
            Assert.False(drone.IsBuilding);
            var ack = Assert.Single(bot.Chats);
            Assert.StartsWith("Negative.", ack.Message, StringComparison.Ordinal);
            Console.WriteLine($"P425-REJECT: Builder drone {drone.Id} with no station type refused — \"{ack.Message}\"");
        }
    }
}
