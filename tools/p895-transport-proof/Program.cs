using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TransportProof;

string? Option(string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
string protocol = Option("--protocol") ?? "tcp";
int bots = int.Parse(Option("--bots") ?? "4", CultureInfo.InvariantCulture);
int seconds = int.Parse(Option("--seconds") ?? "12", CultureInfo.InvariantCulture);
int delay = int.Parse(Option("--delay") ?? "0", CultureInfo.InvariantCulture);
int jitter = int.Parse(Option("--jitter") ?? "0", CultureInfo.InvariantCulture);
int kernelDelay = int.Parse(Option("--kernel-delay") ?? "0", CultureInfo.InvariantCulture);
bool batchWrites = args.Contains("--tcp-batch-writes");
double loss = double.Parse(Option("--loss") ?? "0", CultureInfo.InvariantCulture);
string state = Path.GetFullPath(Option("--state") ?? ".scratch/p895/proof");
Directory.CreateDirectory(state);
if (protocol is not ("tcp" or "udp") || bots is < 2 or > 200 || seconds is < 2 or > 120 || loss is < 0 or > .2)
    throw new ArgumentException("bounded proof: tcp|udp, 2..200 bots, 2..120 seconds, loss 0..0.2");
if (kernelDelay < 0 || kernelDelay > 120 || kernelDelay > 0 && (delay != 0 || jitter != 0 || loss != 0))
    throw new ArgumentException("--kernel-delay labels externally configured netem; cannot combine with proxy impairment");
if (args.Contains("--host")) { Host(); return; }
Run();

void Host()
{
    using var log = new StreamWriter(Path.Combine(state, "host.log")); log.AutoFlush = true; Console.SetOut(log);
    using var server = new Server();
    server.SpawnInitialMinersAtBoot = false;
    server.Boot(new ServerConfig
    {
        Port = 0, ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
        UpdatesPerSecond = 20, BenchmarkSeed = 895,
        Transport = new() { Protocol = protocol, Mode = "pool", PoolWorkers = 2, MaxConnectionsPerIp = 256,
            TcpBatchWrites = batchWrites },
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        // Dense transport fixture: keep 200 clients alive instead of native
        // crowding ejecting the whole sector during the sample. Both arms pin this.
        Sim = new() { NativeGotoPlan = true, CrowdingPenalty = "off" },
        LiveSettings = new() { TimeDilation = "off" },
        Games = [new() { Name = "P895 bounded local delivery proof", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], ShowMap = true, MaxTeamImbalance = 0x7fff, MaxPlayersPerTeam = 256 }],
    });
    int spawn = 0;
    server.ShipSpawner = (core, mission, id) =>
    {
        var hull = core.HullTypes.Values.Where(h => h.Speed > 0 && h.HasCapability(DataHullTypeIGC.HabmFighter) && h.Length < 100).OrderBy(h => h.HullId).First();
        int index = spawn++;
        var ship = new Ship(id, hull, new Vector3(2500 + index % 20 * 150, 2500 + index / 20 * 150, 2500),
            new Vector3(0, 0, 20), Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return (ship, mission.Sectors[0]);
    };
    foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
    using var times = new StreamWriter(Path.Combine(state, "ticks.csv")) { AutoFlush = true };
    using var sends = new StreamWriter(Path.Combine(state, "sends.csv")) { AutoFlush = true };
    sends.WriteLine("tick,enqueued,written,queued,inflight,superseded,pollBlocked,writeCalls,busyWriteMs,longestWriteMs,noProgressMs");
    var peerSends = new PeerSendWindow[256];
    var workerSends = new SendWorkerWindow[2];
    var tcp = server.Host.Transport is DualTransport both ? both.Tcp : server.Host.Transport as TcpTransport;
    File.WriteAllText(Path.Combine(state, "port.txt"), server.BoundPort.ToString(CultureInfo.InvariantCulture));
    var ticks = new List<double>();
    var total = Stopwatch.StartNew();
    bool measuring = false;
    TimeSpan cpuStart = TimeSpan.Zero;
    long measureStart = 0, writtenStart = 0;
    var udp = server.Host.Transport is DualTransport dual ? dual.Udp : server.Host.Transport as UdpTransport;
    UdpTransportStatistics? statsStart = null;
    var process = Process.GetCurrentProcess();
    while (!File.Exists(Path.Combine(state, "stop")) && total.Elapsed.TotalSeconds < 150)
    {
        long start = Stopwatch.GetTimestamp();
        if (!measuring && File.Exists(Path.Combine(state, "measure")))
        { measuring = true; cpuStart = process.TotalProcessorTime; measureStart = start; writtenStart = server.Host.Transport!.TotalWrittenBytes; statsStart = udp?.SampleStatistics(); }
        times.WriteLine(FormattableString.Invariant($"{server.LoopIterations + 1},{start}"));
        if (measuring && server.LoopIterations % 10 == 0)
        {
            var frame = FedPacket.Pack([new ChatMessage { Message = "P895:" + start.ToString(CultureInfo.InvariantCulture) }.Encode()]);
            foreach (var peer in server.TestSnapshotPeers()) server.Host.Send(peer.Conn, frame);
        }
        server.TickSim();
        if (measuring && tcp is not null)
        {
            var window = tcp.DrainSendWindow(peerSends, workerSends);
            var livePeers = peerSends.AsSpan(0, window.Peers).ToArray();
            sends.WriteLine(FormattableString.Invariant($"{server.LoopIterations},{window.EnqueuedBytes},{window.WrittenBytes},{window.QueuedBytes},{livePeers.Sum(p => p.InflightBytes)},{livePeers.Sum(p => p.SupersededFrames)},{livePeers.Sum(p => p.PollBlocked)},{workerSends.Sum(w => w.WriteCalls)},{workerSends.Sum(w => w.BusyWriteMs)},{workerSends.Max(w => w.LongestWriteMs)},{(livePeers.Length == 0 ? 0 : livePeers.Max(p => p.NoProgressMs))}"));
        }
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (measuring) ticks.Add(elapsed);
        int remaining = (int)Math.Max(1, 50 - elapsed);
        Thread.Sleep(remaining);
    }
    double measuredSeconds = measuring ? Stopwatch.GetElapsedTime(measureStart).TotalSeconds : 0;
    var metrics = new HostMetrics(ticks.Count, Quantile(ticks, .5), Quantile(ticks, .95), ticks.Count == 0 ? 0 : ticks.Max(),
        measuredSeconds == 0 ? 0 : (process.TotalProcessorTime - cpuStart).TotalSeconds / measuredSeconds,
        server.LivePlayerCount, server.Host.Transport!.TotalWrittenBytes - writtenStart, tcp?.TcpBatchWrites == true);
    if (statsStart is not null) File.WriteAllText(Path.Combine(state, "host-udp.json"), JsonSerializer.Serialize(udp!.SampleStatistics().Since(statsStart), ProofJson.Default.UdpTransportStatistics));
    File.WriteAllText(Path.Combine(state, "host-metrics.json"), JsonSerializer.Serialize(metrics, ProofJson.Default.HostMetrics));
}

void Run()
{
    // This directory is per run. Never remove another run's scratch data.
    foreach (string file in new[] { "port.txt", "ticks.csv", "stop", "measure", "host-metrics.json" })
        File.Delete(Path.Combine(state, file));
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string arg in new[] { "--host", "--state", state, "--protocol", protocol }) start.ArgumentList.Add(arg);
    if (batchWrites) start.ArgumentList.Add("--tcp-batch-writes");
    start.Environment["ALLEGIANCE_TRANSPORT_PROTOCOL"] = protocol;
    start.Environment["ALLEGIANCE_TRANSPORT_MAX_PER_IP"] = "256";
    using var host = Process.Start(start) ?? throw new IOException("failed to start proof server");
    using var readStop = new CancellationTokenSource();
    var times = new ConcurrentDictionary<uint, long>();
    var clients = new List<WireObserver>();
    var proxies = new List<IDisposable>();
    Task? clockReader = null;
    try
    {
        Wait(() => File.Exists(Path.Combine(state, "port.txt")) || host.HasExited, 30000, "server boot");
        if (host.HasExited) throw new IOException("proof server exited: " + File.ReadAllText(Path.Combine(state, "host.log")));
        ushort port = ushort.Parse(File.ReadAllText(Path.Combine(state, "port.txt")), CultureInfo.InvariantCulture);
        clockReader = Task.Run(async () =>
        {
            using var file = new FileStream(Path.Combine(state, "ticks.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            while (!readStop.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync();
                if (line?.Split(',') is { Length: 2 } pair) times[uint.Parse(pair[0], CultureInfo.InvariantCulture)] = long.Parse(pair[1], CultureInfo.InvariantCulture);
                else await Task.Delay(2);
            }
        });
        for (int i = 0; i < bots; i++)
        {
            ushort endpoint = port;
            if (delay != 0 || jitter != 0 || loss != 0)
            {
                if (protocol == "udp")
                { var proxy = new ImpairedUdpProxy(port, loss, delay, jitter, 895 + i); proxies.Add(proxy); endpoint = proxy.Port; }
                else
                { var proxy = new ImpairedTcpProxy(port, loss, delay, jitter, 895 + i); proxies.Add(proxy); endpoint = proxy.Port; }
            }
            var client = new WireObserver(times);
            clients.Add(client);
            client.Transport = protocol == "udp" ? new UdpTransport(client) : new TcpTransport(client);
            Access.Transport(client.Session) = client.Transport;
            string name = $"P895-{i:D3}";
            client.Transport.Join("127.0.0.1", endpoint, name);
            client.Session.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue });
            // Explicit side requests remove the unrelated lobby auto-seat delay.
            client.Session.SendPositionReq(0);
        }
        Wait(() => clients.All(c => { lock (c.Session.Gate) return c.Session.World.MyShipId is not null; }), 30000, "all scripted clients seated");
        Thread.Sleep(1500);
        foreach (var client in clients) client.Measuring = true;
        File.WriteAllText(Path.Combine(state, "measure"), "start");
        long bytesStart = ProxyBytes(proxies);
        var udpStarts = clients.Select(c => (c.Transport as UdpTransport)?.SampleStatistics()).ToArray();
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            foreach (var c in clients) c.Session.CommandControls(new ControlData(0, 0, 0, .25f), 0);
            Thread.Sleep(50);
        }
        foreach (var client in clients) client.Measuring = false;
        double sampleSeconds = timer.Elapsed.TotalSeconds;
        double clientCpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds;
        long bytes = ProxyBytes(proxies) - bytesStart;
        if (protocol == "udp") File.WriteAllText(Path.Combine(state, "client-udp.json"), JsonSerializer.Serialize(clients.Select((c, i) => ((UdpTransport)c.Transport).SampleStatistics().Since(udpStarts[i]!)).ToArray(), ProofJson.Default.UdpTransportStatisticsArray));
        int connectedAtEnd = clients.Count(c => c.Session.Connected);
        File.WriteAllText(Path.Combine(state, "stop"), "stop");
        // Freeze the sample before materializing/sorting millions of pose records.
        // Quantile computation is proof overhead, not client transport CPU.
        if (!host.WaitForExit(10000)) throw new TimeoutException("proof server did not stop");
        // On direct loopback a pose can beat the tick-log reader. Preserve its
        // actual arrival timestamp and correlate afterwards instead of silently
        // discarding it (which fabricated gaps in P895/P898 zero-delay runs).
        Wait(() => clients.All(c => c.Timing.PendingTicksReady()), 5000, "pose tick correlation");
        foreach (var client in clients) client.Timing.ResolvePendingTicks();
        var ages = clients.SelectMany(c => c.Ages).ToList();
        var flow = clients.SelectMany(c => c.Flow).ToList();
        var gaps = clients.SelectMany(c => c.Gaps).ToList();
        var server = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(state, "host-metrics.json")), ProofJson.Default.HostMetrics)!;
        var result = new Measurement(protocol, kernelDelay > 0 ? "external kernel netem (operator configured)" : proxies.Count == 0 ? "direct loopback" : protocol == "tcp" ? "reliable-stream delay/loss model (200ms recovery), not kernel TCP loss" : "real UDP datagram impairment",
            bots, loss, kernelDelay > 0 ? kernelDelay : delay, jitter, seconds, ages.Count, Quantile(ages, .5), Quantile(ages, .95), ages.Count == 0 ? 0 : ages.Max(),
            gaps.Count == 0 ? 0 : gaps.Max(), flow.Count, Quantile(flow, .5), Quantile(flow, .95), flow.Count == 0 ? 0 : flow.Max(),
            proxies.Count == 0 ? server.WrittenBytes / sampleSeconds : bytes / sampleSeconds,
            server.TickP50Ms, server.TickP95Ms, server.TickMaxMs, server.CpuCores,
            clientCpuSeconds / sampleSeconds,
            server.Connected, connectedAtEnd, server.TcpBatchWrites);
        string json = JsonSerializer.Serialize(result, ProofJson.Default.Measurement);
        File.WriteAllText(Path.Combine(state, "result.json"), json + "\n");
        Console.WriteLine(json);
        if (result.PoseSamples == 0 || result.FlowSamples == 0 || result.ConnectedClients != bots) throw new IOException("proof has missing coverage or disconnected clients");
    }
    finally
    {
        File.WriteAllText(Path.Combine(state, "stop"), "stop");
        foreach (var client in clients) client.Session.Dispose();
        foreach (var proxy in proxies) proxy.Dispose();
        readStop.Cancel(); clockReader?.GetAwaiter().GetResult();
        if (!host.HasExited && !host.WaitForExit(10000)) { host.Kill(); host.WaitForExit(); }
    }
}
static long ProxyBytes(IEnumerable<IDisposable> proxies) => proxies.Sum(p => p switch { ImpairedUdpProxy u => u.Bytes, ImpairedTcpProxy t => t.Bytes, _ => 0 });
static void Wait(Func<bool> ready, int milliseconds, string label)
{
    var timer = Stopwatch.StartNew();
    while (!ready()) { if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(label); Thread.Sleep(10); }
}
static double Quantile(List<double> values, double q)
{ if (values.Count == 0) return 0; var sorted = values.Order().ToArray(); return sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))]; }

