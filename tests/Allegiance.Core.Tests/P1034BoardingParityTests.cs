using Allegiance.Core.Tests.Transport;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>R1b F3: real booted server, production wire dispatch and captured replies.</summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public class P1034BoardingParityTests
{
    private readonly bool _webSocket;
    public P1034BoardingParityTests() { }
    protected P1034BoardingParityTests(bool webSocket) => _webSocket = webSocket;

    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(int Conn, byte[] Message)> Output = [];
        private readonly WebSocketRegressionIngress? _ingress;
        public World(bool webSocket, bool adminEnabled = false, bool twoMissions = false)
        {
            Require.Artwork();
            GameConfig Game(string name) => new() { Name = name, Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false };
            Server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = twoMissions ? [Game("P1034"), Game("Other")] : [Game("P1034")],
                LiveSettings = new() { AdminCheats = new() { Enabled = adminEnabled,
                    AllowNames = ["ADMIN"], Secret = "p1034-test" } },
            });
            if (webSocket) _ingress = new WebSocketRegressionIngress(Server, 4);
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => ((int)c.Value, m)));
        }
        public Mission Mission => Server.Missions[0];
        public void Send(int conn, IFedMessage message)
        {
            var packet = FedPacket.Pack([message.Encode()]);
            if (_ingress is null) Server.TestProcessDatagram(new ConnectionId((uint)conn), packet);
            else _ingress.Send(new ConnectionId((uint)conn), packet);
        }
        public List<byte[]> Sent(int conn, ushort fmid) => Output.Where(o => o.Conn == conn
            && FedMessageHeader.Read(o.Message).Fmid == fmid).Select(o => o.Message).ToList();
        public Ship Seat(int conn, string name, string credential = "")
        {
            Send(conn, new LogonRequest { CharacterName = name, ZoneTicket = null, CDKey = credential,
                MissionPassword = "", FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue,
                CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 1ul, DrmHash = "p1034" });
            var ack = LogonAck.Decode(Sent(conn, LogonAck.Fmid)[^1]);
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
            Send(conn, new PositionReq { SideId = 0 });
            for (int i = 0; i < 400 && Server.Ships.All(s => s.Ship.Id != ack.ShipID); i++) Server.TickSim();
            return Server.Ships.Single(s => s.Ship.Id == ack.ShipID).Ship;
        }
        public void Dispose() { _ingress?.Dispose(); Server.Dispose(); }
    }

    private static void Invoke(World w, string method, params object[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w.Server, args);
    private static Srv.LiveShip Live(World w, Ship s) => w.Server.Ships.Single(l => l.Ship == s);
    private static Dictionary<ConnectionId, Srv.LiveShip> Peers(World w) =>
        (Dictionary<ConnectionId, Srv.LiveShip>)typeof(Srv).GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.Server)!;
    private static void Place(World w, Ship ship, Sector sector, short station)
    {
        ship.Sector!.Remove(ship);
        sector.Add(ship);
        ship.Sector = sector;
        ship.StationId = station;
        // Prepare a coherent peer in the other sector without using the helper under test.
        Peers(w)[new ConnectionId(ship.Name == "PILOT" ? 1u : 2u)] = Live(w, ship) with { Sector = sector };
    }
    private static void Equip(World w, Ship pilot)
    {
        pilot.ClearLoadout();
        pilot.ChangeHull(w.Mission.Core.HullTypes[792]);
        var weapon = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon);
        Assert.NotNull(pilot.MountPart(weapon, 1, (byte)pilot.HullType.MaxFixedWeapons, 255));
    }
    private static void Board(World w, Ship pilot, Ship child)
    {
        w.Mission.Stage = MissionStage.Started;
        w.Send(2, new BoardShip { SidParent = pilot.Id });
        Assert.Same(pilot, child.ParentShip);
    }
    private static void AssertSector(World w, Ship ship, Sector target)
    {
        Assert.Same(target, ship.Sector);
        Assert.Same(target, Live(w, ship).Sector);
        Assert.Single(target.Models, m => ReferenceEquals(m, ship));
        Assert.All(w.Mission.Sectors.Where(s => s != target), s => Assert.DoesNotContain(ship, s.Models));
    }

    [SkippableTheory]
    [InlineData(false, "ADMIN", "p1034-test", false)]
    [InlineData(true, "ADMIN", "", false)]
    [InlineData(true, "ORDINARY", "p1034-test", false)]
    [InlineData(true, "ADMIN", "p1034-test", true)]
    public void Observer_requires_enabled_authenticated_allowlisted_admin(bool enabled, string name, string secret, bool allowed)
    {
        using var w = new World(_webSocket, enabled);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, name, secret);
        pilot.ClearLoadout(); // No equipped turret: observer fallback.
        var hull = child.HullType;
        long money = Live(w, child).Wallet.Money;
        w.Mission.Stage = MissionStage.Started;
        w.Output.Clear();
        w.Send(2, new BoardShip { SidParent = pilot.Id });
        Assert.Equal(allowed, child.ParentShip == pilot);
        if (allowed) Assert.Empty(w.Sent(2, BoardNack.Fmid));
        else
        {
            Assert.Single(w.Sent(2, BoardNack.Fmid));
            Assert.Empty(w.Sent(1, BoardNack.Fmid));
            Assert.Equal(hull, child.HullType);
            Assert.Equal(money, Live(w, child).Wallet.Money);
            Assert.Empty(pilot.ChildShips);
        }
    }

    [SkippableFact]
    public void Cross_sector_board_leave_buy_launch_uses_carriers_station_sector()
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        var homeStation = P550TestDockHome.CapLandHome(w.Mission, 0);
        var home = w.Mission.Sectors.Single(s => s.Id == homeStation.ClusterId);
        Place(w, pilot, home, homeStation.StationId);
        short dock = homeStation.StationId;
        // Research is available at home; a friendly forward hangar still lacks
        // the local technology to sell this paid capital hull.
        var station = home.Stations.Single(s => s.StationId == dock);
        var stype = w.Mission.Core.StationTypes[station.StationTypeId];
        var side = w.Mission.Sides.Single(s => s.Id == child.SideId);
        var canBuy = typeof(Srv).GetMethod("StationSellsHull", BindingFlags.NonPublic | BindingFlags.Static)!;
        var hull = w.Mission.Core.HullTypes[792];
        side.DevelopmentTechs = side.DevelopmentTechs.Or(new TechTreeBitMask(
            hull.TtbmRequired.Select((b, i) => (byte)(b & ~stype.TtbmLocal[i])).ToArray()));
        Assert.True((bool)canBuy.Invoke(null, [stype, side, hull])!);
        var forwardType = w.Mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmLand)
            && !(bool)canBuy.Invoke(null, [t, side, hull])!);
        var forward = w.Mission.Sectors.First(s => s != home && s.Stations.Count > 0);
        var forwardStation = forward.Stations[0] with { SideId = 0, StationId = 29000, StationTypeId = forwardType.StationTypeId };
        forward.AttachStation(new Station(forwardStation, forwardType));
        Place(w, child, forward, forwardStation.StationId);
        Board(w, pilot, child);
        w.Send(2, new BoardShip { SidParent = -1 });
        Assert.Null(child.ParentShip);
        Assert.Equal(dock, child.StationId);
        Live(w, child).Wallet.Money = 1000000;
        w.Output.Clear();
        w.Send(2, new BuyLoadoutRequest { HullId = hull.HullId, FLaunch = true, Parts = [] });
        Assert.Equal(hull.HullId, child.HullType.HullId);
        Assert.True(BuyLoadoutAck.Decode(w.Sent(2, BuyLoadoutAck.Fmid)[^1]).FLaunch);
        Assert.Null(child.StationId);
        Assert.Equal(home.Id, SetCluster.Decode(w.Sent(2, SetCluster.Fmid)[^1]).SectorId);
        AssertSector(w, child, home);
        Assert.True((child.Position - new Vector3(station.Position[0], station.Position[1], station.Position[2])).Length() < 2000);
    }

    [SkippableTheory]
    [InlineData("warp")]
    [InlineData("ripcord")]
    [InlineData("teleport")]
    [InlineData("undock")]
    public void Passengers_follow_sector_membership_peer_routing_and_launch_burst(string path)
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        Board(w, pilot, child);
        var old = pilot.Sector!;
        var target = w.Mission.Sectors.First(s => s != old && s.Stations.Count > 0);
        var station = target.Stations[0];
        w.Output.Clear();
        if (path == "teleport")
        {
            // Keep the expensive hull; this fixture exercises the bNewHull=false branch.
            w.Mission.Sides[0].DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)255, 50).ToArray());
            var original = old.Stations.Single(s => s.StationId == pilot.StationId);
            station = station with { SideId = 0, StationTypeId = original.StationTypeId };
            target.RemoveStation(target.StationRuntimes.Single(s => s.StationId == station.StationId));
            target.AttachStation(new Station(station, w.Mission.Core.StationTypes[station.StationTypeId]));
            w.Send(1, new Docked { StationId = station.StationId });
            Assert.False(TeleportAck.Decode(w.Sent(1, TeleportAck.Fmid)[^1]).BNewHull);
            Assert.Equal(station.StationId, child.StationId);
            AssertSector(w, child, target);
            Assert.Empty(w.Sent(2, SetCluster.Fmid)); // A hangar move is not a launch.
            w.Send(1, new Docked { StationId = -1 });
        }
        else if (path == "undock")
        {
            // Defensive stale peer/model sector: station identity determines launch.
            pilot.StationId = child.StationId = station.StationId;
            w.Send(1, new Docked { StationId = -1 });
        }
        else
        {
            pilot.StationId = child.StationId = null;
            if (path == "warp")
            {
                old.Remove(pilot); target.Add(pilot); pilot.Sector = target;
                pilot.Position = new Vector3(1234, 5678, 9012);
                Invoke(w, "OnShipWarped", pilot, old, target);
            }
            else
            {
                pilot.SetRipcordModel(new Ship.RipcordTarget((short)IgcObjectType.Station, station.StationId, target.Id));
                Invoke(w, "PerformRipcordTeleport", pilot);
            }
        }
        AssertSector(w, pilot, target);
        AssertSector(w, child, target);
        Assert.Same(pilot, child.ParentShip);
        Assert.Null(child.StationId);
        var burst = SetCluster.Decode(Assert.Single(w.Sent(2, SetCluster.Fmid)));
        Assert.Equal(target.Id, burst.SectorId);
        Assert.Equal(pilot.Id, burst.Ship.ShipId);
        Assert.Equal(pilot.Position, burst.Ship.Position);
        if (path != "ripcord") Assert.Equal(w.Sent(1, SetCluster.Fmid)[^1], w.Sent(2, SetCluster.Fmid)[^1]);
    }

    [SkippableFact]
    public void Leave_is_scoped_to_the_boarders_mission()
    {
        using var w = new World(_webSocket, twoMissions: true);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        Board(w, pilot, child);
        var other = w.Server.Missions[1];
        var foreign = new Ship(30000, pilot.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        other.PlaceShip(foreign, other.Sectors[0]);
        Peers(w)[new ConnectionId(3)] = Live(w, pilot) with { Ship = foreign, Sector = other.Sectors[0] };
        w.Output.Clear();
        w.Send(2, new BoardShip { SidParent = -1 });
        Assert.Single(w.Sent(1, LeaveShip.Fmid));
        Assert.Single(w.Sent(2, LeaveShip.Fmid));
        Assert.Empty(w.Sent(3, LeaveShip.Fmid));
    }
    [SkippableFact]
    public void Cross_sector_board_is_coherent_while_still_docked()
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        var target = pilot.Sector!;
        var other = w.Mission.Sectors.First(s => s != target && s.Stations.Count > 0);
        Place(w, child, other, other.Stations[0].StationId);
        w.Output.Clear();
        Board(w, pilot, child);
        AssertSector(w, child, target);
        Assert.Equal(pilot.StationId, child.StationId);
        Assert.Empty(w.Sent(2, SetCluster.Fmid));
    }

    [SkippableFact]
    public void Full_turrets_refuse_ordinary_boarder_without_mutating_hardware_or_money()
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        for (short mount = pilot.HullType.MaxFixedWeapons; mount < pilot.HullType.MaxWeapons; mount++)
        {
            var occupant = new Ship((short)(29000 + mount), child.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity)
                { ParentShip = pilot, TurretId = mount };
            pilot.AddChildShip(occupant);
        }
        var hull = child.HullType;
        var parts = child.Parts.ToArray();
        var wallet = Live(w, child).Wallet.Money;
        w.Mission.Stage = MissionStage.Started;
        w.Output.Clear();
        w.Send(2, new BoardShip { SidParent = pilot.Id });
        Assert.Null(child.ParentShip);
        Assert.Equal(hull, child.HullType);
        Assert.Equal(parts, child.Parts);
        Assert.Equal(wallet, Live(w, child).Wallet.Money);
        Assert.Single(w.Sent(2, BoardNack.Fmid));
        Assert.Empty(w.Sent(1, BoardNack.Fmid));
    }

    [SkippableFact]
    public void Same_sector_arrivals_keep_both_gunners_and_send_each_the_identical_source_burst()
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var first = w.Seat(2, "CREW");
        var second = w.Seat(3, "OTHER");
        Equip(w, pilot);
        var weapon = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon);
        Assert.NotNull(pilot.MountPart(weapon, 1, (byte)(pilot.HullType.MaxFixedWeapons + 1), 255));
        Board(w, pilot, first);
        w.Send(3, new BoardShip { SidParent = pilot.Id });
        Assert.Same(pilot, second.ParentShip);
        var sector = pilot.Sector!;
        pilot.StationId = first.StationId = second.StationId = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            w.Output.Clear();
            pilot.Position = new Vector3(1234 + attempt * 500, 5678, 9012);
            Invoke(w, "OnShipWarped", pilot, sector, sector);
            foreach (var passenger in new[] { first, second }) AssertSector(w, passenger, sector);
            var source = Assert.Single(w.Sent(1, SetCluster.Fmid));
            Assert.Equal(source, Assert.Single(w.Sent(2, SetCluster.Fmid)));
            Assert.Equal(source, Assert.Single(w.Sent(3, SetCluster.Fmid)));
        }
    }

    [SkippableFact]
    public void Cross_sector_board_immediate_status_uses_current_peer_sector()
    {
        using var w = new World(_webSocket);
        var pilot = w.Seat(1, "PILOT");
        var child = w.Seat(2, "CREW");
        Equip(w, pilot);
        var target = pilot.Sector!;
        var other = w.Mission.Sectors.First(s => s != target && s.Stations.Count > 0);
        Place(w, child, other, other.Stations[0].StationId);
        w.Output.Clear();
        Board(w, pilot, child);
        var status = ShipStatusMessage.Decode(Assert.Single(w.Sent(2, ShipStatusMessage.Fmid)));
        Assert.Equal(target.Id, status.SectorId);
        Assert.Equal(pilot.StationId, status.StationId);
        Assert.Equal(pilot.Id, status.ParentId);
    }

}

/// <summary>The same R1b assertions through real WebSocket input.</summary>
[Collection("LiveNetwork")]
public sealed class P1048BoardingParityTests : P1034BoardingParityTests
{
    public P1048BoardingParityTests() : base(true) { }
}
