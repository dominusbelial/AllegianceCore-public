using System.Collections;
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
using Cfg = Allegiance.Server.ServerConfig;
using Gm = Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class MissileLifetimeQueueTests
{
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData("thread-per-peer", false)]
    [InlineData("pool", false)]
    [InlineData("thread-per-peer", true)]
    [InlineData("pool", true)]
    public void Destruction_supersedes_pending_position_before_the_terminal_event(string mode, bool snapshot)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        Ship ship = null!;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var fc = core.Constants.FloatConstants;
            float safeRadius = fc[1] * MathF.Sqrt(fc[2]);
            ship = new Ship(id, core.HullTypes.Values.First(), new Vector3(safeRadius * 0.1f, safeRadius * 0.2f, 0f),
                Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(new Cfg
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Transport = new() { Mode = mode, PoolWorkers = 2 },
            Broadcast = new() { ImmutableSnapshot = snapshot },
            Games = [new Gm { Name = "Missile lifetime queue", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "missile-lifetime");
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = "missile-lifetime", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = 234, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamID = 87700000000000234ul,
        });
        bool Seated() { lock (session.Gate) return session.World.MyShipId is not null; }
        int ticks = 0;
        while (!Seated() && ticks++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(Seated(), "client must seat");

        var type = ship.Mission!.Core.MissileTypes.Values.First(m => m.MaxLock == 0
            && m.InitialSpeed > 0 && m.Lifespan >= 1 && m.Lifespan < 6);
        ship.FireMissile(type, ship.Mission.LastUpdate);
        server.FlushBroadcastSnapshots();
        bool HasMissile() { lock (session.Gate) return session.World.Missiles.Count != 0; }
        Assert.True(SpinWait.SpinUntil(HasMissile, TimeSpan.FromSeconds(30)), "creation must arrive");
        short id;
        lock (session.Gate) id = Assert.Single(session.World.Missiles).Key;

        var transport = Assert.IsType<TcpTransport>(server.Host.Transport);
        var peers = (IDictionary)typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
        var connection = (ConnectionId)Assert.Single(peers.Keys.Cast<object>());
        var sender = peers[connection]!;
        var sink = sender.GetType().GetField("SendOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var blocked = new ManualResetEventSlim();
        sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, _) =>
        {
            blocked.Set();
            return TcpTransport.SendOutcome.Blocked();
        }));
        try
        {
            // Keep an ordered frame in-flight. Producers/publisher can still
            // enqueue normally, but no pending stream can be consumed yet.
            transport.Send(connection, FedPacket.Pack(new Ping { TimeServer = 1 }.Encode()), guaranteed: true);
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(30)), "writer must reach the controlled sink");
            server.TickSim();
            server.FlushBroadcastSnapshots();
            var keys = (IDictionary)sender.GetType().GetField("_coalesceNodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
            Assert.True(keys.Contains(6), "a real missile-position batch must be pending");

            ticks = 0;
            while (ship.Sector!.Models.OfType<Missile>().Any(m => m.Id == id) && ticks++ < 2000)
                server.TickSim();
            Assert.DoesNotContain(ship.Sector!.Models.OfType<Missile>(), m => m.Id == id);
            server.FlushBroadcastSnapshots();

            // A marker in a NEW stream key follows every pending missile batch.
            // An ordered ping would overtake it and miss the resurrection race.
            const uint marker = 0x234b;
            transport.Send(connection, FedPacket.Pack(new Ping { TimeServer = marker }.Encode()),
                guaranteed: true, coalesceKey: int.MaxValue);
            sink.SetValue(sender, null);
            Assert.True(SpinWait.SpinUntil(() => session.TakePingEchoes().Any(e => e.ServerMs == marker),
                TimeSpan.FromSeconds(30)), "client must consume the final stream marker");
            lock (session.Gate) Assert.Empty(session.World.Missiles);
        }
        finally { sink.SetValue(sender, null); }
    }
}
