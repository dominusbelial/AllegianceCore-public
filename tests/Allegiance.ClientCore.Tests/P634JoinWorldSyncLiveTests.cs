using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P634 — the join/world-sync priority live harness. On the 221 map
/// <c>m2xspiderw</c> (both teams home in separate sectors, rendezvous Alderan
/// 1052) it spawns N combat fighters per side to the same neutral rendezvous
/// and measures the P634 numbers end to end:
///   • world-ready — per-bot harness-clock delay from launch (LaunchedAt) to
///     ClientWorld.WorldReady (routing GRAPH present), the scope-1 metric
///     ("time from logon to world complete") that distinguishes a starved join
///     (bots loiter at home because their graph never arrives) from a healthy
///     one. Reports how many of N are world-ready and the p50/p95/max delay.
///   • arrivals over time — the cumulative count of bots that crossed into
///     Alderan, sampled every few seconds (the reach curve).
///   • EnsureCombatPath retries — how many times a bot backed off waiting for
///     the graph (scope 3: no hammering).
///   • kills — real [combat] lines (the mandatory combat check, both models).
///   • server per-peer queue — the [p634-queue] per-window lines (oldest
///     guaranteed-frame age, ordered/stream lane depths).
/// Model: ALLEGIANCE_LIVE_P634=1 enables it; _SIZE (fighters/side, default 30),
/// _SECONDS (window, default 150), _SHIPUPDATE_MODEL (port|native, default
/// port), _DEBUG, _STAGING (default OFF, matching the P633 200v200 baseline),
/// _TAG. Writes the trace to $TMPDIR/p634_&lt;tag&gt;.txt and prints RESULT_P634.
/// Pure measurement: records, does not assert (the target ≥95% arrivals is
/// reported honestly, not gated — the report owns the verdict).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P634JoinWorldSyncLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg MapConfig(int ups) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        LiveSettings = new Ls
        {
            DebugMode = Debug() != 0,
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { Name = "P634 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private sealed class ServerTicker : IDisposable
    {
        private readonly Srv _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        public ServerTicker(Srv server)
        {
            _server = server;
            _thread = new Thread(Loop) { IsBackground = true, Name = "p634-ticker" };
            _thread.Start();
        }
        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _server.TickSim(); } catch { }
                Thread.Sleep(4);
            }
        }
        public void Dispose() { _cts.Cancel(); _thread.Join(2000); }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public TeeWriter(TextWriter inner) => _inner = inner;
        public override void Write(char value) { _inner.Write(value); lock (_sb) _sb.Append(value); }
        public string Captured { get { lock (_sb) return _sb.ToString(); } }
    }

    private sealed record WorldReadyStat(double ReadyFraction, int ReadyCount, int Total,
        double P50, double P95, double Max, int MaxBackoffAttempts);

    private static readonly Regex KillRx = new(@"\[combat\] ship (?<victim>-?\d+) killed by ship (?<killer>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex QueueRx = new(@"\[p634-queue\] peers=(?<peers>\d+) maxOrderedPending=(?<ord>\d+) maxStreamPending=(?<str>\d+) maxQueuedBytes=(?<mb>\d+) totalQueuedBytes=(?<tb>\d+) maxOldestGuaranteedMs=(?<age>\d+)", RegexOptions.Compiled);
    private static readonly Regex TickProfRx = new(@"\[tickprof\] window [\d.]+s ticks=(?<ticks>\d+)", RegexOptions.Compiled);

    private static bool IsGated()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P634") == "1") return false;
        Console.WriteLine("[P634] live harness SKIPPED — set ALLEGIANCE_LIVE_P634=1 to run");
        return true;
    }
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P634_SIZE"), out var s) && s > 0 ? s : 30;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P634_SECONDS"), out var s) && s > 0 ? s : 150;
    private static int Debug() => Environment.GetEnvironmentVariable("ALLEGIANCE_P634_DEBUG") == "1" ? 1 : 0;
    private static string Model() => Environment.GetEnvironmentVariable("ALLEGIANCE_P634_SHIPUPDATE_MODEL") ?? "port";
    private static bool Staging() => Environment.GetEnvironmentVariable("ALLEGIANCE_P634_STAGING") == "1";
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P634_TAG") ?? "default";

    private static WorldReadyStat SummarizeReady(IReadOnlyList<BotManager.ActiveBotView> views, IReadOnlyList<double> readyDelays)
    {
        var lst = readyDelays.Where(d => d >= 0).OrderBy(d => d).ToList();
        int total = views.Count(v => v.Behavior == "combat");
        if (lst.Count == 0)
            return new WorldReadyStat(0, 0, total, double.NaN, double.NaN, double.NaN,
                views.Count > 0 ? views.Max(v => v.WorldNotReadyAttempts) : 0);
        double Pctl(double p)
        {
            if (lst.Count == 1) return lst[0];
            double idx = p * (lst.Count - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return lst[lo] + (idx - lo) * (lst[hi] - lst[lo]);
        }
        return new WorldReadyStat((double)lst.Count / total, lst.Count, total,
            Pctl(0.50), Pctl(0.95), lst[^1],
            views.Count > 0 ? views.Max(v => v.WorldNotReadyAttempts) : 0);
    }

    internal static void Run(int size, double seconds, string model, int dbg, string tag)
    {
        var oldModel = Environment.GetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL");
        Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", model);
        var tee = new TeeWriter(Console.Out);
        var saved = Console.Out;
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(MapConfig(20));
            Console.SetOut(tee);
            Console.WriteLine($"P634 {tag}: booted m2xspiderw :{server.BoundPort} shipUpdateModel={model} debug={dbg} size={size}/side window={seconds:F0}s");

            using var ticker = new ServerTicker(server);
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            if (!mgr.ServerReady) throw new InvalidOperationException("BotManager probe must validate");

            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            const short rendezvous = 1052;   // Alderan, neutral rendezvous on the 221 map

            var hull = Catalog.DefaultFighterHull();
            var parts = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();
            for (int i = 0; i < size; i++)
            {
                var ra = mgr.SpawnBot(hull.Id, hull.Name, a, parts, "combat", rendezvous, useStaging: Staging());
                botIds.Add(ra.ShipId); sideById[ra.ShipId] = a;
                var rb = mgr.SpawnBot(hull.Id, hull.Name, b, parts, "combat", rendezvous, useStaging: Staging());
                botIds.Add(rb.ShipId); sideById[rb.ShipId] = b;
            }
            Console.WriteLine($"P634 {tag}: spawned {botIds.Count} bots (A:{size} B:{size}) rendezvous={rendezvous} staging={(Staging() ? "on" : "off")} — window {seconds:F0}s (model={model})");

            // world-ready per bot: shipId -> delay (harness clock sec); once latched, kept.
            var readyDelay = new Dictionary<short, double>();
            // arrivals-over-time curve.
            var curve = new List<(double Sec, int Reached, int Present, int ReadyCount, int TotalReady)>();

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            var reached = new HashSet<short>();
            var everPresent = new HashSet<short>(botIds);
            var lastPresent = new HashSet<short>(botIds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                double el = (DateTime.UtcNow - t0).TotalSeconds;
                var views = mgr.ActiveBots().ToList();
                var byShip = views.ToDictionary(v => v.ShipId);
                var diags = mgr.DiagnoseWorlds().ToList();
                var present = new HashSet<short>(diags.Select(d => d.ShipId));
                lastPresent = present;
                foreach (var d in diags) if (d.Sector == rendezvous) reached.Add(d.ShipId);
                int readyHere = 0, totalReady = 0;
                foreach (var v in views)
                {
                    if (v.Behavior != "combat") continue;
                    totalReady++;
                    if (v.WorldReady) readyHere++;
                    if (v.WorldReady && v.WorldReadyDelaySec >= 0 && !readyDelay.ContainsKey(v.ShipId))
                        readyDelay[v.ShipId] = v.WorldReadyDelaySec;
                }
                curve.Add((el, reached.Count, present.Count, readyHere, totalReady));
                Console.WriteLine($"P634 {tag}: t={el:F0}s reached={reached.Count} present={present.Count} worldReady={readyHere}/{totalReady} " +
                                  $"readyDelayP50SoFar={Median(readyDelay.Values.Where(d => d >= 0)):0.0}s");
            }

            var cs = mgr.GetCombatSummary();
            string log = tee.Captured;
            long killsA = 0, killsB = 0, deathsA = 0, deathsB = 0;
            foreach (Match m in KillRx.Matches(log))
            {
                if (!short.TryParse(m.Groups["victim"].Value, out var vic)) continue;
                if (!short.TryParse(m.Groups["killer"].Value, out var kil)) continue;
                if (!botIds.Contains(vic) || !botIds.Contains(kil)) continue;
                if (sideById[kil] == a) killsA++; else killsB++;
                if (sideById[vic] == a) deathsA++; else deathsB++;
            }
            var dead = everPresent.Except(lastPresent).ToList();

            // world-ready stats from the FINAL latched set.
            var finalViews = mgr.ActiveBots().ToList();
            var stat = SummarizeReady(finalViews, readyDelay.Values.ToList());

            // server per-peer queue stats (max over window) from the captured log.
            long maxAge = 0, maxOrd = 0, maxStr = 0, maxTb = 0, maxTicks = 0;
            foreach (Match m in QueueRx.Matches(log))
            {
                if (long.TryParse(m.Groups["age"].Value, out var ag) && ag > maxAge) maxAge = ag;
                if (long.TryParse(m.Groups["ord"].Value, out var od) && od > maxOrd) maxOrd = od;
                if (long.TryParse(m.Groups["str"].Value, out var st) && st > maxStr) maxStr = st;
                if (long.TryParse(m.Groups["tb"].Value, out var tb) && tb > maxTb) maxTb = tb;
            }
            foreach (Match m in TickProfRx.Matches(log))
                if (long.TryParse(m.Groups["ticks"].Value, out var tk) && tk > maxTicks) maxTicks = tk;

            long shots = cs.ShotsFired, hits = cs.ShipHits;
            double hr = shots > 0 ? (double)hits / shots : 0;

            Console.WriteLine($"RESULT_P634 {tag} model={model} debug={dbg} size={size}/side window={seconds:F0}s "
                + $"botsReached={reached.Count}/{botIds.Count} worldReady={stat.ReadyCount}/{stat.Total} "
                + $"worldReadyDelay p50={stat.P50:0.0}s p95={stat.P95:0.0}s max={stat.Max:0.0}s "
                + $"maxBackoff={stat.MaxBackoffAttempts} killsA={killsA} killsB={killsB} killsTotal={killsA + killsB} "
                + $"deathsA={deathsA} deathsB={deathsB} shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} "
                + $"serverMaxQueueOrd={maxOrd} maxQueueStr={maxStr} maxOldestGuaranteedMs={maxAge} maxTickWindow={maxTicks}");

            var sb = new StringBuilder();
            sb.AppendLine($"--- P634 {tag} model={model} debug={dbg} ---");
            sb.AppendLine($"RESULT botsReached={reached.Count}/{botIds.Count} worldReady={stat.ReadyCount}/{stat.Total} "
                + $"worldReadyDelay p50={stat.P50:0.0}s p95={stat.P95:0.0}s max={stat.Max:0.0}s maxBackoff={stat.MaxBackoffAttempts} "
                + $"killsA={killsA} killsB={killsB} killsTotal={killsA + killsB} deathsA={deathsA} deathsB={deathsB} "
                + $"shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} "
                + $"serverMaxQueueOrd={maxOrd} maxQueueStr={maxStr} maxOldestGuaranteedMs={maxAge} maxTickWindow={maxTicks}");
            sb.AppendLine("ARRIVAL-CURVE t,reached,present,worldReady,totalReady:");
            foreach (var (sec, r, pr, rc, tr) in curve)
                sb.AppendLine($"  {sec,5:0} {r,4} {pr,4} {rc,4} {tr,4}");
            sb.AppendLine("WORLD-READY-DELAYS (sec) per bot:");
            foreach (var (ship, del) in readyDelay.OrderBy(kv => kv.Value))
                sb.AppendLine($"  ship {ship}: {del:0.0}s");
            sb.AppendLine("--- SERVER LOG (p634-queue / warp crossed / tickprof / kill tail) ---");
            foreach (var ln in log.Split('\n'))
                if (QueueRx.IsMatch(ln) || ln.Contains("[warp] ", StringComparison.Ordinal)
                    || ln.Contains("crossed", StringComparison.Ordinal) || TickProfRx.IsMatch(ln) || KillRx.IsMatch(ln))
                    sb.AppendLine("  " + ln.Trim());
            var dump = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p634_{tag}.txt");
            File.WriteAllText(dump, sb.ToString());
            Console.WriteLine($"P634 {tag} wrote {dump}");
        }
        finally
        {
            Console.SetOut(saved);
            if (oldModel is null) Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", null);
            else Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", oldModel);
        }
    }

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        if (l.Count == 0) return 0;
        return l[l.Count / 2];
    }

    [Fact]
    public void Record_join_world_sync_baseline()
    {
        if (IsGated()) return;
        Run(Size(), Seconds(), Model(), Debug(), Tag());
    }
}
