using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Transport;

namespace Allegiance.Synth;

/// <summary>
/// P605 — synthetic high-peer transport benchmark. Runs a bare
/// <see cref="TcpTransport"/> host (NO full game server — so the process's
/// threads/RSS/CPU are purely the transport's) with a fixed pool of lightweight
/// loopback peers receiving a fixed broadcast rate, and reports server-side
/// thread count, RSS and CPU for comparing thread-per-peer vs pool mode at
/// 1000+ peers.
///
/// Two roles:
///   server: p605synth server &lt;port&gt; &lt;mode&gt; &lt;workers&gt; &lt;peers&gt; &lt;rateHz&gt; &lt;frameBytes&gt; &lt;seconds&gt; &lt;outfile&gt;
///     (mode = "thread-per-peer" | "pool"; workers used only in pool mode)
///   client: p605synth client &lt;addr&gt; &lt;port&gt; &lt;peers&gt; &lt;seconds&gt;
///
/// The server samples its OWN /proc (Linux) for Threads / VmRSS / CPU% over a
/// final window and writes a JSON line to stdout + outfile. The client role
/// connects N peers (each greeting, then reading+discarding in its own thread)
/// so the server-side egress is actually consumed (no backpressure).
/// </summary>
internal static class Program
{
    private const byte Greet = 0x01, Data = 0x02;

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Usage(); return 2; }
        return args[0] switch
        {
            "server" => RunServer(args),
            "client" => RunClient(args),
            _ => throw new ArgumentException($"unknown role '{args[0]}'"),
        };
    }

    private static void Usage()
    {
        Console.Error.WriteLine("p605synth server <port> <mode> <workers> <peers> <rateHz> <frameBytes> <seconds> <outfile>");
        Console.Error.WriteLine("p605synth client <addr> <port> <peers> <seconds>");
    }

    private sealed class Site : ITransportSite
    {
        private readonly List<ConnectionId> _joined = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (_joined) _joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
        public List<ConnectionId> Snapshot() { lock (_joined) return new List<ConnectionId>(_joined); }
        public int Joined { get { lock (_joined) return _joined.Count; } }
    }

    private static int RunServer(string[] a)
    {
        int port = int.Parse(a[1]);
        string mode = a[2];
        int workers = int.Parse(a[3]);
        int peers = int.Parse(a[4]);
        int rateHz = int.Parse(a[5]);
        int frameBytes = int.Parse(a[6]);
        int seconds = int.Parse(a[7]);
        string outfile = a[8];

        var tm = mode == "pool" ? TransportMode.Pool : TransportMode.ThreadPerPeer;
        var site = new Site();
        var host = new TcpTransport(site, tm, poolWorkers: workers);
        host.Host((ushort)port);

        // Accept all peers (each greets via the transport's greeting protocol).
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(20, peers / 20 + 10));
        while (site.Joined < peers && DateTime.UtcNow < deadline) Thread.Sleep(20);
        if (site.Joined < peers) { Console.Error.WriteLine($"only {site.Joined}/{peers} joined"); host.Shutdown(); return 1; }

        // Fixed broadcast rate: one frame to every peer every (1/rateHz) s.
        var cts = new CancellationTokenSource();
        long bytesSent = 0;
        var broadcaster = new Thread(() =>
        {
            var frame = new byte[frameBytes];
            new Random(1234).NextBytes(frame);
            var periodUs = 1_000_000L / rateHz;
            var sw = Stopwatch.StartNew();
            long pass = 0;
            while (!cts.IsCancellationRequested)
            {
                var ids = site.Snapshot();
                foreach (var id in ids) { host.Send(id, frame, guaranteed: true); Interlocked.Add(ref bytesSent, frame.Length); }
                pass++;
                // Pace to ~rateHz passes/second (fixed broadcast rate).
                long wantUs = pass * periodUs;
                long elapsedUs = sw.Elapsed.Ticks / 10;
                if (elapsedUs < wantUs) Thread.Sleep((int)((wantUs - elapsedUs) / 1000));
            }
        }) { IsBackground = true, Name = "synth-broadcaster" };
        broadcaster.Start();

        // Let it run for the requested window (clients stay connected the whole
        // time), then measure WHILE the broadcaster and peer threads are still
        // active — that is the state we are benchmarking. CPU% is taken over a
        // 3 s window of sustained broadcast+send, and thread/RSS right after.
        Thread.Sleep(seconds * 1000);

        var (threads, vmRssKb) = ProcStatus();
        var cpuStart = CpuClockTicks();
        long cpuWallStart = Stopwatch.GetTimestamp();
        Thread.Sleep(3000);
        long cpuWallElapsed = (Stopwatch.GetTimestamp() - cpuWallStart) * 1_000_000 / Stopwatch.Frequency; // us
        long cpuDelta = CpuClockTicks() - cpuStart; // clock ticks
        double cpuPercent = cpuDelta / (double)(cpuWallElapsed / 1_000_000.0) / ClkTck * 100.0;

        cts.Cancel();
        Thread.Sleep(500);

        var result = new
        {
            mode = mode,
            configuredPoolWorkers = tm == TransportMode.Pool ? workers : 0,
            peers,
            rateHz,
            frameBytes,
            seconds,
            osThreads = threads,
            vmRssKb,
            cpuPercent,
            bytesSent,
        };
        string json = JsonSerializer.Serialize(result);
        Console.WriteLine("RESULT " + json);
        File.WriteAllText(outfile, json + "\n");

        host.Shutdown();
        return 0;
    }

    private static int RunClient(string[] a)
    {
        string addr = a[1];
        int port = int.Parse(a[2]);
        int peers = int.Parse(a[3]);
        int seconds = int.Parse(a[4]);

        var done = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        for (int i = 0; i < peers; i++)
        {
            int idx = i;
            var t = new Thread(() => ClientLoop(addr, port, idx, () => done.IsSet))
            { IsBackground = true, Name = $"synth-client-{i}" };
            threads.Add(t);
            t.Start();
        }
        Thread.Sleep(seconds * 1000);
        done.Set();
        foreach (var t in threads) t.Join(2000);
        long total = TotalRxRef;
        Console.WriteLine($"CLIENT_RESULT {{ \"peers\": {peers}, \"seconds\": {seconds}, \"totalRxBytes\": {total} }}");
        return 0;
    }

    private static void ClientLoop(string addr, int port, int idx, Func<bool> stop)
    {
        long rx = 0;
        try
        {
            var raw = new TcpClient();
            raw.NoDelay = true;
            raw.Connect(addr, port);
            var ns = raw.GetStream();
            // Greet.
            var name = Encoding.UTF8.GetBytes($"synth-{idx}");
            var greet = new byte[1 + 4 + name.Length];
            greet[0] = Greet;
            BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)name.Length);
            name.CopyTo(greet.AsSpan(5));
            ns.Write(greet); ns.Flush();

            // Read frames until told to stop.
            var hdr = new byte[5];
            while (!stop())
            {
                int n = ReadUpTo(ns, hdr, 0, 5);
                if (n < 5) break;
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1));
                if (len > (1 << 24)) break;
                var body = new byte[len];
                int got = 0;
                while (got < body.Length) { int r = ReadUpTo(ns, body, got, body.Length - got); if (r <= 0) goto endloop; got += r; }
                rx += len;
            }
        endloop:
            Interlocked.Add(ref TotalRxRef, rx);
            try { raw.Dispose(); } catch { }
        }
        catch { }
    }

    private static long TotalRxRef;

    private static int ReadUpTo(NetworkStream ns, byte[] buf, int off, int count)
    {
        try { return ns.Read(buf, off, count); }
        catch { return 0; }
    }

    // ---- /proc samplers (Linux) ----
    private const long ClkTck = 100;

    private static long CpuClockTicks()
    {
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            int close = stat.LastIndexOf(')');
            var rest = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // after comm: state(0) ppid(1) ... utime(11) stime(12)
            long utime = long.Parse(rest[11]);
            long stime = long.Parse(rest[12]);
            return utime + stime;
        }
        catch (Exception e) { Console.Error.WriteLine("CpuTicks err: " + e); return 0; }
    }

    private static (long Threads, long VmRssKb) ProcStatus()
    {
        long threads = 0, rss = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Threads:"))
                    threads = long.Parse(line.Split(':', 2)[1].Trim().Split()[0]);
                else if (line.StartsWith("VmRSS:"))
                    rss = long.Parse(line.Split(':', 2)[1].Trim().Split()[0]);
            }
        }
        catch (Exception e) { Console.Error.WriteLine("ProcStatus err: " + e); }
        return (threads, rss);
    }
}
