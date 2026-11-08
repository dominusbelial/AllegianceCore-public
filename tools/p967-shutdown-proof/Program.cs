// P967 — bounded loopback proof for the transport shutdown / tombstone leaks.
//
//   dotnet run --project tools/p967-shutdown-proof -c Release
//
// Scenario A (P963-04): a client that connects before Shutdown, stays silent,
//   and greets AFTER Shutdown returns must NOT join.
// Scenario B (P963-08): connection churn must leave the ended-peer tombstone
//   table bounded (2048 recent departures), not one entry per historical
//   connection, while recent departures still report closed.
// Scenario C (P963-04b): Shutdown must own every in-flight greeting socket and
//   thread — none may outlive it.
//
// On master (2cdfd100) this program fails: A reports joinedAfterLateGreet=1 in
// both modes, B40 reports endedMarkers=40 with no retirement path, and B2600
// reports 2600 markers. It passes on the fixed tree. Everything is bounded
// waits on real loopback sockets; no synthetic load, no remote host.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

static int FieldCount(TcpTransport t, string name)
{
    var f = typeof(TcpTransport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    var v = f?.GetValue(t);
    return v switch
    {
        System.Collections.ICollection c => c.Count,
        int i => i,
        null => -1,
        _ => -1,
    };
}

static void SendGreet(NetworkStream s, string name)
{
    var body = Encoding.UTF8.GetBytes(name);
    var frame = new byte[5 + body.Length];
    frame[0] = 0x01; // FrameType.Greet
    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
    body.CopyTo(frame, 5);
    s.Write(frame);
    s.Flush();
}

static bool WaitUntil(Func<bool> cond, int ms)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (cond()) return true;
        Thread.Sleep(5);
    }
    return cond();
}

int failures = 0;

// ── Scenario A: late greeting after Shutdown ───────────────────────────────
foreach (var mode in new[] { TransportMode.ThreadPerPeer, TransportMode.Pool })
{
    var site = new Site();
    var host = new TcpTransport(site, mode, poolWorkers: 2);
    host.Host(0);
    ushort port = host.BoundPort;
    var raw = new TcpClient();
    raw.NoDelay = true;
    raw.Connect("127.0.0.1", port);
    Assert(WaitUntil(() => FieldCount(host, "_pendingGreets") == 1, 3000),
        $"[A/{mode}] accept must register the pending greeting");
    var swShutdown = Stopwatch.StartNew();
    host.Shutdown();
    swShutdown.Stop();
    int joinedAtShutdownReturn = site.JoinedCount();
    // Greet only now — the transport must already be shut down.
    var stream = raw.GetStream();
    try { SendGreet(stream, "late-greeter"); } catch (Exception e) { Console.WriteLine($"[A/{mode}] greet write failed: {e.GetType().Name}"); }
    Thread.Sleep(1500);
    int joinedAfter = site.JoinedCount();
    int peers = FieldCount(host, "_peers");
    int threads = FieldCount(host, "_threads");
    int pending = FieldCount(host, "_pendingGreets");
    Console.WriteLine($"[A/{mode}] shutdownMs={swShutdown.ElapsedMilliseconds} joinedAtShutdownReturn={joinedAtShutdownReturn} joinedAfterLateGreet={joinedAfter} livePeers={peers} liveThreads={threads} pendingGreets={pending}");
    if (joinedAfter > 0) { failures++; Console.WriteLine($"[A/{mode}] FAIL: post-shutdown join ids=[{string.Join(",", site.Joined)}]"); }
    else Console.WriteLine($"[A/{mode}] OK: no post-shutdown join");
    if (threads > 0) { failures++; Console.WriteLine($"[A/{mode}] FAIL: {threads} tracked thread(s) outlived shutdown"); }
    try { raw.Dispose(); } catch { }
    host.Shutdown();
}

