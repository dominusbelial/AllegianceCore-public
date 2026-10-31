using System.Diagnostics;
using Allegiance.Core.Profiling;
using Allegiance.Core.Transport;

if (args.FirstOrDefault() == "--local-host")
{
    await LocalHostProof.Run(args[1], args[2] == "lan");
    return;
}
if (args.FirstOrDefault() == "--udp-client")
{
    using var session = new Allegiance.ClientCore.Session.ConnectionSession { TransportProtocol = "udp" };
    session.Connect(ushort.Parse(args[1]), "P907-AOT");
    if (session.ActiveTransportProtocol != "udp") throw new IOException("AOT proof fell back to TCP");
    session.SendLogonRequest(new() { CharacterName = "P907-AOT", FedsrvVer = Allegiance.Core.Wire.LogonRequest.MsgVer, DwCookie = uint.MaxValue });
    if (!session.WaitForAck(5000).FValidated) throw new IOException("AOT UDP logon refused");
    await Task.Delay(6500);
    Console.WriteLine("[aot-client-proof] protocol=udp validated=true");
    return;
}

// Run in its own process: no pool settings are changed, and blocked work items
// cannot affect another test's pool. Main thread releases every wait in finally.
using var hostSite = new Site();
using var clientSite = new Site();
using var host = new UdpTransport(hostSite) { MeasureReceiveDelay = true };
using var client = new UdpTransport(clientSite);
using var pool = new ThreadPoolTelemetry();
host.Host();
client.Join("127.0.0.1", host.BoundPort, "P907-stall");
if (!hostSite.Joined.WaitOne(3000)) throw new TimeoutException("UDP join");

// Complete normal UDP/pool samples before holding the pool.
client.Send(ConnectionId.Server, new byte[] { 1 }, true);
if (!hostSite.Received.WaitOne(3000)) throw new TimeoutException("baseline UDP delivery");
pool.Sample(); Thread.Sleep(100);
var baseline = pool.DrainWindow();
var receiveBaseline = host.DrainReceiveDelay();
Print("baseline", baseline, receiveBaseline);
Thread.Sleep(1000); // respect the once-per-second probe cadence

using var release = new ManualResetEvent(false);
using var firstBlocked = new ManualResetEvent(false);
using var finished = new CountdownEvent(32);
for (int i = 0; i < 32; i++)
    ThreadPool.QueueUserWorkItem(_ =>
    {
        try { firstBlocked.Set(); release.WaitOne(5000); }
        finally { finished.Signal(); }
    });
double udpMs;
ThreadPoolWindow during;
try
{
    if (!firstBlocked.WaitOne(3000)) throw new TimeoutException("blocked work startup");
    // Allow available workers to enter the already queued blocked waits.
    Thread.Sleep(100);
    pool.Sample();
    long sent = Stopwatch.GetTimestamp();
    client.Send(ConnectionId.Server, new byte[] { 2 }, true);
    Thread.Sleep(800); // dedicated main thread; no busy loops or synthetic CPU load
    during = pool.DrainWindow();
    Print("blocked", during, host.DrainReceiveDelay());
    release.Set();
    if (!hostSite.Received.WaitOne(3000)) throw new TimeoutException("stalled UDP delivery");
    udpMs = Stopwatch.GetElapsedTime(sent, Interlocked.Read(ref hostSite.LastReceivedAt)).TotalMilliseconds;
}
finally
{
    release.Set();
    if (!finished.Wait(6000)) throw new TimeoutException("blocked work cleanup");
}
Thread.Sleep(100);
var recovered = pool.DrainWindow();
Print("released", recovered, host.DrainReceiveDelay());
Thread.Sleep(1100);
pool.Sample(); Thread.Sleep(100);
var normal = pool.DrainWindow();
Print("recovered", normal, host.DrainReceiveDelay());
Console.WriteLine(FormattableString.Invariant($"[stall-proof] udpSession=true sentToObservedMs={udpMs:0.###} pendingProbeMs={during.ProbePendingMs:0.###} releasedProbeMaxMs={recovered.Delay.MaxMs:0.###} recoveredProbeMaxMs={normal.Delay.MaxMs:0.###}"));
if (during.ProbePendingMs < 500 || recovered.Delay.MaxMs < 500 || normal.Delay.Samples == 0 || normal.Delay.MaxMs >= recovered.Delay.MaxMs)
    throw new IOException("did not observe a bounded pool stall and recovery");

static void Print(string phase, ThreadPoolWindow pool, UdpReceiveDelayWindow udp) => Console.WriteLine(FormattableString.Invariant(
    $"[stall-phase] phase={phase} threads={pool.Threads} pending={pool.PendingItems} completedPerSecond={pool.CompletedPerSecond:0.###} probeSamples={pool.Delay.Samples} probeMeanMs={pool.Delay.MeanMs:0.###} probeP95UpperMs={pool.Delay.P95UpperMs:0.###} probeMaxMs={pool.Delay.MaxMs:0.###} probePendingMs={pool.ProbePendingMs:0.###} udpAsync={udp.CompletionToWorker.Samples} udpHandoffMaxMs={udp.CompletionToWorker.MaxMs:0.###}"));

sealed class Site : ITransportSite, IDisposable
{
    public readonly AutoResetEvent Joined = new(false), Received = new(false);
    public long LastReceivedAt;
    public void OnPeerJoined(ConnectionId id, string name) => Joined.Set();
    public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        Interlocked.Exchange(ref LastReceivedAt, Stopwatch.GetTimestamp());
        Received.Set();
    }
    public void OnPeerLeft(ConnectionId id) { }
    public void OnSessionLost() { }
    public void Dispose() { Joined.Dispose(); Received.Dispose(); }
}
