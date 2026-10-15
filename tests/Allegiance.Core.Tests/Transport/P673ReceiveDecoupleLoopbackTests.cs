using System.Buffers.Binary;
using System.Diagnostics;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P673ReceiveDecoupleLoopbackTests(ITestOutputHelper output)
{
    private sealed class Sink : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    [Theory]
    [InlineData(TransportMode.Pool)]
    [InlineData(TransportMode.ThreadPerPeer)]
    public void Aligned_real_socket_burst_has_identical_commands_and_zero_sim_decode_when_enabled(TransportMode mode)
    {
        const int peers = 32, perPeer = 3;
        var raw = new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, 3, new ControlData(.2f, -.3f, 0, .4f)) }.Encode();
        var message = new byte[65532];
        raw.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(message, (ushort)message.Length);
        var packet = FedPacket.Pack(message);
        var results = new List<List<string>>();
        foreach (bool decouple in new[] { false, true })
        {
            var host = new ServerHost { ReceiveDecouple = decouple };
            var transport = new TcpTransport(host, mode, poolWorkers: 2);
            var clients = new List<TcpTransport>();
            try
            {
                transport.Host(0);
                int joins = 0;
                for (int i = 0; i < peers; i++)
                {
                    var client = new TcpTransport(new Sink(), TransportMode.Pool, poolWorkers: 1);
                    clients.Add(client);
                    client.Join("127.0.0.1", transport.BoundPort, $"p673-{i}");
                }
                Until(() => { host.Drain(e => { if (e is PeerJoinedEvt) joins++; }); return joins == peers; });
                foreach (var client in clients)
                    for (int n = 0; n < perPeer; n++) client.Send(ConnectionId.Server, packet, guaranteed: true);
                Until(() => host.PacketsReceived == peers * perPeer);
                int simDecode = 0;
                var commands = new List<string>();
                var sw = Stopwatch.StartNew();
                int drained = host.Drain(e =>
                {
                    var r = Assert.IsType<ReceivedEvt>(e);
                    Assert.Equal(decouple, r.Prepared is not null);
                    if (r.Prepared is null) simDecode++;
                    var update = Assert.Single((r.Prepared ?? InboundPacket.Prepare(r.Data, false)).Messages).Read<ShipUpdate>();
                    commands.Add($"{r.From.Value}:{update.Payload.Controls}:{update.Payload.StateM}");
                });
                sw.Stop();
                output.WriteLine($"mode={mode} decouple={decouple} peers={peers} packets={drained} bytes={packet.Length * drained} drainMs={sw.Elapsed.TotalMilliseconds:F3} simDecode={simDecode}");
                Assert.Equal(peers * perPeer, drained);
                Assert.Equal(decouple ? 0 : peers * perPeer, simDecode);
                Assert.Equal(0, host.EventsDeferred);
                results.Add(commands);
            }
            finally
            {
                foreach (var client in clients) client.Shutdown();
                transport.Shutdown();
            }
        }
        Assert.Equal(results[0], results[1]);
    }

    private static void Until(Func<bool> done)
    {
        var timeout = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(timeout.Elapsed.TotalSeconds < 30, "real loopback callback timed out");
            Thread.Sleep(1);
        }
    }
}
