using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P635 — the multi-route showcase live harness. On the 221 map <c>m2xspiderw</c>
/// (homes Ganymede 1050 / Organia 1051, rendezvous Alderan 1052) it spawns N
/// fighters per side to the neutral rendezvous and compares the arrival/combat
/// outcome when each side is split across ALL its distinct entry alephs
/// (routes=auto, P635) versus funnelling the whole side through its one shortest
/// path (routes=single, the pre-P635 behaviour):
///   • routes found — the per-side distinct entry routes actually assigned;
///   • per-route arrivals over time + total arrivals (target ≥ 95% of 2N);
///   • kills — the mandatory combat check (both ship-update models at 30v30).
/// Model: ALLEGIANCE_LIVE_P635=1 enables it; _SIZE (fighters/side, default 15),
/// _SECONDS (window, default 150), _SHIPUPDATE_MODEL (port|native, default port),
/// _ROUTES (auto|single, default auto), _STAGING (default ON, matching the
/// P633/P635 staging baseline), _DEBUG, _TAG. Writes the trace to
/// $TMPDIR/p635_&lt;tag&gt;.txt and prints RESULT_P635. Pure measurement: records,
/// does not gate (the ≥95% target and kills verdict are reported honestly).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P635MultiRouteLiveTests
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
            new Gm { // P797 harness option: the harness bots cannot scout yet (P798).
            Name = "P635 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p635-ticker" };
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

    private static readonly Regex KillRx = new(@"\[combat\] ship (?<victim>-?\d+) killed by ship (?<killer>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex TickProfRx = new(@"\[tickprof\] window [\d.]+s ticks=(?<ticks>\d+)", RegexOptions.Compiled);
    private static readonly Regex WarpRx = new(@"\[warp\]", RegexOptions.Compiled);

    private static bool IsGated()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P635") == "1") return false;
        Console.WriteLine("[P635] live harness SKIPPED — set ALLEGIANCE_LIVE_P635=1 to run");
        return true;
    }
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P635_SIZE"), out var s) && s > 0 ? s : 15;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P635_SECONDS"), out var s) && s > 0 ? s : 150;
    private static int Debug() => Environment.GetEnvironmentVariable("ALLEGIANCE_P635_DEBUG") == "1" ? 1 : 0;
    private static string Model() => Environment.GetEnvironmentVariable("ALLEGIANCE_P635_SHIPUPDATE_MODEL") ?? "port";
    private static string RoutesArg() => (Environment.GetEnvironmentVariable("ALLEGIANCE_P635_ROUTES") ?? "auto").ToLowerInvariant();
    private static bool Staging() => Environment.GetEnvironmentVariable("ALLEGIANCE_P635_STAGING") != "0";  // default ON
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P635_TAG") ?? "default";

    internal static void Run(int size, double seconds, string model, string routesArg, bool staging, int dbg, string tag)
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
            Console.WriteLine($"P635 {tag}: booted m2xspiderw :{server.BoundPort} shipUpdateModel={model} debug={dbg} size={size}/side window={seconds:F0}s routes={routesArg} staging={staging}");

            using var ticker = new ServerTicker(server);
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            if (!mgr.ServerReady) throw new InvalidOperationException("BotManager probe must validate");

            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            const short rendezvous = 1052;   // Alderan

            // P635 — discover each side's distinct entry routes; single-route when
            // routesArg == "single" or fewer than 2 routes exist (falls back cleanly).
            var routesA = routesArg == "auto" ? mgr.FindRoutes(rendezvous, mgr.FindSideHomeSector(a), 3) : new List<List<short>>();
            var routesB = routesArg == "auto" ? mgr.FindRoutes(rendezvous, mgr.FindSideHomeSector(b), 3) : new List<List<short>>();
            bool splitA = routesA.Count >= 2, splitB = routesB.Count >= 2;
            var countA = new int[Math.Max(1, routesA.Count)];
            var countB = new int[Math.Max(1, routesB.Count)];
            Console.WriteLine($"P635 {tag}: routes A={Describe(routesA)} B={Describe(routesB)} (splitA={splitA} splitB={splitB})");

            var hull = Catalog.DefaultFighterHull();
            var parts = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();
            for (int i = 0; i < size; i++)
            {
                var ra = SpawnSide(mgr, a, hull, parts, rendezvous, staging, splitA, routesA, countA, out _);
                botIds.Add(ra.ShipId); sideById[ra.ShipId] = a;
                var rb = SpawnSide(mgr, b, hull, parts, rendezvous, staging, splitB, routesB, countB, out _);
                botIds.Add(rb.ShipId); sideById[rb.ShipId] = b;
            }
            Console.WriteLine($"P635 {tag}: spawned {botIds.Count} bots (A:{size} B:{size}) rendezvous={rendezvous} routes={routesArg} staging={staging} — window {seconds:F0}s (model={model})");

            // arrivals-over-time: (t, totalReached, present).
            var curve = new List<(double Sec, int Reached, int Present)>();
            var reached = new HashSet<short>();

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                double el = (DateTime.UtcNow - t0).TotalSeconds;
                var diags = mgr.DiagnoseWorlds().ToList();
                var present = new HashSet<short>(diags.Select(d => d.ShipId));
                foreach (var d in diags) if (d.Sector == rendezvous) reached.Add(d.ShipId);
                curve.Add((el, reached.Count, present.Count));
                Console.WriteLine($"P635 {tag}: t={el:F0}s reached={reached.Count} present={present.Count}");
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

            long maxTicks = 0;
            foreach (Match m in TickProfRx.Matches(log))
                if (long.TryParse(m.Groups["ticks"].Value, out var tk) && tk > maxTicks) maxTicks = tk;

            long shots = cs.ShotsFired, hits = cs.ShipHits;
            double hr = shots > 0 ? (double)hits / shots : 0;

            var perRoute = mgr.RouteArrivals().ToList();
            Console.WriteLine($"RESULT_P635 {tag} model={model} routes={routesArg} size={size}/side window={seconds:F0}s "
                + $"botsReached={reached.Count}/{botIds.Count} killsA={killsA} killsB={killsB} killsTotal={killsA + killsB} "
                + $"deathsA={deathsA} deathsB={deathsB} shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} "
                + $"maxTickWindow={maxTicks} perRoute={string.Join(";", perRoute.Select(r => $"s{r.Side}r{r.RouteIndex}={r.Arrived}/{r.Assigned}"))}");

            var sb = new StringBuilder();
            sb.AppendLine($"--- P635 {tag} model={model} routes={routesArg} ---");
            sb.AppendLine($"RESULT botsReached={reached.Count}/{botIds.Count} killsA={killsA} killsB={killsB} killsTotal={killsA + killsB} "
                + $"deathsA={deathsA} deathsB={deathsB} shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} maxTickWindow={maxTicks}");
            sb.AppendLine($"ROUTES-FOUND A={Describe(routesA)} B={Describe(routesB)}");
            sb.AppendLine("PER-ROUTE (side,route,assigned,arrived):");
            foreach (var r in perRoute)
                sb.AppendLine($"  side {r.Side} route {r.RouteIndex} ({r.RouteName}): {r.Arrived}/{r.Assigned}");
            sb.AppendLine("ARRIVAL-CURVE t,reached,present:");
            foreach (var (sec, r, pr) in curve)
                sb.AppendLine($"  {sec,5:0} {r,4} {pr,4}");
            sb.AppendLine("--- SERVER LOG (warp crossed / tickprof / kill tail) ---");
            foreach (var ln in log.Split('\n'))
                if (WarpRx.IsMatch(ln) || ln.Contains("crossed", StringComparison.Ordinal) || TickProfRx.IsMatch(ln) || KillRx.IsMatch(ln))
                    sb.AppendLine("  " + ln.Trim());
            var dump = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p635_{tag}.txt");
            File.WriteAllText(dump, sb.ToString());
            Console.WriteLine($"P635 {tag} wrote {dump}");
        }
        finally
        {
            Console.SetOut(saved);
            if (oldModel is null) Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", null);
            else Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", oldModel);
        }
    }

    private static string Describe(IReadOnlyList<List<short>> routes) =>
        routes.Count == 0 ? "(none)" : string.Join(" | ", routes.Select(r => "[" + string.Join(",", r) + "]"));

    /// <summary>Spawn one side's bot, assigning it to the least-loaded route wing when
    /// a split applies; returns the bot plus its assigned route index (−1 = single).</summary>
    private static BotManager.SpawnResult SpawnSide(BotManager mgr, short side, Catalog.Hull hull,
        IReadOnlyList<ExpandedPartData> parts, short? target, bool staging, bool split, List<List<short>> routes,
        int[] counts, out int routeIndex)
    {
        List<short>? routePath = null;
        routeIndex = -1;
        if (split)
        {
            routeIndex = BotManager.PickLeastLoadedRoute(counts);
            counts[routeIndex]++;
            routePath = routes[routeIndex];
        }
        return mgr.SpawnBot(hull.Id, hull.Name, side, parts, "combat", target, null, null,
            useStaging: staging, routePath: routePath, routeIndex: routeIndex);
    }

    [Fact]
    public void Record_multi_route_showcase()
    {
        if (IsGated()) return;
        Run(Size(), Seconds(), Model(), RoutesArg(), Staging(), Debug(), Tag());
    }
}