// ── Scenario B: churn tombstone bound ─────────────────────────────────────
// B1 keeps the same 40 cycles the master run used, so the "one marker per
// departed connection" observation is directly comparable; B2 pushes far past
// the bounded recent window and proves the table stops growing.
foreach (var mode in new[] { TransportMode.ThreadPerPeer, TransportMode.Pool })
{
    foreach (var cycles in new[] { 40, 2600 })
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, poolWorkers: 2);
        host.Host(0);
        ushort port = host.BoundPort;
        int connected = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < cycles; i++)
        {
            var raw = new TcpClient();
            raw.NoDelay = true;
            raw.Connect("127.0.0.1", port);
            SendGreet(raw.GetStream(), $"churn-{i}");
            if (WaitUntil(() => site.JoinedCount() > connected, 3000)) connected++;
            raw.Dispose();                    // abrupt drop
            WaitUntil(() => site.LeftCount() >= connected, 3000);
        }
        sw.Stop();

        (int total, string detail) LivePerConnState(TcpTransport t)
        {
            var total = 0;
            var sb = new StringBuilder();
            foreach (var f in typeof(TcpTransport).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (f.Name == "_peerEnded") continue;   // bounded tombstones, reported separately
                if (f.GetValue(t) is not System.Collections.IDictionary d) continue;
                var args = f.FieldType.GetGenericArguments();
                if (args.Length != 2 || args[0] != typeof(ConnectionId)) continue;
                total += d.Count;
                sb.Append($"{f.Name}={d.Count} ");
            }
            return (total, sb.ToString().TrimEnd());
        }

        var (live, liveDetail) = LivePerConnState(host);
        int ended = FieldCount(host, "_peerEnded");
        int peers = FieldCount(host, "_peers");
        int threads = FieldCount(host, "_threads");
        var endedOrder = typeof(TcpTransport).GetField("_endedOrder", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host) as System.Collections.ICollection;
        Console.WriteLine($"[B{cycles}/{mode}] cycles={cycles} joined={connected} left={site.LeftCount()} ms={sw.ElapsedMilliseconds} peers={peers} threads={threads} livePerConnectionState={live} [{liveDetail}] endedMarkers={ended} endedRetireQueue={endedOrder?.Count ?? -1}");
        if (live > 0) { failures++; Console.WriteLine($"[B{cycles}/{mode}] FAIL: live per-connection state grew with churn ({live})"); }
        if (ended > 2100) { failures++; Console.WriteLine($"[B{cycles}/{mode}] FAIL: ended markers exceeded the bounded window ({ended})"); }
        // Late-packet suppression must still answer "closed" for the recent ids.
        bool newestClosed = host.IsPeerClosed(new ConnectionId((uint)cycles));
        bool oldestClosed = host.IsPeerClosed(new ConnectionId(1));
        Console.WriteLine($"[B{cycles}/{mode}] IsPeerClosed newest={newestClosed} oldest(1)={oldestClosed}");
        if (!newestClosed) { failures++; Console.WriteLine($"[B{cycles}/{mode}] FAIL: the newest departed id is not reported closed"); }
        if (cycles > 2100 && oldestClosed) { failures++; Console.WriteLine($"[B{cycles}/{mode}] FAIL: the oldest departure was never retired"); }
        host.Shutdown();
    }
}

// ── Scenario C: Shutdown owns an in-flight greeting (socket still silent,
//    never greeted) — it must close it and join its thread. ────────────────
foreach (var mode in new[] { TransportMode.ThreadPerPeer, TransportMode.Pool })
{
    var site = new Site();
    var host = new TcpTransport(site, mode, poolWorkers: 2);
    host.Host(0);
    ushort port = host.BoundPort;
    var raw = new TcpClient();
    raw.NoDelay = true;
    raw.Connect("127.0.0.1", port);
    Assert(WaitUntil(() => FieldCount(host, "_pendingGreets") == 1, 3000),
        $"[C/{mode}] accept must register the pending greeting");
    var sw = Stopwatch.StartNew();
    host.Shutdown();
    sw.Stop();
    int threadsAfter = FieldCount(host, "_threads");
    int joinedC = site.JoinedCount();
    bool socketClosed = false;
    // A closed greeting socket: the first write may still land in the kernel
    // buffer, the next one fails with EPIPE/RST. If the greeting socket is
    // still owned by the (still parked) greet thread every write succeeds.
    for (int i = 0; i < 10 && !socketClosed; i++)
    {
        try
        {
            SendGreet(raw.GetStream(), "probe");
            Thread.Sleep(50);
        }
        catch (IOException) { socketClosed = true; }
        catch (SocketException) { socketClosed = true; }
        catch (ObjectDisposedException) { socketClosed = true; }
    }
    Thread.Sleep(300);
    Console.WriteLine($"[C/{mode}] shutdownMs={sw.ElapsedMilliseconds} trackedThreads={threadsAfter} pendingGreets={FieldCount(host, "_pendingGreets")} greetSocketClosed={socketClosed} joined={site.JoinedCount()} (was {joinedC})");
    if (threadsAfter != 0) { failures++; Console.WriteLine($"[C/{mode}] FAIL: {threadsAfter} thread(s) outlived shutdown"); }
    if (!socketClosed) { failures++; Console.WriteLine($"[C/{mode}] FAIL: the pending greeting socket outlived shutdown"); }
    if (site.JoinedCount() != 0) { failures++; Console.WriteLine($"[C/{mode}] FAIL: the late greeting joined after shutdown"); }
    try { raw.Dispose(); } catch { }
    host.Shutdown();
}

void Assert(bool ok, string what)
{
    if (!ok) { failures++; Console.WriteLine($"FAIL: {what}"); }
}

Console.WriteLine(failures == 0 ? "PROOF_RESULT: PASS (all scenarios held)" : $"PROOF_RESULT: FAIL ({failures} assertion(s))");
return failures == 0 ? 0 : 1;

sealed class Site : ITransportSite
{
    public readonly List<ConnectionId> Joined = new();
    public readonly List<ConnectionId> Left = new();
    public readonly List<int> Received = new();
    public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
    public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
    public void OnSessionLost() { }
    public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { lock (Received) Received.Add(data.Length); }
    public int JoinedCount() { lock (Joined) return Joined.Count; }
    public int LeftCount() { lock (Left) return Left.Count; }
    public int ReceivedCount() { lock (Received) return Received.Count; }
}
