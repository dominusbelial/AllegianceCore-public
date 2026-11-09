using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P977 live counterpart to <see cref="P977EjectAlliedRescueTests"/>:
/// an allied rescue station is exported through a real alliance snapshot, a
/// real player ejects, the production Game1 autopilot flies native SHIP_UPDATE
/// poses to that station, and the server's native allied-lifepod branch re-homes
/// the pilot at its own base (FedSrv.CPP:13264-13300).</summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P977AlliedEjectLiveTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const short EjectSectorId = 1051;

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, Time = 977,
        DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 97701,
    };

    private static void Drain(Srv server, ConnectionSession session, Func<bool> done, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 8)
        {
            lock (session.Gate) if (done()) return;
            server.TickSim();
            Thread.Sleep(2);
        }
        Assert.Fail($"client did not receive {what}");
    }

    private static Station InjectAlliedRescueStation(Mission mission, Sector sector, Station template)
    {
        // Preserve the known-good home station's model and authored orientation:
        // the production autopilot must enter its REAL landing bay, not merely
        // overlap a synthetic collision sphere.
        var type = template.Type with
        {
            SabmCapabilities = (short)(template.Type.SabmCapabilities | DataStationTypeIGC.SabmRescue),
        };
        var data = template.Data with
        {
            Position = [0f, 0f, 0f], ClusterId = sector.Id, SideId = 1, StationId = 9771,
            BpHull = 1, BpShield = 1, Name = "P977_ALLIED",
        };
        var station = new Station(data, type);
        mission.PlaceStation(station, sector);
        return station;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_eject_target_is_flown_to_the_allied_station_and_rehomed()
    {
        Require.Artwork();
        string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        Ship ship = null!;
        typeof(Srv).GetField("ShipSpawner", Private)!.SetValue(server,
            (Func<StaticCore, Mission, short, (Ship, Sector)>)((core, mission, id) =>
            {
                var sector = mission.Sectors.Single(s => s.Id == EjectSectorId);
                ship = new Ship(id, core.HullTypes[504], new SimVec(0f, 0f, 1800f),
                    SimVec.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(ship, sector);
                return (ship, sector);
            }));
        server.Boot(new ServerConfig
        {
            ArtPath = art, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,
            Games = [new()
            {
                Name = "P977 allied eject", Map = "XXV_simple.igc", Teams = 3,
                CivIds = [25, 3, 1], SideNames = ["Alpha", "Allied", "Enemy"],
                AllowAlliedViz = true, ShowMap = true,
            }],
        });
        var mission = server.Missions[0];
        mission.Sides.Single(s => s.Id == 0).AllyGroup = 0;
        mission.Sides.Single(s => s.Id == 1).AllyGroup = 0;
        mission.AlliedSidePairs.Add((0, 1));
        mission.Stage = MissionStage.Started;
        var ownHome = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .First(s => s.SideId == 0 && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        var allied = InjectAlliedRescueStation(mission, mission.Sectors.Single(s => s.Id == EjectSectorId), ownHome);

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P977-allied");
        session.SendLogonRequest(Request("P977-allied"));
        Drain(server, session, () => session.World.MyShipId is { } id
            && session.World.Ships.TryGetValue(id, out var own) && own.Latest is not null
            && session.World.Stations.ContainsKey(allied.StationId)
            && session.World.AreAllied(0, 1), "seat, allied station, and alliance snapshot");
        short shipId = session.World.MyShipId!.Value;

        var savedOut = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(log));
        try
        {
            ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, SimVec.Zero, null);
            Drain(server, session, () => session.World.Ships[shipId].HasPendingEject, "EJECT");
            double oxygenDeadline = ship.LifepodTimeToDie!.Value;

            var catalog = ShopCatalog.TryLoad(art)!;
            Game1.AutopilotTarget? target;
            byte cookie;
            var sim = new OwnShipSimulator();
            lock (session.Gate)
            {
                var own = session.World.Ships[shipId];
                cookie = own.Cookie;
                sim.Seed(own.Latest!.Position, own.Latest.Velocity, own.Latest.Orientation, own.Latest.TurnRates);
                target = Game1.ChooseEjectRescueTarget(session.World, 0, own.Latest.Position,
                    id => catalog.StationTypes!.TryGetValue(id, out var type) ? type.SabmCapabilities : (short)0,
                    id => catalog.ProbeTypes!.TryGetValue(id, out var type) ? type.EabmCapabilities : (short)0,
                    own.SectorId);
            }
            Assert.NotNull(target);
            Assert.Equal(Game1.TargetKind.Station, target!.Value.Kind);
            Assert.Equal(allied.StationId, target.Value.Id);
            Assert.NotEqual(ownHome.StationId, target.Value.Id);

            var game = P699HumanPodHomeLiveTests.CreateAutopilot(session, sim, art, target.Value);
            var cadence = new ShipUpdateCadence();
            var wall = Stopwatch.StartNew();
            while (ship.StationId is null && server.SimNow < oxygenDeadline && wall.Elapsed.TotalSeconds < 60)
            {
                lock (session.Gate)
                {
                    var own = session.World.Ships[shipId];
                    var ga = session.World.GlobalAttributesFor(own.SideId);
                    sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType,
                        ga.Get(GlobalAttributeSet.Thrust), ga.Get(GlobalAttributeSet.MaxSpeed),
                        ga.Get(GlobalAttributeSet.TurnRate), ga.Get(GlobalAttributeSet.TurnTorque)), 0);
                    if (own.HasPendingReset || own.Cookie != cookie)
                    {
                        sim.Seed(own.Latest!.Position, own.Latest.Velocity, own.Latest.Orientation, own.Latest.TurnRates);
                        session.World.ClearOwnShipResetFlag(shipId);
                        cookie = own.Cookie;
                    }
                }
                var controls = P699HumanPodHomeLiveTests.AutopilotControls(game);
                int movement = P699HumanPodHomeLiveTests.AutopilotMovement(game);
                sim.Step(.05f, controls, movement);
                uint stamp = (uint)(server.SimNow * 1000);
                if (cadence.Update(server.SimNow))
                    session.SendShipPose(stamp, cookie, ClientShipUpdate.FromPose(stamp, sim.Position, sim.Velocity,
                        sim.Orientation, movement, controls, sim.TurnRates, sim.Power));
                Thread.Sleep(2);
                server.TickSim();
            }

            string persistedLog = log.ToString();
            string state = $"station={ship.StationId?.ToString() ?? "flying"} sector={ship.Sector?.Id} "
                + $"position={ship.Position} simNow={server.SimNow:F2} oxygenDeadline={oxygenDeadline:F2} "
                + $"target={target.Value.Kind}#{target.Value.Id}\n{persistedLog}";
            Assert.True(persistedLog.Contains($"[allied-dock] ship {shipId}: lifepod at allied station {allied.StationId} (side 1)"), state);
            Assert.True(ship.StationId == ownHome.StationId, state);
            Assert.True(server.SimNow < oxygenDeadline, "oxygen expiry must never count as allied rescue\n" + state);
        }
        finally { Console.SetOut(savedOut); }
    }
}
