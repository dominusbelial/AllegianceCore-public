using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Profiling;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P661EgressLiveTests(ITestOutputHelper output)
{
    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentDictionary<string, ConnectionId> Joined = new();
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined[name] = id;
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received.Enqueue(data.ToArray());
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Three_loopback_peers_receive_expected_fmids_and_exact_split_totals(TransportMode mode)
    {
        var site = new Site();
        var accounting = new EgressAccounting();
        var host = new TcpTransport(site, mode, 2) { Egress = accounting };
        host.Host(0);
        var names = new[] { "P661-human", "mcp-p661-a", "mcp-p661-b" };
        var clients = new List<TcpTransport>();
        var sites = new List<Site>();
        try
        {
            foreach (var name in names)
            {
                var clientSite = new Site();
                var client = new TcpTransport(clientSite);
                clients.Add(client); sites.Add(clientSite);
                client.Join("127.0.0.1", host.BoundPort, name);
            }
            Assert.True(SpinWait.SpinUntil(() => site.Joined.Count == 3, 5000));
            var ordered = P661EgressAccountingTests.Frame((2, 16), (26, 100));
            var stream = P661EgressAccountingTests.Frame((175, 12), (176, 36));
            var ownShip = P661EgressAccountingTests.Frame((78, 32));
            foreach (var name in names)
            {
                var peer = site.Joined[name];
                host.Send(peer, ordered.AsSpan(5), true);
                host.SendCoalescing(peer, stream.AsSpan(5), 42);
                host.SendGuaranteedCoalescing(peer, ownShip.AsSpan(5), 43);
            }
            Assert.True(SpinWait.SpinUntil(() => sites.All(s => s.Received.Count == 3), 5000));
            long expectedBytes = 3L * (ordered.Length + stream.Length + ownShip.Length);
            // Receiver completion can precede the writer's accounting instruction.
            var windows = new List<EgressWindow>();
            long sent = 0;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var window = accounting.DrainWindow();
                windows.Add(window); sent += window.Rows.Sum(r => r.SentBytes);
                if (sent == expectedBytes) break;
                Thread.Sleep(1);
            } while (DateTime.UtcNow < deadline);
            Assert.Equal(expectedBytes, sent);
            var rows = windows.SelectMany(w => w.Rows).ToArray();
            Assert.Equal(expectedBytes, rows.Sum(r => r.OfferedBytes));
            Assert.Equal(15, rows.Sum(r => r.SentMessages));
            Assert.Equal(0, rows.Sum(r => r.CoalescedFrames + r.DroppedFrames));
            Assert.Equal(32, rows.Where(r => r.Fmid == 78 && r.PeerKind == "human" && r.Lane == "stream").Sum(r => r.SentBytes));
            Assert.Equal(64, rows.Where(r => r.Fmid == 78 && r.PeerKind == "harness" && r.Lane == "stream").Sum(r => r.SentBytes));
            Assert.Equal(116, rows.Where(r => r.PeerKind == "human" && r.Lane == "ordered" && r.Fmid != EgressAccounting.OverheadFmid).Sum(r => r.SentBytes));
            foreach (var clientSite in sites)
            {
                ushort[] observed = clientSite.Received.SelectMany(packet => FedPacket.Unpack(packet))
                    .Select(message => FedMessageHeader.Read(message).Fmid).Order().ToArray();
                Assert.Equal(new ushort[] { 2, 26, 78, 175, 176 }, observed);
            }
            output.WriteLine($"P661 loopback mode={mode} peers=3 fmids=2,26,78,175,176 offeredBytes={expectedBytes} sentBytes={sent} msgs=15 human=1 harness=2");
        }
        finally
        {
            foreach (var client in clients) client.Shutdown();
            host.Shutdown();
        }
    }

    // Deterministic queue/sink test, using the existing private PeerSender and
    // P645 send override. No writer is started, so coalescing/shedding cannot race.
    [Fact]
    public void Real_sender_hooks_account_coalescing_shedding_partial_progress_and_failed_writes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using var socket = listener.AcceptTcpClient();
        listener.Stop();
        var type = typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;
        var sender = Activator.CreateInstance(type, socket.GetStream(), socket, (Action<DisconnectReason>)(_ => { }))!;
        var accounting = new EgressAccounting();
        type.GetField("Egress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, accounting);
        type.GetField("EgressHarness", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, true);
        var enqueue = type.GetMethod("Enqueue")!;
        var drain = type.GetMethod("TryWriteHead")!;
        var sink = type.GetField("SendOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long oldCapacity = TcpTransport.CapacityBytes;
        try
        {
            TcpTransport.CapacityBytes = 120;
            var old = P661EgressAccountingTests.Frame((175, 12), (175, 8)); // 29
            var latest = P661EgressAccountingTests.Frame((176, 36)); // 45
            var shed = P661EgressAccountingTests.Frame((78, 32)); // 41
            var ordered = P661EgressAccountingTests.Frame((26, 60)); // 69
            enqueue.Invoke(sender, [old, true, 1]);
            enqueue.Invoke(sender, [latest, true, 1]); // supersedes guaranteed stream
            enqueue.Invoke(sender, [shed, false, 2]);
            enqueue.Invoke(sender, [ordered, true, 0]); // sheds only the non-guaranteed stream
            sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, _) => TcpTransport.SendOutcome.Blocked()));
            drain.Invoke(sender, null);
            var before = accounting.DrainWindow();
            Assert.Equal(old.Length + latest.Length + shed.Length + ordered.Length, before.Rows.Sum(r => r.OfferedBytes));
            Assert.Equal(old.Length, before.Rows.Sum(r => r.CoalescedBytes));
            Assert.Equal(2, Assert.Single(before.Rows, r => r.Fmid == 175).CoalescedMessages);
            Assert.Equal(1, Assert.Single(before.Rows, r => r.Fmid == 175).CoalescedFrames);
            Assert.Equal(shed.Length, before.Rows.Sum(r => r.DroppedBytes));
            Assert.Equal(1, Assert.Single(before.Rows, r => r.Fmid == 78).DroppedFrames);
            Assert.Equal(0, before.Rows.Sum(r => r.SentBytes));
            sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, _) => TcpTransport.SendOutcome.Sent(10)));
            drain.Invoke(sender, null);
            var partial = accounting.DrainWindow();
            Assert.Equal(10, partial.Rows.Sum(r => r.SentBytes));
            Assert.Equal(0, partial.Rows.Sum(r => r.SentMessages));
            sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, count) => TcpTransport.SendOutcome.Sent(count)));
            drain.Invoke(sender, null); // complete ordered
            drain.Invoke(sender, null); // send retained guaranteed stream
            var complete = accounting.DrainWindow();
            Assert.Equal(ordered.Length + latest.Length - 10, complete.Rows.Sum(r => r.SentBytes));
            Assert.Equal(2, complete.Rows.Sum(r => r.SentMessages));
            enqueue.Invoke(sender, [ordered, true, 0]);
            sink.SetValue(sender, (Func<byte[], int, int, TcpTransport.SendOutcome>)((_, _, _) => TcpTransport.SendOutcome.TimedOut()));
            drain.Invoke(sender, null);
            Assert.Equal(0, accounting.DrainWindow().Rows.Sum(r => r.SentBytes));
        }
        finally
        {
            TcpTransport.CapacityBytes = oldCapacity;
            type.GetMethod("Close")!.Invoke(sender, null);
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Server_profiler_window_and_admin_dump_use_the_same_completed_snapshot()
    {
        Require.Artwork();
        using var server = new global::Allegiance.Server.Server();
        var saved = Console.Out;
        using var log = new StringWriter();
        var site = new Site();
        var client = new TcpTransport(site);
        try
        {
            Console.SetOut(log);
            server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                Profiler = new ProfilerConfig { Enabled = true, ReportIntervalSeconds = 3600 },
                LiveSettings = new LiveSettingsConfig
                {
                    AdminCheats = new AdminCheatsConfig { Enabled = true, AllowNames = ["mcp-p661-admin"], Secret = "p661-test" },
                },
                Games = [new GameConfig { Name = "P661", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            client.Join("127.0.0.1", server.BoundPort, "mcp-p661-admin");
            client.Send(ConnectionId.Server, FedPacket.Pack(new LogonRequest
            { CharacterName = "mcp-p661-admin", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 }.Encode()), true);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (site.Received.IsEmpty && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(1); }
            Assert.False(site.Received.IsEmpty);
            client.Send(ConnectionId.Server, FedPacket.Pack(new PositionReq { SideId = server.Missions[0].Sides[0].Id }.Encode()), true);
            var ships = (System.Collections.IDictionary)typeof(global::Allegiance.Server.Server)
                .GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
            while (ships.Count == 0 && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(1); }
            Assert.Single(((System.Collections.IEnumerable)ships).Cast<object>());
            TickProfiler.ForceReport();
            var window = Assert.IsType<EgressWindow>(server.LastEgressWindow);
            Assert.Contains(window.Rows, r => r.Fmid == 2 && r.PeerKind == "harness" && r.Lane == "ordered" && r.OfferedMessages == 1);
            Assert.Equal(1, log.ToString().Split('\n').Count(l => l.StartsWith("[egress-by-type] ")));
            // Invoke the real authorization/dispatch hook, with the logged-on ship.
            var entry = ((System.Collections.IEnumerable)ships).Cast<object>().Single();
            var from = (ConnectionId)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            var live = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
            var ship = live.GetType().GetProperty("Ship")!.GetValue(live)!;
            var handle = typeof(global::Allegiance.Server.Server).GetMethod("TryHandleAdminCheat", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)handle.Invoke(server, [from, ship, new ChatMessage { Message = "p661-test egress" }])!);
            Assert.Same(window, server.LastEgressWindow);
            string jsonLine = log.ToString().Split('\n').Single(l => l.StartsWith("[egress-by-type-table] {"));
            using var json = JsonDocument.Parse(jsonLine["[egress-by-type-table] ".Length..]);
            Assert.Equal(window.Rows.Length, json.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal(window.WindowSeconds, json.RootElement.GetProperty("windowSeconds").GetDouble());
            output.WriteLine(log.ToString().Split('\n').Single(l => l.StartsWith("[egress-by-type] ")));
        }
        finally { client.Shutdown(); Console.SetOut(saved); }
    }
}
