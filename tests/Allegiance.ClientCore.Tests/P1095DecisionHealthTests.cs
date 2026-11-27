using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.Tools.McpSpawner;
using Pilot = Allegiance.Bot.Bot;

namespace Allegiance.ClientCore.Tests;

// Serial collection because the tests capture the process diagnostic writer.
[Collection("LiveNetwork")]
[Trait("Tier", "Fast")]
public sealed class P1095DecisionHealthTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // Backfill permits an unseated probe. Port zero cannot accept its local dial.
    // The fixture can then install idle pilots without starting a game or loading artwork.
    private static BotManager Manager()
    {
        var manager = new BotManager(IPAddress.Loopback.ToString(), 0, backfill: true, transport: new());
        // Exercise each due decision regardless of the caller's LOD environment.
        typeof(BotManager).GetField("_lodEnabled", Hidden)!.SetValue(manager, false);
        return manager;
    }

    private static (Pilot Pilot, Peer Peer) AddPilot(BotManager manager, string name, short ship)
    {
        var peer = new Peer();
        var pilot = new Pilot();
        typeof(Pilot).GetField("_transport", Hidden)!.SetValue(pilot, peer);
        var type = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
        var bot = Activator.CreateInstance(type, name, (short)1, "scout", (short)0, (short)0, "idle", 0d)!;
        type.GetField("Bot")!.SetValue(bot, pilot);
        type.GetField("SpawnPending")!.SetValue(bot, false);
        type.GetField("ReadyShipId")!.SetValue(bot, ship);
        ((IList)typeof(BotManager).GetField("_bots", Hidden)!.GetValue(manager)!).Add(bot);
        var pose = ServerShipUpdate.FromValues(ship, 0, default, 0, Vector3.Zero, Orientation.Identity,
            Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
        Receive(pilot, new SetCluster { Ship = pose });
        return (pilot, peer);
    }

    private static void Receive(Pilot pilot, IFedMessage message) =>
        ((ITransportSite)pilot).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));

    private static void Order(Pilot pilot, short target) =>
        Receive(pilot, new QueuedCommandUpdate { CommandId = OrderChange.CidGoto, TargetType = IgcObjectType.Warp, TargetId = target });

    private static byte[] ExpectedOrder(short ship, short target) => FedPacket.Pack([new OrderChange
    {
        ShipID = ship, Command = OrderChange.CmdQueued, CommandId = OrderChange.CidGoto,
        ObjectType = IgcObjectType.Warp, ObjectID = target
    }.Encode()]);

    private static JsonElement Stats(BotManager manager)
    {
        var stats = WorkerReply.FromTool(McpTools.Handle("get_perf_stats", null, manager));
        Assert.False(stats.IsError, stats.Data.ToString());
        return stats.Data;
    }

    private static int Streak(JsonElement stats) =>
        stats.GetProperty("decisionFailures").GetProperty("consecutiveWorkerPasses").GetInt32();

    [Fact]
    public void Worker_exception_counts_once_then_resets_on_first_success()
    {
        using var manager = Manager();
        var (_, peer) = AddPilot(manager, "alpha", 5);
        Assert.Equal(0, Streak(Stats(manager)));
        peer.ReadFaultThread = Thread.CurrentThread;
        manager.RunDecisionPass();
        peer.ReadFaultThread = null;
        Assert.Equal(1, Streak(Stats(manager)));
        manager.SetPaused(true);
        manager.RunDecisionPass();
        Assert.Equal(1, Streak(Stats(manager)));
        manager.SetPaused(false);
        manager.RunDecisionPass();
        Assert.Equal(0, Streak(Stats(manager)));
    }

    [Fact]
    public void Sustained_bot_exceptions_reach_host_once_and_other_pilots_still_accept_orders()
    {
        using var manager = Manager();
        var (first, failing) = AddPilot(manager, "alpha", 5);
        var (second, alsoFailing) = AddPilot(manager, "bravo", 6);
        var (third, working) = AddPilot(manager, "charlie", 7);
        Order(first, 10);
        Order(second, 11);
        Order(third, 12);
        failing.ThrowOnSend = alsoFailing.ThrowOnSend = true;
        var directory = Directory.CreateTempSubdirectory("p1095-");
        using var host = (SkirmishHost)Activator.CreateInstance(typeof(SkirmishHost), Hidden, null,
            [new SkirmishOptions(), directory.FullName], null)!;
        var log = new StringWriter();
        var original = Console.Error;
        Console.SetError(log);
        try
        {
            for (int i = 1; i <= 8; ++i)
            {
                manager.RunDecisionPass();
                var stats = Stats(manager);
                Assert.Equal(i, Streak(stats));
                var bots = stats.GetProperty("decisionFailures").GetProperty("bots").EnumerateArray().ToArray();
                Assert.Equal(["alpha", "bravo"], bots.Select(b => b.GetProperty("name").GetString()));
                Assert.All(bots, b => Assert.Equal(i, b.GetProperty("consecutiveBotPasses").GetInt32()));
                // Merge two actual worker-shaped reports. A second worker's single
                // miss must not raise the worst-worker count or the host threshold.
                var other = JsonSerializer.SerializeToElement(new
                {
                    decisionFailures = new { consecutiveWorkerPasses = 1, bots = Array.Empty<object>() }
                });
                var replies = new[] { PerfReply(stats), PerfReply(other) };
                var merged = SupervisorResults.MergePerf(replies, [1, 2], []);
                host.ReadHealth(JsonSerializer.SerializeToElement(new { healthy = true, stats = merged }));
                Assert.Null(host.Failure);
                Assert.Equal(i, Streak(host.Stats));
                Assert.Equal(i, Streak(host.Stats.GetProperty("workers")[0].GetProperty("report")));
                Assert.Equal(i >= 3 ? 1 : 0, Lines(log, "[skirmish]").Length);
                if (i == 1) Order(third, 13);
            }
            var diagnostic = Assert.Single(Lines(log, "[bot-decision]"));
            Assert.Contains("worker=1", diagnostic);
            Assert.Contains("bot=alpha", diagnostic);
            Assert.Contains("IOException: injected send failure", diagnostic);
            Assert.Equal(2, working.Frames.Count);
            Assert.Equal(ExpectedOrder(7, 12), working.Frames[0]);
            Assert.Equal(ExpectedOrder(7, 13), working.Frames[1]);

            failing.ThrowOnSend = false;
            manager.RunDecisionPass();
            var remaining = Assert.Single(Stats(manager).GetProperty("decisionFailures").GetProperty("bots").EnumerateArray());
            Assert.Equal("bravo", remaining.GetProperty("name").GetString());
            alsoFailing.ThrowOnSend = false;
            manager.RunDecisionPass();
            var recovered = Stats(manager);
            Assert.Equal(0, Streak(recovered));
            Assert.Empty(recovered.GetProperty("decisionFailures").GetProperty("bots").EnumerateArray());
            Assert.Equal(ExpectedOrder(5, 10), Assert.Single(failing.Frames));
            host.ReadHealth(JsonSerializer.SerializeToElement(new { healthy = true, stats = recovered }));
            // A distant bot can miss three due decisions across successful LOD
            // maintenance ticks. A later host episode must rearm on its bot count.
            var distant = new { name = "alpha", consecutiveBotPasses = 3 };
            var skipped = new { decisionFailures = new { consecutiveWorkerPasses = 0, bots = new[] { distant } } };
            host.ReadHealth(JsonSerializer.SerializeToElement(new { healthy = true, stats = skipped }));
            Assert.Equal(2, Lines(log, "[skirmish]").Length);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Running_worker_processes_commands_while_failing_and_joins_on_dispose()
    {
        Thread loop;
        Peer peer;
        using (var manager = Manager())
        {
            var pilot = AddPilot(manager, "alpha", 5);
            peer = pilot.Peer;
            Order(pilot.Pilot, 10);
            peer.ThrowOnSend = true;
            loop = (Thread)typeof(BotManager).GetField("_loopThread", Hidden)!.GetValue(manager)!;
            loop.Start();
            Assert.True(SpinWait.SpinUntil(() => Streak(Stats(manager)) >= 3, TimeSpan.FromSeconds(3)));
            manager.SetPaused(true);
            manager.SetFocusSector(17);
            Assert.Equal(17, typeof(BotManager).GetField("_focusSector", Hidden)!.GetValue(manager));
            int count = Streak(Stats(manager));
            Thread.Sleep(150); // three worker deadlines with decision execution paused
            Assert.Equal(count, Streak(Stats(manager)));
            manager.SetPaused(false);
            Assert.True(SpinWait.SpinUntil(() => Streak(Stats(manager)) > count, TimeSpan.FromSeconds(3)));
        }
        Assert.False(loop.IsAlive);
        Assert.True(peer.Closed);
    }

    private static WorkerReply PerfReply(JsonElement report) => new(false,
        JsonSerializer.SerializeToElement(new { Report = report, Cadence = Array.Empty<BotCadence>() }));

    private static string[] Lines(StringWriter log, string prefix) =>
        log.ToString().Split(Environment.NewLine).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private sealed class Peer : ITransport
    {
        internal Thread? ReadFaultThread;
        internal bool ThrowOnSend, Closed;
        internal readonly List<byte[]> Frames = [];
        public bool IsConnected => Thread.CurrentThread == ReadFaultThread ? throw new InvalidOperationException("injected read failure") : !Closed;
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed)
        {
            if (ThrowOnSend) throw new IOException("injected send failure");
            Frames.Add(data.ToArray());
        }
        public void Shutdown() => Closed = true;
        public ConnectionId LocalId => ConnectionId.Server;
        public ushort BoundPort => 0;
        public void Host(ushort port = 0) => throw new NotSupportedException();
        public void Join(string address, ushort port, string playerName) => throw new NotSupportedException();
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => throw new NotSupportedException();
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int key) => throw new NotSupportedException();
    }
}
