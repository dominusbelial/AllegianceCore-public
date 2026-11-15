using System.Reflection;
using System.Collections.Concurrent;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using Allegiance.Server;
using Allegiance.ClientCore.Session;

namespace Allegiance.Core.Tests;

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P1026SideDepartureTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly ConcurrentQueue<(int Conn, byte[] Message)> Output = new();
        public World(Action<ServerConfig>? configure = null)
        {
            Require.Artwork();
            var cfg = new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = [new GameConfig { Name = "P1026", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
            };
            configure?.Invoke(cfg);
            Config = cfg;
            Server.Boot(cfg);
            Server._testSendCapture = (c, p) =>
            {
                foreach (var message in FedPacket.Unpack(p)) Output.Enqueue(((int)c.Value, message));
            };
        }
        public ServerConfig Config = null!;
        public Mission Mission => Server.Missions[0];
        public static LogonRequest Request(string name, string password = "") => new()
        {
            CharacterName = name, ZoneTicket = null, CDKey = "", MissionPassword = password,
            FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 1ul, DrmHash = "r1",
        };
        public void Send(int conn, IFedMessage message) =>
            Server.TestProcessDatagram(new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public List<byte[]> SentTo(int conn, ushort fmid) =>
            Output.Where(o => o.Conn == conn && FedMessageHeader.Read(o.Message).Fmid == fmid).Select(o => o.Message).ToList();
        public LogonAck Logon(int conn, LogonRequest req)
        {
            Send(conn, req);
            return LogonAck.Decode(SentTo(conn, LogonAck.Fmid)[^1]);
        }
        /// <summary>Log on, pick <paramref name="side"/> and tick until seated.</summary>
        public Ship Seat(int conn, string name, short side)
        {
            var ack = Logon(conn, Request(name));
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
            Send(conn, new PositionReq { SideId = side });
            for (int i = 0; i < 400 && Server.Ships.All(s => s.Ship.Id != ack.ShipID); i++) Server.TickSim();
            return Server.Ships.First(s => s.Ship.Id == ack.ShipID).Ship;
        }
        public void Dispose() => Server.Dispose();
    }

    private static Srv.LiveShip Live(World w, Ship ship) => w.Server.Ships.Single(s => s.Ship == ship);
    private static void Prepare(World w, out Ship pilot, out Ship mate, out Ship enemy)
    {
        mate = w.Seat(1, "MATE", 0);
        pilot = w.Seat(2, "PILOT", 0);
        enemy = w.Seat(3, "ENEMY", 1);
        w.Mission.Stage = MissionStage.Started;
        w.Mission.Sides[1].AutoAccept = true;
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Running_switch_resets_hardware_and_docks_at_new_base(bool flying, bool approval)
    {
        using var w = new World();
        Prepare(w, out var pilot, out _, out var enemy);
        var oldSector = pilot.Sector!;
        var oldHull = w.Mission.Core.HullTypes.Values.First(h => h.Price > 0 && h.GroupId >= 0);
        pilot.ChangeHull(oldHull);
        var part = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon);
        pilot.MountPart(part, 1, 0, 1);
        pilot.AddKillBonus();
        pilot.SetCommand(OrderChange.CmdAccepted, enemy, OrderChange.CidAttack);
        pilot.GotoWaypoint = new GotoOrder(GotoKind.Waypoint, oldSector.Id, new Vector3(9000, 8000, 7000));
        if (flying) { pilot.StationId = null; pilot.Position = new Vector3(1900, 2500, 3100); }
        w.Mission.Sides[1].AutoAccept = !approval;
        w.Send(2, new PositionReq { SideId = 1 });
        if (approval)
        {
            Assert.Equal((short)-2, pilot.SideId);
            Assert.Null(pilot.Sector);
            Assert.Null(pilot.StationId);
            Assert.DoesNotContain(pilot, oldSector.Models);
            Assert.Empty(pilot.Parts);
            w.Send(3, new PositionAck { SideId = 1, ShipId = pilot.Id, FAccepted = true });
        }
        Assert.Equal((short)1, pilot.SideId);
        Assert.Empty(pilot.Parts);
        Assert.All(pilot.CargoHold, p => Assert.Null(p));
        Assert.Equal(w.Mission.Sides[1].Civilization.Lifepod, pilot.HullType.HullId);
        Assert.Equal(0, pilot.RefundableHullValue);
        Assert.Equal(0, pilot.KillBonus);
        Assert.Null(pilot.GotoWaypoint);
        Assert.Null(pilot.AcceptedShipTarget);
        Assert.Equal(OrderChange.CidNone, pilot.AcceptedShipCommandId);
        var home = w.Mission.Sectors.SelectMany(s => s.StationRuntimes).Single(s => s.StationId == pilot.StationId);
        Assert.Equal((short)1, home.SideId);
        Assert.True(home.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.Equal(pilot.Sector, Live(w, pilot).Sector);
        Assert.Contains(w.SentTo(1, QuitSide.Fmid), m => QuitSide.Decode(m).ShipId == pilot.Id);
        Assert.Contains(w.SentTo(2, DockedNotify.Fmid), m => DockedNotify.Decode(m).StationId == pilot.StationId);
        Assert.NotEmpty(w.SentTo(2, EnterGame.Fmid));
        Assert.NotEmpty(w.SentTo(2, ClusterInfo.Fmid));
    }

    [SkippableFact]
    public void Quit_pays_wallet_plus_full_hull_mounted_and_cargo_value_once()
    {
        using var w = new World();
        Prepare(w, out var pilot, out var mate, out _);
        var hull = w.Mission.Core.HullTypes.Values.First(h => h.GroupId >= 0 && h.Price > 0);
        pilot.ClearLoadout();
        pilot.ChangeHull(hull);
        var part = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon) with { Price = 73 };
        Assert.NotNull(pilot.MountPart(part, 1, 0, 1, freeSpawn: true));
        Assert.NotNull(pilot.MountPart(part, 1, 255, 1));
        Live(w, pilot).Wallet.Money = 250;
        long before = Live(w, mate).Wallet.Money;
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        Assert.Equal(before + 250 + hull.Price + 2 * part.Price, Live(w, mate).Wallet.Money);
        Assert.Equal(0, Live(w, pilot).Wallet.Money);
        Assert.Null(pilot.StationId);
        Assert.Null(pilot.Sector);
        Assert.Empty(pilot.Parts);
        Assert.All(pilot.CargoHold, p => Assert.Null(p));
        long paid = Live(w, mate).Wallet.Money;
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        Assert.Equal(paid, Live(w, mate).Wallet.Money);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Departing_pilot_promotes_first_passenger_and_keeps_crew_on_old_side(bool observer)
    {
        using var w = new World();
        Prepare(w, out var pilot, out var mate, out _);
        w.Mission.Stage = MissionStage.NotStarted;
        var other = w.Seat(4, "CREW", 0);
        w.Mission.Stage = MissionStage.Started;
        pilot.StationId = null;
        var hull = w.Mission.Core.HullTypes.Values.First(h => h.Price > 0 && h.GroupId >= 0);
        pilot.ClearLoadout();
        pilot.ChangeHull(hull);
        var weapon = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon);
        Assert.NotNull(pilot.MountPart(weapon, 1, 0, 1));
        foreach (var child in new[] { mate, other })
        {
            child.ClearLoadout(); child.StationId = null;
            child.ParentShip = pilot; child.TurretId = child == mate && !observer ? (short)0 : null;
            pilot.AddChildShip(child);
        }
        if (!observer) pilot.Weapons[0].SetGunner(mate);
        long before = Live(w, mate).Wallet.Money + Live(w, other).Wallet.Money;
        w.Output.Clear();
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        Assert.Null(mate.ParentShip);
        Assert.Equal(hull.HullId, mate.HullType.HullId);
        Assert.Single(mate.Parts);
        Assert.Same(mate, other.ParentShip);
        Assert.Equal((short)0, mate.SideId);
        Assert.Equal((short)0, other.SideId);
        Assert.Null(pilot.ParentShip);
        Assert.Empty(pilot.ChildShips);
        Assert.Null(pilot.Sector);
        Assert.Equal(before, Live(w, mate).Wallet.Money + Live(w, other).Wallet.Money);
        var events = w.Output.Where(o => o.Conn == 1).Select(o => FedMessageHeader.Read(o.Message).Fmid).ToList();
        Assert.True(events.IndexOf(Promote.Fmid) >= 0 && events.IndexOf(Promote.Fmid) < events.IndexOf(QuitSide.Fmid));
    }

    [SkippableFact]
    public void No_defections_allows_last_side_rejoin_and_refuses_enemy_after_reconnect()
    {
        using var w = new World(c => c.Games[0].AllowDefections = false);
        Prepare(w, out var pilot, out _, out _);
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        w.Send(2, new PositionReq { SideId = 1 });
        Assert.Equal((short)-2, pilot.SideId);
        Assert.Contains(w.SentTo(2, DelPositionReq.Fmid), m => DelPositionReq.Decode(m).Reason == DelPositionReqReason.NoDefections);
        w.Send(2, new PositionReq { SideId = 0 });
        Assert.Equal((short)0, pilot.SideId);
        Assert.NotNull(pilot.StationId);
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        typeof(Srv).GetMethod("ReleasePeer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w.Server, [new ConnectionId(2)]);
        w.Send(5, World.Request("PILOT"));
        w.Send(5, new PositionReq { SideId = 1 });
        for (int i = 0; i < 20 && w.SentTo(5, DelPositionReq.Fmid).Count == 0; i++) w.Server.TickSim();
        Assert.Contains(w.SentTo(5, DelPositionReq.Fmid), m => DelPositionReq.Decode(m).Reason == DelPositionReqReason.NoDefections);
        w.Send(5, new PositionReq { SideId = 0 });
        for (int i = 0; i < 400 && w.Server.Ships.All(s => s.Ship.Name != "PILOT"); i++) w.Server.TickSim();
        Assert.Contains(w.Server.Ships, s => s.Ship.Name == "PILOT" && s.Ship.SideId == 0);
    }

    [SkippableFact]
    public void Prestart_switch_keeps_existing_hull_parts_and_placement()
    {
        using var w = new World(c => c.Games[0].AllowDefections = false);
        Prepare(w, out var pilot, out _, out _);
        w.Mission.Stage = MissionStage.NotStarted;
        w.Output.Clear();
        var hull = pilot.HullType;
        var sector = pilot.Sector;
        var dock = pilot.StationId;
        var pos = pilot.Position;
        var parts = pilot.Parts.ToArray();
        w.Send(2, new PositionReq { SideId = 1 });
        Assert.Equal((short)1, pilot.SideId);
        Assert.Equal(hull, pilot.HullType);
        Assert.Equal(sector, pilot.Sector);
        Assert.Equal(dock, pilot.StationId);
        Assert.Equal(pos, pilot.Position);
        Assert.Equal(parts, pilot.Parts);
        Assert.Empty(w.SentTo(2, DockedNotify.Fmid));
    }

    [SkippableFact]
    public void Foreign_station_cannot_sell_even_unchanged_hull_or_launch()
    {
        using var w = new World();
        Prepare(w, out var pilot, out _, out _);
        var dock = pilot.StationId;
        var hull = pilot.HullType;
        pilot.SideId = 1; // corrupt/pre-fix dock state must fail closed
        w.Send(2, new BuyLoadoutRequest { HullId = hull.HullId, FLaunch = true, Parts = [] });
        var ack = BuyLoadoutAck.Decode(w.SentTo(2, BuyLoadoutAck.Fmid)[^1]);
        Assert.False(ack.FBoughtEverything);
        Assert.False(ack.FLaunch);
        Assert.Equal(dock, pilot.StationId);
        Assert.Equal(hull, pilot.HullType);
    }
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wire_replay_removes_old_observer_pose_and_rejoins_a_launchable_friendly_hangar(bool flying)
    {
        using var w = new World();
        Prepare(w, out var pilot, out _, out var enemy);
        using var observer = new ConnectionSession();
        using var player = new ConnectionSession();
        void Deliver()
        {
            while (w.Output.TryDequeue(out var item))
                if (item.Conn == 1 || item.Conn == 2)
                    (item.Conn == 1 ? observer : player).ReplayPacket(FedPacket.Pack([item.Message]), 1000);
        }
        Deliver();
        if (flying) { pilot.StationId = null; pilot.Position = new Vector3(2000, 3000, 4000); }
        w.Send(2, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.Quit });
        Deliver();
        Assert.True(observer.World.Ships[pilot.Id].IsGone);
        Assert.Equal(-1, observer.World.Ships[pilot.Id].StationId);
        Assert.Equal(-1, observer.World.Ships[pilot.Id].SectorId);
        Assert.Null(observer.World.Ships[pilot.Id].Loadout);
        Assert.Equal((short)-2, player.World.MySideId);
        w.Send(2, new PositionReq { SideId = 1 });
        Deliver();
        Assert.Equal((short)1, player.World.MySideId);
        Assert.True(player.World.InGame);
        Assert.Equal(pilot.StationId, player.World.Ships[pilot.Id].StationId);
        Assert.Contains(player.World.Stations.Values, st => st.StationId == pilot.StationId && st.SideId == 1);
        Assert.Equal(pilot.HullType.HullId, player.World.Ships[pilot.Id].HullId);
        var home = w.Mission.Sectors.SelectMany(s => s.StationRuntimes).Single(st => st.StationId == pilot.StationId);
        var canBuy = typeof(Srv).GetMethod("StationSellsHull", BindingFlags.Static | BindingFlags.NonPublic)!;
        var freeHull = w.Mission.Core.HullTypes.Values.First(h => h.GroupId >= 0 && h.Price == 0
            && (bool)canBuy.Invoke(null, [home.Type, w.Mission.Sides[1], h])!);
        var offeredHull = SuccessorUpgrade.HullSuccessor(w.Mission.Core, freeHull, home.Type.TtbmLocal,
            w.Mission.Sides[1].DevelopmentTechs);
        w.Send(2, new BuyLoadoutRequest { HullId = offeredHull.HullId, FLaunch = true, Parts = [] });
        var ack = BuyLoadoutAck.Decode(w.SentTo(2, BuyLoadoutAck.Fmid)[^1]);
        // Existing protocol semantics report fbought=false for an empty part
        // request; fLaunch and the actual fitted hull prove purchase acceptance.
        Assert.Equal(offeredHull.HullId, ack.HullId);
        Assert.True(ack.FLaunch);
        Assert.Null(pilot.StationId);
        Assert.Equal((short)1, pilot.SideId);
        Assert.Equal(enemy.Sector, pilot.Sector);
    }

    [SkippableFact]
    public void Leader_boot_and_boarded_gunner_quit_reset_only_the_departing_player()
    {
        using var w = new World();
        Prepare(w, out var pilot, out var mate, out _);
        pilot.ClearLoadout();
        pilot.ParentShip = mate;
        pilot.TurretId = 0;
        mate.AddChildShip(pilot);
        var parts = mate.Parts.ToArray();
        var hull = mate.HullType;
        w.Send(1, new QuitSide { ShipId = pilot.Id, Reason = QuitSideReason.LeaderBooted });
        Assert.Equal((short)-2, pilot.SideId);
        Assert.Null(pilot.ParentShip);
        Assert.Null(pilot.Sector);
        Assert.Null(pilot.StationId);
        Assert.Empty(mate.ChildShips);
        Assert.Equal(parts, mate.Parts);
        Assert.Equal(hull, mate.HullType);
        Assert.NotNull(mate.StationId);
    }

}
