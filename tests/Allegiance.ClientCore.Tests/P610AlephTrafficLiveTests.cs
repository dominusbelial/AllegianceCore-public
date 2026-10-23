using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P610 — bot fleet traffic spacing at alephs. Live measurements on the REAL
/// 221 configuration (in-process NON-debug m2xspiderw server: side0 home
/// Ganymede 1050, side1 home Organia 1051, rendezvous Alderan 1052), driving
/// real <see cref="BotManager"/> combat bots with STAGING toward Alderan, then
/// parsing the stamped server log for the aleph-traffic metrics:
///   • ship-ship collisions per minute (split same-side vs cross-side),
///   • repeat-pair grinding (a pair colliding &gt;3×),
///   • home-aleph clear time (first to last crossing out of each home sector),
///   • time to reach Alderan,
///   • asteroid collisions,
///   • and that the bots still REACH the target.
/// Env: ALLEGIANCE_LIVE_P610=1, _SIZE (bots/side), _SECONDS, _TAG, _NAME.
/// Writes the full run trace to $TMPDIR/p610_*.txt and prints RESULT lines.
/// Pure measurement: records, does not assert.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P610AlephTrafficLiveTests
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
            DebugMode = false,   // the REAL map (Ganymede/Organia), not the debug Omega/Plato
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { // P797 harness option: the harness bots cannot scout yet (P798).
            Name = "P610 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p610-ticker" };
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
    /// sim-relative wall clock (so [warp]/[collision]/[asteroid] lines can be
    /// ordered + timed).</summary>
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
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P610") == "1") return false;
        Console.WriteLine("[P610] live proofs SKIPPED — set ALLEGIANCE_LIVE_P610=1 to run");
        return true;
    }

    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P610_SIZE"), out var s) ? s : 5;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P610_SECONDS"), out var s) && s > 0 ? s : 240;
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P610_TAG") ?? "default";

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

    private static readonly Regex RxColShip = new(@"\[collision\] ship (\d+) x ship (\d+)", RegexOptions.Compiled);
    private static readonly Regex RxColAst = new(@"\[collision\] ship (\d+) x asteroid (\d+)", RegexOptions.Compiled);
    private static readonly Regex RxWarp = new(@"\[warp\] ship (\d+) crossed from '(.*?)' \((\d+)\) to '(.*?)' \((\d+)\)", RegexOptions.Compiled);
    private static readonly Regex RxTs = new(@"^\[t=([0-9.]+)\]", RegexOptions.Compiled);

    /// <summary>Run ONE P610 aleph-traffic measurement at `size` bots/side with
    /// staging toward Alderan for `seconds`, writing the full trace and returning
    /// the parsed RESULT summary. Pure measurement: no assertion.</summary>
    private static string RunScenario(int size, double seconds, string tag)
    {
        var sb = new StringBuilder();
        var dumpPath = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p610_{tag}.txt");

        var (server, ticker, mgr, tee, saved) = Boot(out var port);
        try
        {
            sb.AppendLine($"P610 {tag} booted 221cfg(m2xspiderw NON-debug) :{port} size={size}/side seconds={seconds:F0}s");
            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            var hull = Catalog.DefaultFighterHull();
            var parts = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
            sb.AppendLine($"HULL {hull.Name} LOADOUT {string.Join(",", parts.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()))}");

            var clusters = mgr.ProbeClusters();
            foreach (var kv in clusters.OrderBy(x => x.Key))
                sb.AppendLine($"SECTOR {kv.Key} '{kv.Value.Name}'");

            const short target = 1052;   // Alderan rendezvous on the 221 map
            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();
            for (int i = 0; i < size; i++)
            {
                var ra = mgr.SpawnBot(hull.Id, hull.Name, a, parts, "combat", target, null, null, useStaging: true);
                botIds.Add(ra.ShipId); sideById[ra.ShipId] = a;
                var rb = mgr.SpawnBot(hull.Id, hull.Name, b, parts, "combat", target, null, null, useStaging: true);
                botIds.Add(rb.ShipId); sideById[rb.ShipId] = b;
            }
            sb.AppendLine($"SPAWNED {botIds.Count} bots (A:{size} side {a}, B:{size} side {b}) staging=true target={target}");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            // Each side's HOME sector on the 221 map is fixed and verified live at
            // size 1/30 (side0 -> Ganymede 1050, side1 -> Organia 1051): roster
            // sides[0] homes at 1050, sides[1] at 1051. Hardcoded because a
            // lazily-sampled home mis-detects once large fleets transit within the
            // first 2 s sample (seen at 60/side: side0 read as Rigel 1059).
            var homeBySide = new Dictionary<short, short> { [a] = 1050, [b] = 1051 };
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                foreach (var d in mgr.DiagnoseWorlds())
                {
                    if (!homeBySide.ContainsKey(d.Side) && d.Sector >= 0 && botIds.Contains(d.ShipId))
                        homeBySide[d.Side] = d.Sector;
                    sb.AppendLine($"POS t={(DateTime.UtcNow - t0).TotalSeconds:F0} {d.Name} side={d.Side} ship={d.ShipId} sector={d.Sector} pos=({d.Pos[0]:F0},{d.Pos[1]:F0},{d.Pos[2]:F0})");
                }
            }

            string log = tee.Captured;
            sb.AppendLine($"HOME_BY_SIDE {string.Join(", ", homeBySide.OrderBy(k => k.Key).Select(k => $"side{k.Key}->{k.Value}"))}");

            // ---- P610 metric parsing ----
            var colPairs = new List<(double T, short A, short B, bool SameSide)>();
            int astCollisions = 0;
            var warps = new List<(double T, short Ship, short FromSec, string FromName, short ToSec, string ToName)>();

            foreach (var line in log.Split('\n'))
            {
                var t = line.Trim();
                double stamp = 0;
                var tm = RxTs.Match(t);
                if (tm.Success) stamp = double.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                var mc = RxColShip.Match(t);
                if (mc.Success)
                {
                    short c1 = short.Parse(mc.Groups[1].Value), c2 = short.Parse(mc.Groups[2].Value);
                    if (botIds.Contains(c1) && botIds.Contains(c2))
                    {
                        bool same = sideById.TryGetValue(c1, out var s1) && sideById.TryGetValue(c2, out var s2) && s1 == s2;
                        colPairs.Add((stamp, Math.Min(c1, c2), Math.Max(c1, c2), same));
                    }
                    continue;
                }
                var ma = RxColAst.Match(t);
                if (ma.Success && botIds.Contains(short.Parse(ma.Groups[1].Value)))
                { astCollisions++; continue; }

                var mw = RxWarp.Match(t);
                if (mw.Success)
                {
                    short sh = short.Parse(mw.Groups[1].Value);
                    if (botIds.Contains(sh))
                        warps.Add((stamp, sh, short.Parse(mw.Groups[3].Value), mw.Groups[2].Value,
                            short.Parse(mw.Groups[5].Value), mw.Groups[4].Value));
                }
            }

            double minutes = seconds / 60.0;
            int totShip = colPairs.Count;
            int sameSide = colPairs.Count(x => x.SameSide);
            int crossSide = totShip - sameSide;
            double perMin = minutes > 0 ? totShip / minutes : 0;
            double perMinSame = minutes > 0 ? sameSide / minutes : 0;
            double perMinCross = minutes > 0 ? crossSide / minutes : 0;

            // Repeat-pair grinding: a (unordered) pair colliding > 3×.
            var pairCounts = colPairs.GroupBy(p => (p.A, p.B)).ToDictionary(g => g.Key, g => g.Count());
            int grindingPairs = pairCounts.Count(kv => kv.Value > 3);
            int maxPairCols = pairCounts.Count > 0 ? pairCounts.Values.Max() : 0;

            // Home-aleph clear time per side: first->last crossing out of the home sector.
            // Reach-Alderan: first crossing INTO 1052 per side; bots that ever reached it.
            var clearTimes = new StringBuilder();
            var reach = new Dictionary<short, double>();   // side -> first reach of 1052
            var reachedBots = new HashSet<short>();
            foreach (var side in new[] { a, b })
            {
                if (!homeBySide.TryGetValue(side, out var home)) continue;
                var sideWarps = warps.Where(w => sideById[w.Ship] == side).ToList();
                var outs = sideWarps.Where(w => w.FromSec == home).Select(w => w.T).ToList();
                if (outs.Count >= 2)
                    clearTimes.Append($" side{side}=home{home} clear={outs[^1] - outs[0]:F1}s(first@{outs[0]:F1},last@{outs[^1]:F1},n={outs.Count})");
                else if (outs.Count == 1)
                    clearTimes.Append($" side{side}=home{home} clear=N/A(1 bot left@{outs[0]:F1})");
                else
                    clearTimes.Append($" side{side}=home{home} clear=N/A(0 crossed)");
                var toTarget = sideWarps.Where(w => w.ToSec == 1052).Select(w => w.T).ToList();
                if (toTarget.Count > 0) reach[side] = toTarget.Min();
                foreach (var w in sideWarps.Where(x => x.ToSec == 1052)) reachedBots.Add(w.Ship);
            }
            sb.AppendLine($"HOME_CLEAR{clearTimes}");
            sb.AppendLine($"REACH_ALDERAN {string.Join(", ", reach.OrderBy(k => k.Key).Select(k => $"side{k.Key}@{k.Value:F1}s"))} botsReached={reachedBots.Count}/{botIds.Count}");

            // ---- RESULT line ----
            string r = $"RESULT_P610 {tag} size={size} window={seconds:F0}s "
                + $"shipCollisions={totShip} sameSide={sameSide} crossSide={crossSide} "
                + $"collPerMin={perMin:F2} samePerMin={perMinSame:F2} crossPerMin={perMinCross:F2} "
                + $"grindingPairs={grindingPairs} maxPairCols={maxPairCols} "
                + $"asteroidCollisions={astCollisions} botsReached={reachedBots.Count}/{botIds.Count} "
                + $"{clearTimes} REACH_ALDERAN={string.Join(",", reach.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value:F1}s"))}";
            sb.AppendLine(r);
            Console.WriteLine(r);

            // Dump raw collision + warp lines for the record.
            sb.AppendLine("RAW_COLLISIONS:");
            foreach (var ln in log.Split('\n'))
                if (RxColShip.IsMatch(ln) || RxColAst.IsMatch(ln) || RxWarp.IsMatch(ln))
                    sb.AppendLine("  " + ln.Trim());
            File.WriteAllText(dumpPath, sb.ToString());
            Console.WriteLine($"P610 wrote {dumpPath}");
            return r;
        }
        finally
        {
            Console.SetOut(saved);
            try { mgr.Dispose(); } catch { }
            try { ticker.Dispose(); } catch { }
            try { server.Dispose(); } catch { }
        }
    }

    [Fact]
    public void Record_aleph_traffic_run()
    {
        if (IsGated()) return;
        RunScenario(Size(), Seconds(), Tag());
    }
}
