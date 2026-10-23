using System.Diagnostics;
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
/// P622 — the anti-swarm live proof. Reproduces the P622 incident on a LOCAL
/// in-process 221-map server (m2xspiderw NON-debug, side0 home Ganymede 1050,
/// side1 home Organia 1051, rendezvous Alderan 1052): one side's combat fleet
/// arrives at Alderan while the only enemy present is a lone MINER (side B's
/// miner-hulled bots, flown to and parked in Alderan ahead of time). Measures,
/// every 2 s:
///   • the ENGAGER DISTRIBUTION over time — how many side-A bots engage each
///     target (the swarm measure: BEFORE P622 the whole fleet sits on the one
///     miner; AFTER it is capped to a small detachment and the rest HOLD),
///   • that the bots REACH the target (mandatory live check),
///   • same-side COLLISIONS per minute (the swarm grinds against the rock),
///   • KILLS (enemy ships destroyed).
/// BEFORE/AFTER: run the same test process twice from the shell — WITHOUT
/// ALLEGIANCE_P622_LEGACY_NEAREST for "after" (P622 active), and with it set
/// for "before" (the pre-P622 nearest-enemy swarm), same size/timer.
/// Env: ALLEGIANCE_LIVE_P622=1, _SIZE (fighters/side), _MINERS (enemy miner
/// count), _SECONDS, _OFFSET (s), _TAG. Writes the trace to $TMPDIR/p622_*.txt
/// and prints RESULT lines. Pure measurement: records, does not assert.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P622TargetSpreadLiveTests
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
            ShowMap = true, Name = "P622 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p622-ticker" };
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
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P622") == "1") return false;
        Console.WriteLine("[P622] live proofs SKIPPED — set ALLEGIANCE_LIVE_P622=1 to run");
        return true;
    }
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P622_SIZE"), out var s) ? s : 60;
    private static int Miners() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P622_MINERS"), out var s) ? s : 2;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P622_SECONDS"), out var s) && s > 0 ? s : 240;
    private static double Offset() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P622_OFFSET"), out var s) && s > 0 ? s : 12;
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P622_TAG") ?? "default";

    private static readonly Regex RxColShip = new(@"\[collision\] ship (\d+) x ship (\d+)", RegexOptions.Compiled);
    private static readonly Regex RxWarp = new(@"\[warp\] ship (\d+) crossed from '(.*?)' \((\d+)\) to '(.*?)' \((\d+)\)", RegexOptions.Compiled);
    private static readonly Regex RxTs = new(@"^\[t=([0-9.]+)\]", RegexOptions.Compiled);

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

    /// <summary>Resolve the fighter hull (default) and the MINER hull candidates
    /// (every hull with c_habmMiner) from the REAL core. The server refuses
    /// DRONE-only miners (GroupId&lt;0 — P470/P441: native's hangar never sells a
    /// drone hull, and the drone gate runs even under testHarness), so the harness
    /// tries each candidate in order (player-buyable GroupId≥0 first) and uses the
    /// first that actually spawns.</summary>
    private static (Catalog.Hull fighter, List<Catalog.Hull> minerCandidates) Hulls()
    {
        var fighter = Catalog.DefaultFighterHull();
        var core = Catalog.RepresentativeCore();
        var miners = new List<Catalog.Hull>();
        foreach (var h in core.HullTypes.Values)
        {
            if (!h.HasCapability(DataHullTypeIGC.HabmMiner)) continue;
            miners.Add(new Catalog.Hull(h.HullId, h.Name));
        }
        // Player-buyable miners (GroupId >= 0) first, then drone miners.
        return (fighter, miners
            .OrderByDescending(m => core.HullTypes[m.Id].GroupId >= 0)
            .ThenBy(m => m.Id).ToList());
    }

    private static string RunScenario(int size, int miners, double seconds, double offset, string tag)
    {
        var sb = new StringBuilder();
        var dumpPath = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p622_{tag}.txt");

        var (server, ticker, mgr, tee, saved) = Boot(out var port);
        try
        {
            sb.AppendLine($"P622 {tag} booted 221cfg(m2xspiderw NON-debug) :{port} "
                + $"fighters/side={size} enemyMiners={miners} offset={offset:F0}s seconds={seconds:F0}s");

            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            var (fh, minerCandidates) = Hulls();
            var fighterParts = Catalog.NativePreferredLoadout(fh.Id, fh.Name, 0).Parts;
            sb.AppendLine($"FIGHTER {fh.Name} ({fh.Id}) MINER_CANDIDATES "
                + string.Join(",", minerCandidates.Select(c => $"{c.Name}({c.Id})")));

            const short target = 1052;   // Alderan rendezvous on the 221 map

            // ── Phase 1: park the enemy MINERS in Alderan (side B, fly straight) ──
            // Try each miner hull candidate in order (drone miners are refused by
            // the server even under testHarness); first success per slot wins.
            var minerIds = new List<short>();
            var minerUsed = new List<(short Id, string Name)>();
            var refused = new List<string>();
            for (int i = 0; i < miners; i++)
            {
                bool spawned = false;
                foreach (var cand in minerCandidates)
                {
                    if (minerUsed.Any(u => u.Id == cand.Id)) continue;   // one of each hull per slot
                    var parts = Catalog.NativePreferredLoadout(cand.Id, cand.Name, 0).Parts;
                    try
                    {
                        var rm = mgr.SpawnBot(cand.Id, cand.Name, b, parts, "combat", target, useStaging: false);
                        minerIds.Add(rm.ShipId); minerUsed.Add((cand.Id, cand.Name)); spawned = true;
                        break;
                    }
                    catch (InvalidOperationException ex) { refused.Add($"{cand.Name}:{ex.Message.Substring(0, Math.Min(60, ex.Message.Length))}"); }
                }
                if (!spawned)
                {
                    sb.AppendLine($"WARN: no buyable miner hull for slot {i} — using a fighter as the lone target");
                    var rf = mgr.SpawnBot(fh.Id, fh.Name, b, fighterParts, "combat", target, useStaging: false);
                    minerIds.Add(rf.ShipId); minerUsed.Add((fh.Id, fh.Name + "#fighter"));
                }
            }
            sb.AppendLine($"SPAWNED {minerIds.Count} enemy lone-targets (side {b}, hulls {string.Join(",", minerUsed.Select(u => $"{u.Name}({u.Id})"))}) target={target}");
            if (refused.Count > 0) sb.AppendLine("MINER_REFUSED: " + string.Join(" | ", refused.Distinct()));
            var t0 = DateTime.UtcNow;
            var parkedDeadline = t0.AddSeconds(90);
            while (DateTime.UtcNow < parkedDeadline)
            {
                var diags = mgr.DiagnoseWorlds().Where(d => minerIds.Contains(d.ShipId)).ToList();
                if (diags.Count > 0 && diags.All(d => d.Sector == target)) break;
                Thread.Sleep(2000);
            }
            var minerSectors = mgr.DiagnoseWorlds().Where(d => minerIds.Contains(d.ShipId))
                .Select(d => $"#{d.ShipId}->{d.Sector}").ToList();
            sb.AppendLine($"MINER_SECTORS {string.Join(", ", minerSectors)}");

            // ── Phase 2: side A combat fleet arrives first (staged) ──
            var allIds = new HashSet<short>(minerIds);
            var sideById = new Dictionary<short, short>();
            foreach (var m in minerIds) sideById[m] = b;
            var aIds = new List<short>();
            for (int i = 0; i < size; i++)
            {
                var ra = mgr.SpawnBot(fh.Id, fh.Name, a, fighterParts, "combat", target, useStaging: true);
                aIds.Add(ra.ShipId); allIds.Add(ra.ShipId); sideById[ra.ShipId] = a;
            }
            sb.AppendLine($"SPAWNED side {a}: {aIds.Count} fighters (staged)");

            // ── Phase 3: side B combat fleet arrives later (the "one side first") ──
            if (offset > 0)
            {
                Thread.Sleep((int)(offset * 1000));
                for (int i = 0; i < size; i++)
                {
                    var rb = mgr.SpawnBot(fh.Id, fh.Name, b, fighterParts, "combat", target, useStaging: true);
                    allIds.Add(rb.ShipId); sideById[rb.ShipId] = b;
                }
                sb.AppendLine($"SPAWNED side {b}: {size} fighters (staged, +{offset:F0}s)");
            }

            // ── Measurement loop ──
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            int maxOnSingleTarget = 0;
            int maxOnMiner = 0;
            int maxEngagingEnemies = 0;
            int maxHolding = 0;
            var reached = new HashSet<short>();          // ever in Alderan
            var everPresent = new HashSet<short>(allIds);
            var lastPresent = new HashSet<short>(allIds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                double el = (DateTime.UtcNow - t0).TotalSeconds;
                var diags = mgr.DiagnoseWorlds().ToList();
                var present = new HashSet<short>(diags.Select(d => d.ShipId));
                foreach (var d in diags) if (d.Sector == target) reached.Add(d.ShipId);

                // Engager distribution for side-A bots in Alderan.
                var views = mgr.ActiveBots();
                var buckets = new Dictionary<int, int>();
                int holding = 0;
                foreach (var v in views)
                {
                    if (v.Side != a) continue;
                    if (v.Sector != target) continue;
                    if (v.Target is { } t) buckets[t] = buckets.TryGetValue(t, out var c) ? c + 1 : 1;
                    else holding++;
                }
                int onSingle = buckets.Count > 0 ? buckets.Values.Max() : 0;
                int onMiner = buckets.Where(kv => minerIds.Contains((short)kv.Key)).Sum(kv => kv.Value);
                int engaging = buckets.Values.Sum();
                maxOnSingleTarget = Math.Max(maxOnSingleTarget, onSingle);
                maxOnMiner = Math.Max(maxOnMiner, onMiner);
                maxEngagingEnemies = Math.Max(maxEngagingEnemies, engaging);
                maxHolding = Math.Max(maxHolding, holding);
                var dist = string.Join(",", buckets.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
                sb.AppendLine($"SAMPLE t={el:F0} sideAInAlderan={views.Count(v => v.Side == a && v.Sector == target)} "
                    + $"engaging={engaging} holding={holding} maxOnSingle={onSingle} minerEngagers={onMiner} "
                    + $"targets={{ {dist} }} present={present.Count}");
            }

            // ── Kills: any spawned ship id that was ever present then vanished ──
            var dead = everPresent.Except(lastPresent).ToList();
            int minerDeaths = dead.Count(minerIds.Contains);
            int enemyDeaths = dead.Count(s => sideById.TryGetValue(s, out var sd) && sd == b);

            string log = tee.Captured;
            // ── Collisions (same-side = the swarm grinding) ──
            var colPairs = new List<(double T, short A, short B, bool SameSide)>();
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
                    if (allIds.Contains(c1) && allIds.Contains(c2))
                    {
                        bool same = sideById.TryGetValue(c1, out var s1) && sideById.TryGetValue(c2, out var s2) && s1 == s2;
                        colPairs.Add((stamp, Math.Min(c1, c2), Math.Max(c1, c2), same));
                    }
                }
            }
            double minutes = seconds / 60.0;
            int sameSide = colPairs.Count(x => x.SameSide);
            double collPerMin = minutes > 0 ? colPairs.Count / minutes : 0;

            string r = $"RESULT_P622 {tag} fighters/side={size} enemyMiners={miners} window={seconds:F0}s "
                + $"botsReached={reached.Count}/{allIds.Count} "
                + $"maxEngagersOnSingleTarget={maxOnSingleTarget} (cap fighter={CombatTargetSelector.CapFighter}, miner={CombatTargetSelector.CapMinerDrone}) "
                + $"maxMinerEngagers={maxOnMiner} maxEngaging={maxEngagingEnemies} maxHolding={maxHolding} "
                + $"shipCollisions={colPairs.Count} sameSide={sameSide} collPerMin={collPerMin:F2} "
                + $"kills={dead.Count} minerDeaths={minerDeaths} enemyDeaths={enemyDeaths}";
            sb.AppendLine(r);
            Console.WriteLine(r);
            sb.AppendLine("RAW_COLLISIONS:");
            foreach (var ln in log.Split('\n'))
                if (RxColShip.IsMatch(ln) || RxWarp.IsMatch(ln))
                    sb.AppendLine("  " + ln.Trim());
            File.WriteAllText(dumpPath, sb.ToString());
            Console.WriteLine($"P622 wrote {dumpPath}");
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
    public void Record_target_spread_run()
    {
        if (IsGated()) return;
        RunScenario(Size(), Miners(), Seconds(), Offset(), Tag());
    }
}
