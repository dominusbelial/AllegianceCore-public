using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Allegiance.Core.Transport;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
[Trait("Tier", "Fast")]
public sealed class P1101IngressTests
{
    // Complete bodies captured at the baseline host's receive event on a TCP loopback.
    private static readonly string[] Captured =
    [
        "10002300010000000000000000000000589BC3DF",
        "10002300020000000000000000000000A8495DA8",
        "10002300030000000000000000000000C705F833",
        "1000230004000000000000000000000048EC6047",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Captured_packets_match_baseline_events_at_every_tick(bool decouple)
    {
        var host = new ServerHost { ReceiveDecouple = decouple };
        ITransportSite site = host;
        long budget = ServerHost.DrainBudgetEvents;
        int share = ServerHost.DrainFairShareEvents;
        try
        {
            ServerHost.DrainBudgetEvents = 4;
            ServerHost.DrainFairShareEvents = 2;
            foreach (uint id in new uint[] { 3, 1, 2 })
            {
                site.OnPeerJoined(new(id), $"P{id}");
                foreach (var hex in Captured) site.OnReceiveOwned(new(id), Convert.FromHexString(hex));
                site.OnPeerLeft(new(id));
            }

            // Baseline transcript. Each receive number indexes the exact captured bytes (1-based).
            string[] ticks =
            [
                "1+P1 1/1 2+P2 3+P3",
                "1/2 2/1 2/2 3/1",
                "1/3 2/3 3/2 3/3",
                "1/4 1- 2/4 3/4",
                "2- 3-", "",
            ];
            foreach (var expected in ticks)
            {
                var events = new List<ITransportEvent>();
                host.Drain(events.Add);
                var actual = events.Select(e => e switch
                {
                    PeerJoinedEvt j => $"{j.Id.Value}+{j.Name}",
                    ReceivedEvt r => $"{r.From.Value}/{1 + Array.IndexOf(Captured, Convert.ToHexString(r.Data))}",
                    PeerLeftEvt l => $"{l.Id.Value}-",
                    _ => throw new InvalidDataException("Unexpected transport event"),
                });
                Assert.Equal(expected, string.Join(' ', actual));
            }
        }
        finally { ServerHost.DrainBudgetEvents = budget; ServerHost.DrainFairShareEvents = share; }
    }

    // Observe the actual TCP hand-off before the mux remaps the ID and forwards to the host.
    private sealed class Tap(ITransportSite next) : ITransportSite
    {
        internal readonly List<byte[]> Bodies = [];
        public void OnPeerJoined(ConnectionId id, string name) => next.OnPeerJoined(id, name);
        public void OnPeerLeft(ConnectionId id) => next.OnPeerLeft(id);
        public void OnSessionLost() => next.OnSessionLost();
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> data) => next.OnReceive(id, data);
        void ITransportSite.OnReceiveOwned(ConnectionId id, byte[] body)
        {
            Bodies.Add(body);
            next.OnReceiveOwned(id, body);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tcp_transfers_each_exact_array_through_mux_and_borrowed_input_is_copied(bool decouple)
    {
        var host = new ServerHost { ReceiveDecouple = decouple };
        var mux = new ServerTransportMux(host);
        Tap? tap = null;
        ITransportSite? adapter = null;
        mux.Add(s => new TcpTransport(tap = new Tap(adapter = s), TransportMode.Pool, 1));
        mux.Host(0, IPAddress.Loopback);
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, mux.BoundPort);
            var stream = client.GetStream();
            stream.Write(new byte[] { 1, 1, 0, 0, 0, (byte)'p' });
            byte[][] sent = [Convert.FromHexString(Captured[0]), new byte[12001], Convert.FromHexString(Captured[1])];
            new Random(1101).NextBytes(sent[1]); // spans multiple ReadFrame growth steps
            foreach (byte[] packet in sent)
            {
                byte[] header = [2, 0, 0, 0, 0];
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), packet.Length);
                stream.Write(header);
                stream.Write(packet);
            }
            Assert.True(SpinWait.SpinUntil(() => host.PacketsReceived == sent.Length, TimeSpan.FromSeconds(5)));
            byte[] borrowed = Convert.FromHexString(Captured[2]);
            adapter!.OnReceive(new(1), borrowed);
            borrowed.AsSpan().Clear();
            var events = new List<ITransportEvent>();
            host.Drain(events.Add);
            var receives = events.OfType<ReceivedEvt>().ToArray();
            Assert.Equal(sent.Length + 1, receives.Length);
            Assert.Equal(sent.Length, tap!.Bodies.Count);
            for (int i = 0; i < sent.Length; i++)
            {
                Assert.Same(tap.Bodies[i], receives[i].Data);
                Assert.Equal(sent[i], receives[i].Data);
            }
            Assert.Equal(Captured[2], Convert.ToHexString(receives[^1].Data));
        }
        finally { mux.Shutdown(); }
    }

    [Fact]
    public void Drain_reuses_warm_scratch_and_bounds_capacity_after_departures()
    {
        var host = new ServerHost();
        ITransportSite site = host;
        byte[] body = Convert.FromHexString(Captured[0]);
        long budget = ServerHost.DrainBudgetEvents;
        try
        {
            // Force selection, order and dictionaries above their retention limits.
            ServerHost.DrainBudgetEvents = 20000;
            for (uint id = 1; id <= 1600; id++)
            {
                for (int i = 0; i < (id == 1 ? 64 : 6); i++) site.OnReceive(new(id), body);
                site.OnPeerLeft(new(id));
            }
            host.Drain(e => { if (e is PeerLeftEvt l) host.RemovePeerAccounting(l.Id); });
            T Scratch<T>(string name) => (T)typeof(ServerHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
            var spare = Scratch<Stack<Queue<ITransportEvent>>>("_spareLanes");
            Assert.InRange(spare.Count, 1, 512);
            Assert.All(spare, q => { Assert.Empty(q); Assert.InRange(q.Capacity, 0, 32); });
            var selection = Scratch<List<(uint, int, ITransportEvent)>>("_selected");
            Assert.Empty(selection);
            Assert.InRange(selection.Capacity, 0, 8192);
            Assert.InRange(Scratch<List<uint>>("_pendingOrder").Capacity, 0, 1024);
            Assert.InRange(Scratch<Dictionary<uint, Queue<ITransportEvent>>>("_pendingLanes").Capacity, 0, 1024);
            Assert.InRange(Scratch<Dictionary<uint, int>>("_handled").Capacity, 0, 1024);
            Assert.Equal(0, host.BufferedPeerCount);

            Action<ITransportEvent> handle = static _ => { };
            long allocated = 0;
            for (int tick = 0; tick < 25; tick++)
            {
                for (uint id = 1; id <= 32; id++) site.OnReceive(new(id), body);
                long start = GC.GetAllocatedBytesForCurrentThread();
                host.Drain(handle);
                if (tick > 0) allocated += GC.GetAllocatedBytesForCurrentThread() - start;
            }
            Assert.True(allocated < 24 * 256, $"Warm drain allocated {allocated} bytes across 24 ticks");
        }
        finally { ServerHost.DrainBudgetEvents = budget; }
    }
}
