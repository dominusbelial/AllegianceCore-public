using System.Collections.Concurrent;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P718InterfaceLoopbackTests
{
    // Inc/Messages.h:407,613,644,705,812,825,831,854. Golden bytes are
    // independent of the codecs: signed IDs, default alignment, packed records,
    // four separate six-side arrays, and the trailing Latin-1 ZTS parameter.
    public static IEnumerable<object[]> Cases()
    {
        yield return [51, "0A003300000005000100"];
        yield return [86, "0A005600010032000100"];
        yield return [92, "0E005C00080006001E00781F00F0"];
        yield return [106, "0A006A000A0009000100"];
        yield return [124, "13007C00100003000B000000090000006F6B00"];
        yield return [126, "13007E00100003000B0000000D0000006F6B00"];
        yield return [128, "04008000"];
        yield return [155, "1C009B000102030405060708090A0B0C0D0E0F101112131415161718"];
    }
    private static IFedMessage Sample(int id) => id switch
    {
        51 => new ChangeTeamCiv { SideId = 0, CivId = 5, Random = true },
        86 => new AsteroidDestroyed { ClusterId = 1, AsteroidId = 50, ExplodeF = true },
        92 => new ProbesUpdate { Entries = [new(30, 120), new(31, 240)] },
        106 => new SetWingId { ShipID = 10, WingId = 9, BCommanded = true },
        124 => new QuitMission { ShipId = 11, Reason = QuitSideReason.LinkDead, Message = "ok" },
        126 => new QuitSide { ShipId = 11, Reason = QuitSideReason.SwitchingSides, Message = "ok" },
        128 => new EnterGame(),
        155 => new GameState { Conquest = [1, 2, 3, 4, 5, 6], Territory = [7, 8, 9, 10, 11, 12],
            NFlags = [13, 14, 15, 16, 17, 18], NArtifacts = [19, 20, 21, 22, 23, 24] },
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    private static IFedMessage Decode(int id, byte[] bytes) => id switch
    {
        51 => ChangeTeamCiv.Decode(bytes), 86 => AsteroidDestroyed.Decode(bytes), 92 => ProbesUpdate.Decode(bytes),
        106 => SetWingId.Decode(bytes), 124 => QuitMission.Decode(bytes), 126 => QuitSide.Decode(bytes),
        128 => EnterGame.Decode(bytes), 155 => GameState.Decode(bytes), _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    private sealed class Site(ConnectionSession? session = null) : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public readonly SemaphoreSlim Arrived = new(0);
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        {
            if (session is not null) ((ITransportSite)session).OnReceive(id, bytes);
            Received.Enqueue(bytes.ToArray());
            Arrived.Release();
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Native_bytes_cross_real_tcp_and_dispatch_in_packet_order(int id, string hex)
    {
        using var session = new ConnectionSession(); P718InterfaceMessageTests.Seed(session);
        P718InterfaceMessageTests.Feed(session,
            new ExportAsteroid { Asteroid = new Allegiance.Core.DataAsteroidIGC(1, [1, 2, 3], [0, 1, 0], [0, 0, 1],
                [0, 0, 0], 1, 1, 0x20, 50, 100, 100, "", "", "", 1, "Si1", 255) },
            new ExportProbe { Probe = new DataProbeExport { ProbeId = 30, ClusterId = 1 } },
            new ExportProbe { Probe = new DataProbeExport { ProbeId = 31, ClusterId = 1 } });
        var hostSite = new Site(); var clientSite = new Site(session);
        var host = new TcpTransport(hostSite); var client = new TcpTransport(clientSite);
        try
        {
            host.Host(0); client.Join("127.0.0.1", host.BoundPort, "p718");
            var peer = await hostSite.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
            byte[] golden = Convert.FromHexString(hex);
            Assert.Equal(golden, Sample(id).Encode());
            Assert.Equal(golden, Decode(id, golden).Encode());
            // Two messages in each packet and two packets on the ordered TCP stream.
            // ENTER_GAME followed by the selected message, then a distinct tail frame.
            byte[] packet = FedPacket.Pack([new EnterGame().Encode(), golden]);
            byte[] tail = FedPacket.Pack(new ChangeTeamCiv { SideId = 1, CivId = 4 }.Encode());
            host.Send(peer, packet, true); host.Send(peer, tail, true);
            Assert.True(await clientSite.Arrived.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await clientSite.Arrived.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(clientSite.Received.TryDequeue(out var first)); Assert.Equal(packet, first);
            var messages = FedPacket.Unpack(first!);
            Assert.Equal(new byte[] { 4, 0, 128, 0 }, messages[0]); Assert.Equal(golden, messages[1]);
            Assert.True(clientSite.Received.TryDequeue(out var last)); Assert.Equal(tail, last);
            lock (session.Gate)
            {
                Assert.DoesNotContain((ushort)id, session.UnknownFmids);
                Assert.True(session.World.InGame);
                Assert.Equal((short)4, session.World.SideCivilizations[1].CivId);
                switch (id)
                {
                    case 51: Assert.True(session.World.SideCivilizations[0].Random); break;
                    case 86: Assert.Empty(session.World.Asteroids); Assert.Single(session.World.Explosions); break;
                    case 92: Assert.Equal(.5f, session.World.Probes[30].Fraction); Assert.Equal(1f, session.World.Probes[31].Fraction); break;
                    case 106: Assert.Equal(9, session.World.Ships[10].WingId); break;
                    case 124: Assert.False(session.World.Ships.ContainsKey(11)); break;
                    case 126: Assert.Equal(-2, session.World.Ships[11].SideId); break;
                    case 155: Assert.Equal((byte)24, session.World.SideGameStates[5].Artifacts); break;
                }
            }
        }
        finally { client.Shutdown(); host.Shutdown(); }
    }
    [Fact]
    public async Task Own_mission_departure_closes_actual_session_socket_and_delivers_reason_once()
    {
        using var session = new ConnectionSession();
        var hostSite = new Site(); var host = new TcpTransport(hostSite);
        try
        {
            host.Host(0); session.Connect(host.BoundPort, "p718-own");
            var peer = await hostSite.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int losses = 0;
            session.SessionLost += () => { Interlocked.Increment(ref losses); lost.TrySetResult(); };
            P718InterfaceMessageTests.Seed(session);
            host.Send(peer, FedPacket.Pack(new QuitMission { ShipId = 10, Reason = (QuitSideReason)2, Message = "removed" }.Encode()), true);
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (session.Gate)
            {
                Assert.False(session.Connected); Assert.True(session.Lost); Assert.Equal(1, losses);
                Assert.Equal("removed", session.World.LastDeparture!.Value.Message);
                Assert.Empty(session.World.Ships);
            }
        }
        finally { host.Shutdown(); }
    }

}
