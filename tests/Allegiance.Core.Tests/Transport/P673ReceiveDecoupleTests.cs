using System.Buffers.Binary;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P673ReceiveDecoupleTests
{
    private static ServerHost Host(bool decouple, params uint[] peers)
    {
        var host = new ServerHost { ReceiveDecouple = decouple };
        foreach (uint id in peers) ((ITransportSite)host).OnPeerJoined(new(id), $"p{id}");
        host.Drain(_ => { });
        return host;
    }

    [Fact]
    public async Task Worker_decodes_once_before_tick_and_drain_runs_only_on_its_caller()
    {
        var host = Host(true, 1);
        int worker = 0;
        var bytes = FedPacket.Pack(new Ping { TimeClient = 42 }.Encode());
        await Task.Run(() => { worker = Environment.CurrentManagedThreadId; ((ITransportSite)host).OnReceive(new(1), bytes); });
        Assert.Equal(1, host.PacketsReceived);
        int caller = Environment.CurrentManagedThreadId;
        int applied = 0;
        host.Drain(e =>
        {
            var received = Assert.IsType<ReceivedEvt>(e);
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            var command = Assert.Single(received.Prepared!.Messages);
            Assert.Same(command.Read<Ping>(), command.Read<Ping>());
            Assert.Equal(42u, command.Read<Ping>().TimeClient);
            applied++;
        });
        Assert.NotEqual(0, worker);
        Assert.Equal(1, applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tick_batch_is_sorted_by_peer_then_sequence_and_budget_rotates_fairly(bool decouple)
    {
        long budget = ServerHost.DrainBudgetEvents;
        int share = ServerHost.DrainFairShareEvents;
        try
        {
            var host = Host(decouple, 3, 1, 2);
            ServerHost.DrainBudgetEvents = 2;
            ServerHost.DrainFairShareEvents = 1;
            foreach (uint peer in new uint[] { 3, 1, 2 })
                for (uint seq = 0; seq < 4; seq++)
                    ((ITransportSite)host).OnReceive(new(peer), FedPacket.Pack(new Ping { TimeClient = seq }.Encode()));
            var seen = new Dictionary<uint, List<uint>>();
            for (int tick = 0; tick < 6; tick++)
            {
                var order = new List<uint>();
                Assert.Equal(2, host.Drain(e =>
                {
                    var r = Assert.IsType<ReceivedEvt>(e);
                    order.Add(r.From.Value);
                    if (!seen.TryGetValue(r.From.Value, out var seqs)) seen[r.From.Value] = seqs = [];
                    seqs.Add((r.Prepared ?? InboundPacket.Prepare(r.Data, false)).Messages[0].Read<Ping>().TimeClient);
                }));
                Assert.Equal(order.Order(), order);
                if (tick == 1) Assert.Equal(3, seen.Count);
            }
            Assert.All(seen.Values, seqs => Assert.Equal(new uint[] { 0, 1, 2, 3 }, seqs));
        }
        finally { ServerHost.DrainBudgetEvents = budget; ServerHost.DrainFairShareEvents = share; }
    }

    [Fact]
    public void Incomplete_peer_does_not_block_ready_peer_or_let_leave_overtake_receive()
    {
        var host = Host(true, 1, 2);
        var queue = (System.Collections.Concurrent.ConcurrentQueue<ITransportEvent>)typeof(ServerHost)
            .GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        // Pause at the real publication seam, without sleeping or timing assumptions.
        var pending = new ReceivedEvt(new(1), FedPacket.Pack(new Ping().Encode()), pending: true);
        queue.Enqueue(pending);
        ((ITransportSite)host).OnPeerLeft(new(1));
        ((ITransportSite)host).OnReceive(new(2), FedPacket.Pack(new Ping().Encode()));
        var seen = new List<ITransportEvent>();
        Assert.Equal(1, host.Drain(seen.Add));
        Assert.Equal(2u, Assert.IsType<ReceivedEvt>(Assert.Single(seen)).From.Value);
        Assert.True(host.EventsDeferred >= 2);
        pending.Complete(InboundPacket.Prepare(pending.Data, true));
        seen.Clear();
        Assert.Equal(2, host.Drain(seen.Add));
        Assert.Same(pending, seen[0]);
        Assert.IsType<PeerLeftEvt>(seen[1]);
        long before = host.PacketsReceived;
        ((ITransportSite)host).OnReceive(new(1), pending.Data); // late callback after close
        Assert.Equal(before, host.PacketsReceived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wire_attacks_are_refused_and_next_valid_message_survives(bool decode)
    {
        var crc = FedPacket.Pack(new Ping().Encode());
        crc[^1] ^= 0x80;
        Assert.NotNull(InboundPacket.Prepare(crc, decode).Error);
        Assert.NotNull(InboundPacket.Prepare(FedPacket.Pack(Enumerable.Repeat(new Ping().Encode(), FedPacket.MaxMessagesPerPacket + 1)), decode).Error);
        byte[] reload = new Reload { Entries = [new ReloadData(50, 1)] }.Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(reload.AsSpan(4), 200);
        BinaryPrimitives.WriteUInt16LittleEndian(reload.AsSpan(6), 400);
        byte[] missile = new FireMissile { Entries = [default] }.Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(missile.AsSpan(4), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(missile.AsSpan(6), 400);
        var packet = InboundPacket.Prepare(FedPacket.Pack(new byte[][] { reload, missile, [4, 0, 24, 0], new Ping { TimeClient = 77 }.Encode() }), decode);
        Assert.Null(packet.Error);
        Assert.Throws<ArgumentException>(() => packet.Messages[0].Read<Reload>());
        Assert.Throws<ArgumentException>(() => packet.Messages[1].Read<FireMissile>());
        Assert.Throws<ArgumentException>(() => packet.Messages[2].Read<ShipUpdate>());
        Assert.Equal(77u, packet.Messages[3].Read<Ping>().TimeClient);
        // A tiny cbmsg accepted by the packet walker stays a header error.
        Assert.True(InboundPacket.Prepare(FedPacket.Pack(new byte[] { 2, 0, 4, 0, 0, 0 }), decode).Messages[0].BadHeader);
    }

    [Fact]
    public void Histogram_retains_100_ticks_and_attributes_fmids_bytes_and_peer_ids()
    {
        var lines = new List<string>();
        var trace = new InboundTrace { Write = lines.Add };
        for (int tick = 0; tick < 100; tick++)
        {
            if (tick == 40) { trace.Message(new(7), 24, 48); trace.Message(new(9), 24, 48); }
            trace.EndTick(tick, tick == 40 ? 10.5 : 0);
        }
        Assert.Contains("fmids[24=2/96] peers[7:24=1/48,9:24=1/48]", lines[40]);
        Assert.Contains("40:2/96/10.500", lines[^1]);
        Assert.Contains("99:0/0/0.000", lines[^1]);
        Assert.Equal(101, lines.Count);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Identical_tick_inputs_produce_identical_simulation_and_wire_results_off_vs_on()
    {
        Require.Artwork();
        using var off = new World(false);
        using var on = new World(true);
        for (int tick = 0; tick < 20; tick++)
        {
            foreach (var w in new[] { off, on })
            {
                string before = w.Snapshot();
                // Reverse peer arrival order deliberately; the tick applies peer id first.
                w.Input(2, new AutoDonate { SidDonateTo = 100, Amount = 5 });
                w.Input(1, new AutoDonate { SidDonateTo = 101, Amount = 7 });
                w.Input(1, new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, 0, new ControlData(.2f, 0, 0, .5f)) });
                // P653 attacks run through the same worker-prepared path as valid input.
                w.Input(1, new PlayerDonate { ShipID = 101, MoneyGiven = -100 });
                w.Input(1, new BuyLoadoutRequest { HullId = short.MaxValue, Parts = [] });
                w.Input(2, new SwapPart { ShipID = 100, MountNew = 0, MountOld = 0 });
                Assert.Equal(before, w.Snapshot()); // publication cannot mutate the simulation
                w.Advance(tick);
            }
            Assert.Equal(off.Snapshot(), on.Snapshot());
            Assert.Equal(off.Output, on.Output);
            Assert.Equal(100 - 2 * (tick + 1), off.Server.Ships.Single(s => s.Ship.Id == 100).Wallet.Money);
            Assert.Equal(100 + 2 * (tick + 1), off.Server.Ships.Single(s => s.Ship.Id == 101).Wallet.Money);
        }
        Assert.Equal(200, off.Server.Ships.Sum(s => s.Wallet.Money));
        Assert.NotEqual(Vector3.Zero, off.Ships[0].Velocity);
        Assert.True(off.Ships[0].Controls.Throttle > 0);
    }

    private sealed class World : IDisposable
    {
        internal readonly ServerHost Host;
        internal readonly Srv Server = new();
        internal readonly Mission Mission;
        internal readonly Ship[] Ships;
        internal readonly List<string> Output = [];
        private readonly Action<ITransportEvent> _apply;
        internal World(bool decouple)
        {
            Host = P673ReceiveDecoupleTests.Host(decouple, 1, 2);
            string root = TestArtRoot.Value;
            var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(root, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(root, "XXV_simple.igc")));
            Mission = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.Started;
            var sector = Mission.Sectors[0];
            var hull = core.HullTypes.Values.First(h => h.Speed > 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            Ships = Enumerable.Range(0, 2).Select(i => new Ship((short)(100 + i), hull, new Vector3(10000 + 1000 * i, 10000, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 }).ToArray();
            foreach (var ship in Ships) Mission.PlaceShip(ship, sector);
            Field("_host", Host);
            Field("_config", new ServerConfig());
            Field("_missions", new List<Mission> { Mission });
            Server.TestSeedPeers([(new(1), Ships[0], sector), (new(2), Ships[1], sector)]);
            foreach (var live in Server.Ships) live.Wallet.Money = 100;
            Server._testSendCapture = (peer, packet) => Output.Add($"{peer.Value}:{Convert.ToHexString(packet)}");
            _apply = typeof(Srv).GetMethod("HandleTransportEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<ITransportEvent>>(Server);
        }
        private void Field(string name, object value) => typeof(Srv).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Server, value);
        internal void Input(uint peer, IFedMessage message) => ((ITransportSite)Host).OnReceive(new(peer), FedPacket.Pack(message.Encode()));
        internal void Advance(int tick) { Host.Drain(_apply); Mission.Update((tick + 1) * .05); }
        internal string Snapshot() => string.Join(';', Server.Ships.OrderBy(s => s.Ship.Id).Select(s => $"{s.Ship.Id}:{Bits(s.Ship.Position)}:{Bits(s.Ship.Velocity)}:{Bits(s.Ship.Orientation.GetForward())}:{s.Ship.Controls}:{s.Ship.StateM}:{s.Wallet.Money}:{s.Ship.HullType.HullId}:{s.Ship.Parts.Count}"));
        private static string Bits(Vector3 v) => $"{BitConverter.SingleToInt32Bits(v.X)}/{BitConverter.SingleToInt32Bits(v.Y)}/{BitConverter.SingleToInt32Bits(v.Z)}";
        public void Dispose() { }
    }
}
