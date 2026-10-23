using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539d — fair-contact live proofs on the real 221 configuration. Boots an
/// in-process NON-debug <c>m2xspiderw</c> server (the 13-sector map: side 0 home
/// Ganymede 1050, side 1 home Organia 1051, rendezvous Alderan 1052) with the
/// deployed 221 settings (20 Hz, civIds [25,3], testHarness testUnlock) — NOT
/// the debug map P539c used — and drives real <see cref="BotManager"/> combat
/// bots through <see cref="BotManager.TransitRows"/>, <see cref="BotManager.GetCombatSummary"/>
/// and the captured server log to measure:
///   • per-side arrival (hops, first/last arrival, spread) in travel mode vs
///     travel-with-staging,
///   • collision-vs-gunfire death accounting per side,
///   • symmetry (equal-tech shots within 1.5×) with travel-with-staging,
///   • the full showcase bar (higher-tech side wins, reversal flips it).
///
/// P795 removed the arena mode; this harness now records march only
/// (travel or travel-with-staging). Parameterised by env: ALLEGIANCE_P539D_MODE=travel|staging,
/// _SIZE, _SECONDS, _TECH_A, _TECH_B, _TAG. All gated behind
/// ALLEGIANCE_LIVE_P539D=1; the run trace is written to $TMPDIR/p539d_*.txt.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P539dFairContactLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    /// <summary>The deployed 221 server configuration (NON-debug): 20 Hz,
    /// civIds [25,3], side names Alpha/Bravo, testUnlock via the harness.</summary>
    private static Cfg MapConfig(int ups) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        LiveSettings = new Ls
        {
            DebugMode = false,           // the REAL map (Ganymede/Organia), not the debug Omega/Plato
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { // P797 harness option: the harness bots cannot scout yet (P798).
            ShowMap = true, Name = "P539d (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p539d-ticker" };
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

    /// <summary>Captures server console output and timestamps each line with the
    /// sim-relative wall clock (so [warp]/[eject]/[collision]/[combat] lines can be
    /// ordered + timed for the arrival/collision tables).</summary>
    private sealed class StampTee : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _sb = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        public StampTee(TextWriter inner) => _inner = inner;
        public override void Write(char value)
        {
            _inner.Write(value);
            if (value == '\n')
            {
                lock (_sb) _sb.Append($"[t={_sw.Elapsed.TotalSeconds:F2}] {_line}\n");
                _line.Clear();
            }
            else _line.Append(value);
        }
        public string Captured { get { Flush(); lock (_sb) return _sb.ToString(); } }
        protected override void Dispose(bool disposing) { Flush(); base.Dispose(disposing); }
    }

    private static bool IsGated()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P539D") == "1") return false;
        Console.WriteLine("[P539d] live proofs SKIPPED — set ALLEGIANCE_LIVE_P539D=1 to run");
        return true;
    }

    private static string Mode() => Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_MODE") ?? "staging";
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_SIZE"), out var s) ? s : 5;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_SECONDS"), out var s) && s > 0 ? s : 240;
    private static int TechA() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_TECH_A"), out var t) ? t : 0;
    private static int TechB() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_TECH_B"), out var t) ? t : 0;
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_TAG") ?? Mode();

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                foreach (var model in sec.Models)
                    if (model is Ship s && s.Id == id) return s;
        return null;
    }

    /// <summary>Boot a fresh 221-config server + BotManager. Returns (server,
    /// ticker, mgr). The server stays DISPOSABLE; the caller disposes
    /// ticker+mgr+server in reverse order.</summary>
    private static (Srv server, ServerTicker ticker, BotManager mgr, StampTee tee, TextWriter saved)
        Boot(out int? boundPort)
    {
        var tee = new StampTee(Console.Out);
        var saved = Console.Out;
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(MapConfig(20));
        Console.SetOut(tee);
        boundPort = server.BoundPort;
        var ticker = new ServerTicker(server);
        var mgr = new BotManager("127.0.0.1", server.BoundPort);
        if (!mgr.ServerReady) throw new InvalidOperationException("probe must validate");
        return (server, ticker, mgr, tee, saved);
    }

    private static int _runSeq;

    /// <summary>Run ONE 221-config combat scenario of `size` bots/side with the
    /// given tech levels and mode, for `seconds`, writing a rich summary to a file
    /// and returning the parsed result row (shots/hits/kills/deaths per side,
    /// arrival table). Pure measurement: no assertion here (callers assert).</summary>
    private static (string Summary, string Path) RunScenario(int size, int techA, int techB, string mode, double seconds, string tag)
    {
        int seq = Interlocked.Increment(ref _runSeq);
        var sb = new StringBuilder();
        var dumpPath = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p539d_{tag}_seq{seq}.txt");

        var (server, ticker, mgr, tee, saved) = Boot(out var port);
        try
        {
            sb.AppendLine($"P539D {tag} booted 221cfg(m2xspiderw NON-debug) :{port} size={size} techA={techA} techB={techB} mode={mode} window={seconds:F0}s");
            var sides = mgr.ValidSides();
            // P539d — side-assignment swap: which PHYSICAL roster team plays "side A"
            // (the first / measured side). Swap flips it so symmetry is proven not to
            // depend on the roster index (Ganymede-team vs Organia-team).
            bool swap = Environment.GetEnvironmentVariable("ALLEGIANCE_P539D_SWAP") == "1";
            short a = swap ? sides[1] : sides[0];
            short b = swap ? sides[0] : sides[1];
            var hull = Catalog.DefaultFighterHull();
            var partsA = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techA).Parts;
            var partsB = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techB).Parts;
            sb.AppendLine($"LOADOUT sideA(tech{techA}): {string.Join(", ", partsA.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()))}");
            sb.AppendLine($"LOADOUT sideB(tech{techB}): {string.Join(", ", partsB.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()))}");

            // Resolve the rendezvous (Alderan) + per-side routes from a probe snapshot.
            var clusters = mgr.ProbeClusters();
            short alderan = 1052;
            sb.AppendLine($"RENDEZVOUS sector={alderan} name={(clusters.TryGetValue(alderan, out var c) ? c.Name : "?")}");
            foreach (var kv in clusters)
                sb.AppendLine($"SECTOR {kv.Key} '{kv.Value.Name}'");
            foreach (var s in sides)
                sb.AppendLine($"SIDE {s} -> rendezvous {alderan}");

            bool useStaging = mode is "staging";
            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();

            for (int i = 0; i < size; i++)
            {
                var ra = mgr.SpawnBot(hull.Id, hull.Name, a, partsA, "combat", alderan, null, null, useStaging);
                botIds.Add(ra.ShipId); sideById[ra.ShipId] = a;
                var rb = mgr.SpawnBot(hull.Id, hull.Name, b, partsB, "combat", alderan, null, null, useStaging);
                botIds.Add(rb.ShipId); sideById[rb.ShipId] = b;
            }
            sb.AppendLine($"SPAWNED {botIds.Count} bots (A:{size} B:{size}) mode={mode} staging={useStaging}");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            int sampleIdx = 0;
            var perBotSector = new Dictionary<short, short>();   // shipId -> sector, sampled
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                sampleIdx++;
                foreach (var d in mgr.DiagnoseWorlds())
                {
                    var srvSec = ServerShip(server, d.ShipId) is { } s2 && s2.Sector is { } sec ? sec.Id : (short)-1;
                    sb.AppendLine($"S{sampleIdx} {d.Name} side={d.Side} ship={d.ShipId} world={d.Sector} srv={srvSec} pos=({d.Pos[0]:F0},{d.Pos[1]:F0},{d.Pos[2]:F0})");
                }
                if (sampleIdx % 30 == 0)
                {
                    var it = mgr.GetCombatSummary();
                    sb.AppendLine($"INTERIM t={(DateTime.UtcNow - t0).TotalSeconds:F0}s shotsA={it.Bots.Where(x=>x.Side==a).Sum(x=>x.ShotsFired)} shotsB={it.Bots.Where(x=>x.Side==b).Sum(x=>x.ShotsFired)} kills={it.Kills}");
                }
            }

            // ---- Arrival table (per-bot, from the live telemetry) ----
            sb.AppendLine("ARRIVAL (per bot): name side launchedAt arrivedAt hops warp_hops");
            foreach (var r in mgr.TransitRows().OrderBy(x => x.Side).ThenBy(x => x.LaunchedAt))
                sb.AppendLine($"  ARR {r.Name} side={r.Side} launch={r.LaunchedAt:F1} arrive={(r.ArrivedAt is { } aa ? aa.ToString("F1") : "-")} hops={r.Hops} " +
                              string.Join(";", r.WarpHops.Select(h => $"{h.From}->{h.To}@{h.T:F1}")));

            // ---- Combat + death summary ----
            var csum = mgr.GetCombatSummary();
            foreach (var r in csum.Bots)
                sb.AppendLine($"COMBAT {r.Name} side={r.Side} shots={r.ShotsFired} hits={r.ShipHits} hullHits={r.EnemyHullHits} kills={r.Kills}");
            long shotsA = csum.Bots.Where(x => x.Side == a).Sum(x => x.ShotsFired);
            long shotsB = csum.Bots.Where(x => x.Side == b).Sum(x => x.ShotsFired);
            long hitsA = csum.Bots.Where(x => x.Side == a).Sum(x => x.ShipHits);
            long hitsB = csum.Bots.Where(x => x.Side == b).Sum(x => x.ShipHits);
            long killsA = csum.Bots.Where(x => x.Side == a).Sum(x => x.Kills);
            long killsB = csum.Bots.Where(x => x.Side == b).Sum(x => x.Kills);
            int deathsA = mgr.TransitRows().Where(x => x.Side == a).Sum(x => x.Deaths);
            int deathsB = mgr.TransitRows().Where(x => x.Side == b).Sum(x => x.Deaths);
            int gfA = mgr.TransitRows().Where(x => x.Side == a).Sum(x => x.GunfireDeaths);
            int gfB = mgr.TransitRows().Where(x => x.Side == b).Sum(x => x.GunfireDeaths);
            int colA = mgr.TransitRows().Where(x => x.Side == a).Sum(x => x.CollisionDeaths);
            int colB = mgr.TransitRows().Where(x => x.Side == b).Sum(x => x.CollisionDeaths);
            sb.AppendLine($"RESULT techA={techA} techB={techB} mode={mode} shotsA={shotsA} shotsB={shotsB} hitsA={hitsA} hitsB={hitsB} killsA={killsA} killsB={killsB} deathsA={deathsA} deathsB={deathsB} gfA={gfA} gfB={gfB} colA={colA} colB={colB}");

            // ---- Collision / eject / combat / warp lines from the stamped server log ----
            string log = tee.Captured;
            // Authoritative per-side death accounting (task: gunfire kills =
            // "[combat] … killed by ship X"; collision/other deaths = ejects without a
            // shooter). A gunfire kill's KILL_SHIP credit can arrive seconds AFTER the
            // eject (the P107 eject-gate ordering), so classify by matching each eject
            // against the existence of a credited-killer line for the same victim.
            var shipToSide = new Dictionary<short, short>();
            foreach (var kv in sideById) shipToSide[kv.Key] = kv.Value;
            var sideEjects = new Dictionary<short, int>();      // total deaths per side (one eject = one death)
            var sideGunfire = new Dictionary<short, int>();     // credited gunfire kills on each side's ships
            var gunfireVictims = new HashSet<short>();
            foreach (var line in log.Split('\n'))
            {
                var mEj = Regex.Match(line, @"\[eject\] player ship (\d+) ejected");
                if (mEj.Success && shipToSide.TryGetValue(short.Parse(mEj.Groups[1].Value), out var ejSide))
                { sideEjects[ejSide] = sideEjects.GetValueOrDefault(ejSide) + 1; continue; }
                var mK = Regex.Match(line, @"\[combat\] ship (\d+) killed by ship (-?\d+)");
                if (mK.Success)
                {
                    short v = short.Parse(mK.Groups[1].Value);
                    int killer = int.Parse(mK.Groups[2].Value);
                    if (!shipToSide.ContainsKey(v)) continue;
                    if (killer >= 0 && !gunfireVictims.Contains(v))
                    {
                        gunfireVictims.Add(v);
                        sideGunfire[shipToSide[v]] = sideGunfire.GetValueOrDefault(shipToSide[v]) + 1;
                    }
                }
            }
            int ejA = sideEjects.GetValueOrDefault(a), ejB = sideEjects.GetValueOrDefault(b);
            int gkA = sideGunfire.GetValueOrDefault(a), gkB = sideGunfire.GetValueOrDefault(b);
            sb.AppendLine($"SRVDETH ejectsA={ejA} ejectsB={ejB} gunfireA={gkA} gunfireB={gkB} collisionA={ejA - gkA} collisionB={ejB - gkB}");

            var botsText = botIds.Select(id => id.ToString()).ToHashSet();
            foreach (var line in log.Split('\n'))
            {
                var t = line.Trim();
                bool hasTimestamp = t.StartsWith("[t=", StringComparison.Ordinal);
                string body = hasTimestamp ? t[(t.IndexOf(']') + 1)..] : t;
                if (!botsText.Any(bid => Regex.IsMatch(body, $@"ship {bid}( |\b)|player ship {bid} "))) continue;
                if (body.Contains("[warp]", StringComparison.Ordinal) || body.Contains("[eject]", StringComparison.Ordinal)
                    || body.Contains("[collision]", StringComparison.Ordinal) || body.Contains("[combat] ship", StringComparison.Ordinal)
                    || body.Contains("[p539d] DEATH", StringComparison.Ordinal)
                    || body.Contains("[p539d] side ", StringComparison.Ordinal))
                    sb.AppendLine("SERV " + t);
            }
            File.WriteAllText(dumpPath, sb.ToString());
            Console.WriteLine($"P539D wrote {dumpPath}");
            return (sb.ToString(), dumpPath);
        }
        finally
        {
            Console.SetOut(saved);
            try { mgr.Dispose(); } catch { }
            try { ticker.Dispose(); } catch { }
            try { server.Dispose(); } catch { }
        }
    }

    // ── Live proof entry points (gated) ──

    /// <summary>Topology probe: dump the 221 map sectors/homes + per-side BFS route
    /// lengths to the rendezvous (the premise: Ganymede side-0 home is farther).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_221_topology_and_routes()
    {
        Require.Artwork();
        if (IsGated()) return;
        var sb = new StringBuilder();
        var (server, ticker, mgr, tee, saved) = Boot(out var port);
        sb.AppendLine($"TOPOLOGY 221cfg m2xspiderw :{port}");
        var clusters = mgr.ProbeClusters();
        foreach (var kv in clusters.OrderBy(x => x.Key))
            sb.AppendLine($"  SECTOR {kv.Key} '{kv.Value.Name}'");
        // Asteroid density per sector (the asteroid field is what used to drown
        // the loadout edge in collisions in the removed arena mode).
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                sb.AppendLine($"  DENSITY sector {sec.Id} '{sec.Name}' asteroids={sec.Mineable.Count}");
        // Expected homes per the 13-sector map: side 0 -> Ganymede 1050, side 1 ->
        // Organia 1051; rendezvous Alderan 1052. The per-bot arrival/hops table in
        // the run traces proves the actual route lengths per side.
        sb.AppendLine("  EXPECTED homes: side0=1050 Ganymede, side1=1051 Organia, rendezvous=1052 Alderan (P368/P382)");
        sb.AppendLine("  (per-side route hops are measured live in the arrival table of each run trace)");
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", "p539d_topology.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.SetOut(saved);
        try { mgr.Dispose(); } catch { }
        try { ticker.Dispose(); } catch { }
        try { server.Dispose(); } catch { }
    }

    /// <summary>Arrival + fair-contact measurement for one mode (env-driven). This is
    /// the primary harness used for the per-side arrival table (travel vs
    /// travel-with-staging) and each showcase/symmetry run; it RECORDS, does not assert.</summary>
    [Fact]
    public void Record_one_run()
    {
        if (IsGated()) return;
        var (summary, path) = RunScenario(Size(), TechA(), TechB(), Mode(), Seconds(), Tag());
        // Prints the RESULT + SRVDETH lines to stdout for the caller to capture.
        foreach (var line in summary.Split('\n'))
            if (line.StartsWith("RESULT ") || line.StartsWith("SRVDETH "))
                Console.WriteLine(line);
        Console.WriteLine($"PATH {path}");
    }
}
