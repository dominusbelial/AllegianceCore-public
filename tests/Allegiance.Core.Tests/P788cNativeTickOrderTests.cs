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

[Collection("LiveNetwork")]
public sealed class P788cNativeTickOrderTests
{
    internal static ServerConfig Config(bool native, bool snapshot = false, bool tiers = false)
    {
        var config = UpdateCadenceTests.Config();
        // Reflection keeps the behavioral fixtures executable on the dispatch
        // base too: an unknown opt-in setting simply retains the old order.
        typeof(SimConfig).GetProperty("NativeBroadcastOrder")?.SetValue(config.Sim, native);
        config.Broadcast!.ImmutableSnapshot = snapshot;
        config.Broadcast.EncodeOnce = snapshot;
        config.Broadcast.InterestTiers = tiers;
        config.Broadcast.ParallelBuild = snapshot;
        return config;
    }

    private static readonly MethodInfo Dispatch = typeof(Srv).GetMethod("ProcessDatagram", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Receive(Srv server, ConnectionId connection, IFedMessage message)
        => Dispatch.Invoke(server, [connection, FedPacket.Pack([message.Encode()])]);
    private static IEnumerable<byte[]> Messages(IEnumerable<byte[]> frames, ushort id)
        => frames.SelectMany(p => FedPacket.Unpack(p)).Where(m => (ushort)(m[2] | m[3] << 8) == id);

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Ship_poses_are_projected_once_at_the_selected_update_boundary(bool native, bool snapshot, bool tiers)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(native, snapshot, tiers));
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        mission.CrowdingPenaltyEnabled = false;
        var peers = UpdateCadenceTests.Seed(server, ships: 2, viewers: 2);
        var ship = peers[0].Ship;
        ship.Position = new Vector3(2000, 2000, 2000);
        ship.Velocity = new Vector3(10, 0, 0);
        var other = peers[1].Ship;
        ship.CommandTarget = other;
        other.Position = ship.Position + new Vector3(200, 0, 0);
        other.Velocity = new Vector3(10, 0, 0);
        var frames = new List<byte[]>();
        server.TestPublicationCapture = (conn, packet, _, _) => { if (conn == peers[0].Conn) frames.Add(packet); };
        try
        {
            for (int tick = 0; tick < 3; tick++)
            {
                frames.Clear();
                var before = ship.Position;
                var otherBefore = other.Position;
                long beforeTick = server.LoopIterations;
                server.TickSim();
                server.FlushBroadcastSnapshots();
                var own = Messages(frames, SingleShipUpdate.Fmid).Select(m => SingleShipUpdate.Decode(m))
                    .Where(u => u.OidTarget == ship.Id).Single();
                Assert.Equal(native ? before : ship.Position, own.Ship.Position);
                Assert.Equal((uint)(native ? beforeTick : server.LoopIterations), own.Ship.Time);
                Assert.NotEqual(before, ship.Position);
                var heavy = Messages(frames, HeavyShipsUpdate.Fmid).Select(m => HeavyShipsUpdate.Decode(m)).Single();
                var pose = heavy.HeavyShipEntries.Single(e => e.ShipId == other.Id);
                var expected = native ? otherBefore : other.Position;
                Assert.InRange((pose.Position(heavy.PositionReference) - expected).Length(), 0, 1);
                Assert.Equal(own.Ship.Time, pose.Time(heavy.TimeReference));
            }
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Stopped_turrets_send_LIGHT_identifiers_including_self_and_override_saved_active_pose(bool native, bool tiers)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(native, tiers, tiers));
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, ships: 6, viewers: 2);
        var carrier = peers[0].Ship;
        var gunner = peers[1].Ship;
        gunner.ParentShip = carrier;
        gunner.TurretId = 0;
        gunner.StateM = ShipControlState.WeaponsMask;
        var observer = peers[0].Sector.Models.OfType<Ship>().Single(s => s.Id == 10002);
        observer.ParentShip = carrier;
        var npc = peers[0].Sector.Models.OfType<Ship>().Single(s => s.Id == 10003);
        peers[0].Sector.Remove(npc);
        npc = new Ship(10003, carrier.HullType, carrier.Position, Vector3.Zero, new Orientation())
            { ParentShip = carrier, TurretId = 1, IsNpc = true, SideId = 0 };
        mission.PlaceShip(npc, peers[0].Sector);
        var dead = peers[0].Sector.Models.OfType<Ship>().Single(s => s.Id == 10004);
        dead.ParentShip = carrier; dead.TurretId = 2; dead.SetFraction(0);
        var docked = peers[0].Sector.Models.OfType<Ship>().Single(s => s.Id == 10005);
        docked.ParentShip = carrier; docked.TurretId = 3; docked.StationId = 1;
        var frames = new Dictionary<ConnectionId, List<byte[]>>();
        server.TestPublicationCapture = (c, b, _, _) =>
        { if (!frames.TryGetValue(c, out var list)) frames[c] = list = new(); list.Add(b); };
        try
        {
            Receive(server, peers[1].Conn, new ActiveTurretUpdate
            { Atu = ClientActiveTurretUpdate.From((uint)(server.SimNow * 1000 + 1), ControlData.Neutral, Orientation.Identity) });
            server.TickSim(); server.FlushBroadcastSnapshots();
            Assert.Single(Messages(frames[peers[0].Conn], HeavyShipsUpdate.Fmid)
                .Select(m => HeavyShipsUpdate.Decode(m)).Single().ActiveTurretUpdates);
            frames.Clear();
            Receive(server, peers[1].Conn, new InactiveTurretUpdate());
            server.TickSim(); server.FlushBroadcastSnapshots();
            foreach (var peer in peers)
            {
                var light = Messages(frames[peer.Conn], LightShipsUpdate.Fmid).Select(m => LightShipsUpdate.Decode(m)).Single();
                Assert.Equal([gunner.Id], light.InactiveTurretShipIds);
                Assert.Empty(Messages(frames[peer.Conn], HeavyShipsUpdate.Fmid).Select(m => HeavyShipsUpdate.Decode(m)).Single().ActiveTurretUpdates);
            }
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Mixed_started_and_starting_missions_each_send_once_in_their_own_phase()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = Config(true, snapshot: true);
        config.Games.Add(new() { Name = "P788c second", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["A", "B"] });
        server.Boot(config);
        var first = server.Missions[0];
        var second = server.Missions[1];
        first.Stage = MissionStage.Started;
        second.Stage = MissionStage.Starting;
        var hull = first.Core.HullTypes.Values.First();
        var a = new Ship(10000, hull, new Vector3(2000, 2000, 2000), new Vector3(10, 0, 0), new Orientation()) { SideId = 0 };
        var b = new Ship(10001, hull, new Vector3(2000, 2000, 2000), new Vector3(10, 0, 0), new Orientation()) { SideId = 0 };
        first.PlaceShip(a, first.Sectors[0]); second.PlaceShip(b, second.Sectors[0]);
        server.TestSeedPeers([(new ConnectionId(700), a, a.Sector!), (new ConnectionId(701), b, b.Sector!)]);
        var poses = new List<SingleShipUpdate>();
        server.TestPublicationCapture = (_, bytes, _, _) => poses.AddRange(
            Messages([bytes], SingleShipUpdate.Fmid).Select(m => SingleShipUpdate.Decode(m)));
        try
        {
            var before = a.Position;
            server.TickSim(); server.FlushBroadcastSnapshots();
            var ownA = poses.Single(p => p.OidTarget == a.Id && p.Ship.ShipId == a.Id);
            var ownB = poses.Single(p => p.OidTarget == b.Id && p.Ship.ShipId == b.Id);
            Assert.Equal(before, ownA.Ship.Position);
            Assert.Equal(0u, ownA.Ship.Time);
            Assert.Equal(1u, ownB.Ship.Time);
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Player_warp_never_rearms_on_time_legacy_rejected_or_stale_updates()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config(false));
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, 1, 1);
        var ship = peers[0].Ship;
        var conn = peers[0].Conn;
        ship.Position = new Vector3(2000, 2000, 2000);
        ship.PlayerWarpState = WarpState.NoUpdate;
        ship.LastWarpTime = server.TestMission.LastUpdate;
        for (int i = 0; i < 50; i++) server.TickSim();
        Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
        Receive(server, conn, new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, 0, ControlData.Neutral) });
        Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
        // Seeded peers have the default cookie 0; replay/age/cookie checks use
        // the production dispatcher, not a direct warp-state setter.
        ShipUpdate Pose(uint time, byte cookie = 0) => new()
        { Time = time, Cookie = cookie, Payload = ClientShipUpdate.FromPose(time, ship.Position,
            ship.Velocity, ship.Orientation, 0, ControlData.Neutral, ship.TurnRates.ToArray(), 1) };
        Receive(server, conn, Pose((uint)(server.SimNow * 1000), 99));
        Receive(server, conn, Pose((uint)(server.SimNow * 1000 + 2000)));
        Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
        Receive(server, conn, Pose((uint)(server.SimNow * 1000)));
        Assert.Equal(WarpState.Waiting, ship.PlayerWarpState);
        double deadline = ship.TimeNextWarp;
        for (int i = 0; i < 50; i++) server.TickSim();
        Assert.True(server.TestMission.LastUpdate >= deadline);
        Assert.Equal(WarpState.Waiting, ship.PlayerWarpState);
        Receive(server, conn, Pose((uint)(server.SimNow * 1000)));
        Assert.True(ship.PlayerWarpState == WarpState.Ready,
            $"state={ship.PlayerWarpState} dead={ship.Dead} station={ship.StationId} parent={ship.ParentShip?.Id} stage={ship.Mission?.Stage} sim={server.SimNow} mission={ship.Mission?.LastUpdate} deadline={deadline}");
    }
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Scripted_echo_is_validated_without_rounding_server_flight_or_bypassing_rejection()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config(false));
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, 1, 1);
        var ship = peers[0].Ship;
        ship.Position = new Vector3(2000.12345f, 2000, 2000);
        ship.Velocity = new Vector3(10.12345f, 0, 0);
        ship.Orientation.Yaw(.123456f);
        server.TickSim();
        ship.PlayerWarpState = WarpState.NoUpdate;
        ship.Controls = new ControlData(.25f, -.5f, 0, .75f);
        var beforePosition = ship.Position;
        var beforeVelocity = ship.Velocity;
        var beforeForward = ship.Orientation.GetForward();
        var beforeControls = ship.Controls;
        var wire = ServerShipUpdate.FromModel(ship.Id, ship, 1);
        ShipUpdate Echo(Vector3 position, uint time) => new()
        { Time = time, Payload = ClientShipUpdate.FromPose(time, position, wire.Velocity,
            wire.Orientation, ship.StateM, ControlData.Neutral, wire.TurnRates, wire.Power) };
        var wrongCookie = Echo(wire.Position, (uint)(server.SimNow * 1000));
        wrongCookie.Cookie = 99;
        Dispatch.Invoke(server, [peers[0].Conn, ScriptedWarpHandshake.Pack(wrongCookie)]);
        Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
        Assert.Equal(beforeControls, ship.Controls);
        Dispatch.Invoke(server, [peers[0].Conn, ScriptedWarpHandshake.Pack(
            Echo(wire.Position, (uint)(server.SimNow * 1000)))]);
        Assert.Equal(WarpState.Waiting, ship.PlayerWarpState);
        Assert.Equal(beforePosition, ship.Position);
        Assert.Equal(beforeVelocity, ship.Velocity);
        Assert.Equal(beforeForward, ship.Orientation.GetForward());
        Assert.Equal(beforeControls, ship.Controls);
        server.TickSim();
        ship.PlayerWarpState = WarpState.NoUpdate;
        var frames = new List<byte[]>();
        server.TestPublicationCapture = (_, bytes, _, _) => frames.Add(bytes);
        try
        {
            Dispatch.Invoke(server, [peers[0].Conn, ScriptedWarpHandshake.Pack(
                Echo(ship.Position + new Vector3(1000, 0, 0), (uint)(server.SimNow * 1000)))]);
            Assert.Equal(WarpState.NoUpdate, ship.PlayerWarpState);
            Assert.Single(Messages(frames, ShipReset.Fmid));
        }
        finally { server.TestPublicationCapture = null; }
    }
}
