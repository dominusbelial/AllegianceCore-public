using System.Collections.Concurrent;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P722InterfaceLoopbackTests
{
    // Real TCP bytes, then the real ConnectionSession receive dispatcher. Keep
    // message order independently of the decoded ClientWorld assertions.
    private sealed class Peer : ITransportSite, IDisposable
    {
        public readonly ConnectionSession Session = new();
        public readonly ConcurrentQueue<byte[]> Messages = new();
        private readonly TcpTransport _transport;
        public Peer(Srv server, string name, short side = 0)
        {
            _transport = new TcpTransport(this);
            typeof(ConnectionSession).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Session, _transport);
            _transport.Join("127.0.0.1", server.BoundPort, name);
            Session.SendLogonRequest(new LogonRequest
            {
                CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
                MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                SteamAuthTicket = new byte[16], DrmHash = "p722",
            });
            Session.SendPositionReq(side);
            Pump(server, () => Session.World.MyShipId is not null);
            Id = Session.World.MyShipId!.Value;
        }
        // P718 clears the departed world, including MyShipId. Keep the identity
        // assigned at join for byte assertions made after a boot/self-quit.
        public short Id { get; }
        public byte[][] Of(ushort fmid) => Messages.Where(m => FedMessageHeader.Read(m).Fmid == fmid).ToArray();
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            ((ITransportSite)Session).OnReceive(from, data);
            foreach (var m in FedPacket.Unpack(data)) Messages.Enqueue(m);
        }
        public void OnPeerJoined(ConnectionId id, string name) => ((ITransportSite)Session).OnPeerJoined(id, name);
        public void OnPeerLeft(ConnectionId id) => ((ITransportSite)Session).OnPeerLeft(id);
        public void OnSessionLost() => ((ITransportSite)Session).OnSessionLost();
        public void Dispose() => Session.Dispose();
    }

    private static void Pump(Srv server, Func<bool> done)
    {
        for (int i = 0; i < 6000 && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "loopback condition did not arrive");
    }

    private static Srv Boot(bool decouple = false)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.ShipSpawner = (_, m, id) =>
        {
            var sector = m.Sectors.First(s => s.Stations.Count == 0);
            var hull = m.Core.HullTypes.Values.First(h => h.Hardpoints.Any(p => p.Fixed));
            var ship = new Ship(id, hull, new Vector3(20000 + (id - 8096) * 1000, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Receive = new Allegiance.Server.ReceiveConfig { Decouple = decouple },
            Games = [new Allegiance.Server.GameConfig
            { Name = "P722 interface", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        return server;
    }

    private static Ship Ship(Srv server, Peer peer) => server.Ships.Single(l => l.Ship.Id == peer.Id).Ship;

    // Native missileigc.cpp:471-496: damage precedes the shield gate; capture
    // changes side once and never creates an impact blast (even on refusal).
    [SkippableTheory]
    [InlineData(true, false, false, 0f, false, true)]
    [InlineData(false, false, false, 0f, false, false)]
    [InlineData(true, true, false, 0f, false, false)]
    [InlineData(true, false, true, 0f, false, false)]
    [InlineData(true, false, false, 1f, false, false)]
    [InlineData(true, false, false, 1f, true, true)]
    public void Capture_missile_uses_native_station_gates_and_exact_capture_bytes(
        bool capability, bool friendly, bool pedestal, float shield, bool drainOnImpact, bool captures)
    {
        Require.Artwork();
        using var server = Boot();
        using var owner = new Peer(server, "p722-captor");
        using var observer = new Peer(server, "p722-observer", 1);
        var ship = Ship(server, owner); var mission = ship.Mission!; var sector = ship.Sector!;
        mission.Stage = MissionStage.Started;
        var baseMissile = mission.Core.MissileTypes.Values.First(m => mission.Core.Constants.DamageConstants[m.DamageType].Any(d => d > 0));
        byte shieldDefense = (byte)Array.FindIndex(mission.Core.Constants.DamageConstants[baseMissile.DamageType], d => d > 0);
        var type = mission.Core.StationTypes.Values.First() with
        {
            SabmCapabilities = pedestal ? DataStationTypeIGC.SabmPedestal : (short)0,
            Radius = 40, MaxArmorHitPoints = 10000, MaxShieldHitPoints = 10000,
            ShieldRegeneration = 0, DefenseTypeShield = shieldDefense,
        };
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 30000, StationTypeId = type.StationTypeId, ClusterId = sector.Id,
            SideId = friendly ? (short)0 : (short)1, Position = [10000, 0, 0], BpHull = 1, BpShield = (byte)(shield * 240),
        }, type);
        mission.PlaceStation(station, sector);
        typeof(Station).GetProperty(nameof(Station.ShieldFraction))!.SetValue(station, shield);
        foreach (var p in new[] { owner, observer })
            lock (p.Session.Gate) p.Session.World.Apply(ExportStation.FromStation(station.Data));
        var missileType = baseMissile with
        {
            EabmCapabilities = capability ? (short)1 : (short)0,
            Power = drainOnImpact ? type.MaxShieldHitPoints / mission.Core.Constants.DamageConstants[baseMissile.DamageType][shieldDefense] : 0,
            BlastPower = capability ? 1000 : 0, BlastRadius = 1000,
            InitialSpeed = 1000, Acceleration = 0, Radius = 2, ReadyTime = 0, Lifespan = 20,
        };
        var missile = new Missile(31000, sector, missileType, new Vector3(10100, 0, 0),
            new Vector3(-1000, 0, 0), new Vector3(-1, 0, 0), mission.LastUpdate, ship);
        var witness = new Ship(30003, ship.HullType, new Vector3(10000, 0, 150), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(witness, sector);
        float witnessHull = witness.Fraction, witnessShield = witness.ShieldFraction;
        sector.Add(missile);
        Pump(server, () => !sector.Models.Contains(missile));
        Assert.Equal(captures ? (short)0 : friendly ? (short)0 : (short)1, station.SideId);
        Assert.Equal(station.SideId, sector.Stations.Single(s => s.StationId == station.StationId).SideId);
        Assert.Equal(witnessHull, witness.Fraction); Assert.Equal(witnessShield, witness.ShieldFraction);
        if (captures)
        {
            Pump(server, () => owner.Of(58).Length > 0 && observer.Of(58).Length > 0);
            byte[] expected = [12, 0, 58, 0, 48, 117, 1, 0, 0, 0, (byte)ship.Id, (byte)(ship.Id >> 8)];
            Assert.Equal(expected, Assert.Single(owner.Of(58)));
            Assert.Equal(expected, Assert.Single(observer.Of(58)));
            lock (observer.Session.Gate)
                Assert.Equal((short)0, observer.Session.World.Stations[station.StationId].SideId);
        }
        else
        {
            // Ping is an ordered receipt barrier after the sim consumed the missile.
            int before = observer.Of(Ping.Fmid).Length; observer.Session.SendPing();
            Pump(server, () => observer.Of(Ping.Fmid).Length > before);
            Assert.Empty(observer.Of(58));
        }
    }

    // Native indicator.cpp:296-320: same-side, unboarded relay with scanner
    // contact; allies don't grant this indicator. Revoking a grant clears it.
    [SkippableTheory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void Remote_lead_relay_grants_and_revokes_owner_indicator(bool friendly, bool inRange, bool boarded, bool granted)
    {
        Require.Artwork();
        using var server = Boot(); using var peer = new Peer(server, "p722-lead");
        var shooter = Ship(server, peer); var mission = shooter.Mission!; var sector = shooter.Sector!;
        shooter.ChangeHull(shooter.HullType with { HabmCapabilities = 0 });
        var weapon = mission.Core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type).First(w => mission.Core.ProjectileTypes[w.ProjectileTypeId].Speed > 0);
        shooter.MountWeapon(weapon);
        var hull = shooter.HullType with { HabmCapabilities = 0, Signature = 1 };
        var target = new Ship(30001, hull, shooter.Position + new Vector3(0, 300, -300), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(target, sector); shooter.CommandTarget = target;
        var relay = new Ship(30002, hull with { HabmCapabilities = DataHullTypeIGC.HabmRemoteLeadIndicator,
            ScannerRange = inRange ? 100000 : 0 }, target.Position + new Vector3(100, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = friendly ? (short)0 : (short)1 };
        mission.PlaceShip(relay, sector);
        if (boarded) relay.ParentShip = shooter;
        int before = peer.Of(LeadIndicatorUpdate.Fmid).Length;
        Pump(server, () => peer.Of(LeadIndicatorUpdate.Fmid).Skip(before).Any(m => LeadIndicatorUpdate.Decode(m).HasLead == granted));
        var frame = peer.Of(LeadIndicatorUpdate.Fmid).Skip(before).Last();
        Assert.Equal((byte)34, frame[0]); Assert.Equal((byte)210, frame[2]);
        Assert.Equal(shooter.Id, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(granted ? target.Id : (short)-1, LeadIndicatorUpdate.Decode(frame).TargetShipId);
        relay.ChangeHull(relay.HullType with { HabmCapabilities = 0 });
        before = peer.Of(LeadIndicatorUpdate.Fmid).Length;
        Pump(server, () => peer.Of(LeadIndicatorUpdate.Fmid).Skip(before).Any(m => !LeadIndicatorUpdate.Decode(m).HasLead));
        lock (peer.Session.Gate) Assert.False(peer.Session.World.LeadIndicator.HasLead);
    }

    // Native igc.h:738-739; FedSrv.CPP:13393-13429; shipIGC.cpp:3727-3729.
    [SkippableTheory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void Light_ripcord_anchor_obeys_both_hull_bits_and_activation_precedes_abort(bool lightRipper, bool lightAnchor, bool accepted)
    {
        Require.Artwork();
        using var server = Boot(); using var peer = new Peer(server, "p722-ripper");
        using var anchorPeer = new Peer(server, "p722-anchor");
        var ship = Ship(server, peer); var anchor = Ship(server, anchorPeer); var mission = ship.Mission!;
        var target = mission.Sectors.First(s => s.Stations.Count == 0 && s != ship.Sector);
        anchor.Sector!.Remove(anchor); mission.PlaceShip(anchor, target);
        anchor.ChangeHull(anchor.HullType with { HabmCapabilities = lightAnchor ? DataHullTypeIGC.HabmIsLtRipcordTarget : DataHullTypeIGC.HabmIsRipcordTarget });
        ship.ChangeHull(ship.HullType with { HabmCapabilities = lightRipper ? DataHullTypeIGC.HabmCanLtRipcord : (short)0 });
        mission.Stage = MissionStage.Started;
        peer.Session.SendRipcordRequest(target.Id);
        ushort fmid = accepted ? RipcordActivate.Fmid : RipcordDenied.Fmid;
        Pump(server, () => peer.Of(fmid).Length > 0);
        if (!accepted)
        {
            Assert.Empty(peer.Of(RipcordActivate.Fmid));
            byte[] denied = [4, 0, 186, 0];
            Assert.Equal(denied, Assert.Single(peer.Of(RipcordDenied.Fmid)));
            return;
        }
        byte[] expected = [12, 0, 185, 0, (byte)ship.Id, (byte)(ship.Id >> 8), 0, 0,
            (byte)anchor.Id, (byte)(anchor.Id >> 8), (byte)target.Id, (byte)(target.Id >> 8)];
        Assert.Equal(expected, Assert.Single(peer.Of(RipcordActivate.Fmid)));
        peer.Session.SendRipcordRequest(-1);
        Pump(server, () => peer.Of(RipcordAborted.Fmid).Length > 0);
        byte[] aborted = [6, 0, 187, 0, (byte)ship.Id, (byte)(ship.Id >> 8)];
        Assert.Equal(aborted, Assert.Single(peer.Of(RipcordAborted.Fmid)));
        var order = peer.Messages.Select(m => FedMessageHeader.Read(m).Fmid).ToList();
        Assert.True(order.IndexOf(RipcordActivate.Fmid) < order.IndexOf(RipcordAborted.Fmid));
    }

    // Messages.h:812-818, FedSrv.CPP:6790-6818. Compare the received bytes,
    // verify all peers learn the departure, and reject cross-side/nonleader boots.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Leader_boot_is_confirmed_by_exact_mission_notice_and_client_removes_departed_pilot(bool decouple)
    {
        Require.Artwork();
        using var server = Boot(decouple); using var leader = new Peer(server, "p722-leader");
        using var target = new Peer(server, "p722-target"); using var enemy = new Peer(server, "p722-enemy", 1);
        var victim = Ship(server, target);
        enemy.Session.SendQuitMission(target.Id, QuitSideReason.LeaderBooted);
        target.Session.SendQuitMission(leader.Id, QuitSideReason.LeaderBooted);
        int pings = leader.Of(Ping.Fmid).Length; leader.Session.SendPing();
        Pump(server, () => leader.Of(Ping.Fmid).Length > pings);
        Assert.Contains(server.Ships, l => l.Ship == victim);
        leader.Session.SendQuitMission(target.Id, QuitSideReason.LeaderBooted);
        Pump(server, () => target.Of(QuitMission.Fmid).Length > 0 && enemy.Of(QuitMission.Fmid).Length > 0);
        byte[] expected = [17, 0, 124, 0, 16, 0, 1, 0, (byte)target.Id, (byte)(target.Id >> 8), 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(expected, Assert.Single(target.Of(QuitMission.Fmid)));
        Assert.Equal(expected, Assert.Single(enemy.Of(QuitMission.Fmid)));
        Assert.DoesNotContain(server.Ships, l => l.Ship == victim);
        lock (target.Session.Gate) Assert.Equal(QuitSideReason.LeaderBooted, target.Session.World.OwnMissionDeparture!.Reason);
        lock (enemy.Session.Gate) Assert.False(enemy.Session.World.Ships.ContainsKey(target.Id));
    }

    [SkippableFact]
    public void Self_quit_delivers_exact_reason_and_clears_own_side_without_removing_teammate()
    {
        Require.Artwork();
        using var server = Boot(); using var leaver = new Peer(server, "p722-self");
        using var teammate = new Peer(server, "p722-stay");
        short id = leaver.Id;
        leaver.Session.SendQuitMission(id, QuitSideReason.Quit);
        Pump(server, () => leaver.Of(QuitMission.Fmid).Length > 0 && teammate.Of(QuitMission.Fmid).Length > 0);
        byte[] expected = [17, 0, 124, 0, 16, 0, 1, 0, (byte)id, (byte)(id >> 8), 0, 0, 8, 0, 0, 0, 0];
        Assert.Equal(expected, Assert.Single(leaver.Of(QuitMission.Fmid)));
        Assert.Equal(expected, Assert.Single(teammate.Of(QuitMission.Fmid)));
        lock (leaver.Session.Gate)
        {
            Assert.Equal(QuitSideReason.Quit, leaver.Session.World.OwnMissionDeparture!.Reason);
            Assert.Null(leaver.Session.World.MySideId);
        }
        Assert.Contains(server.Ships, s => s.Ship.Id == teammate.Id);
    }
}