sealed class WireObserver(ConcurrentDictionary<uint, long> ticks) : ITransportSite
{
    public readonly ConnectionSession Session = new();
    public ITransport Transport = null!;
    public volatile bool Measuring;
    public readonly PoseTiming Timing = new(ticks);
    public ConcurrentQueue<double> Ages => Timing.Ages;
    public ConcurrentQueue<double> Gaps => Timing.Gaps;
    public readonly ConcurrentQueue<double> Flow = new();
    public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        long now = Stopwatch.GetTimestamp();
        if (Measuring)
        {
            foreach (var msg in FedPacket.Unpack(data))
            {
                ushort id = FedMessageHeader.Read(msg).Fmid;
                if (id == HeavyShipsUpdate.Fmid)
                {
                    var update = HeavyShipsUpdate.Decode(msg);
                    foreach (var entry in update.HeavyShipEntries) Pose(entry.ShipId, update.TimeReference, now);
                }
                else if (id == SingleShipUpdate.Fmid)
                { var update = SingleShipUpdate.Decode(msg); Pose(update.Ship.ShipId, update.Ship.Time, now); }
                else if (id == ChatMessage.Fmid)
                {
                    string text = ChatMessage.Decode(msg).Message;
                    if (text.StartsWith("P895:", StringComparison.Ordinal) && long.TryParse(text.AsSpan(5), out long sent))
                        Flow.Enqueue(Stopwatch.GetElapsedTime(sent, now).TotalMilliseconds);
                }
            }
        }
        ((ITransportSite)Session).OnReceive(from, data);
    }
    private void Pose(short ship, uint tick, long now)
    {
        if (ship == Session.World.MyShipId) return;
        Timing.Observe(ship, tick, now);
    }
    public void OnPeerJoined(ConnectionId id, string name) => ((ITransportSite)Session).OnPeerJoined(id, name);
    public void OnPeerLeft(ConnectionId id) => ((ITransportSite)Session).OnPeerLeft(id);
    public void OnSessionLost() => ((ITransportSite)Session).OnSessionLost();
}
static class Access
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    public static extern ref ITransport? Transport(ConnectionSession session);
}
internal sealed record HostMetrics(int Ticks, double TickP50Ms, double TickP95Ms, double TickMaxMs, double CpuCores, int Connected, long WrittenBytes, bool TcpBatchWrites);
internal sealed record Measurement(string Protocol, string Impairment, int Bots, double Loss, int DelayMs, int JitterMs, int Seconds,
    int PoseSamples, double PoseP50Ms, double PoseP95Ms, double PoseMaxMs, double LongestPoseGapMs,
    int FlowSamples, double FlowP50Ms, double FlowP95Ms, double FlowMaxMs, double? BytesPerSecond,
    double ServerTickP50Ms, double ServerTickP95Ms, double ServerTickMaxMs, double ServerCpuCores, double ClientProxyCpuCores,
    int ConnectedServer, int ConnectedClients, bool TcpBatchWrites);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Measurement))]
[JsonSerializable(typeof(HostMetrics))]
[JsonSerializable(typeof(UdpTransportStatistics))]
[JsonSerializable(typeof(UdpTransportStatistics[]))]
internal partial class ProofJson : JsonSerializerContext;
