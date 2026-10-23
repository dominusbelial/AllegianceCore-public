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
/// P632 — the port-mode "bots no longer kill each other" regression repro, and
/// the forward mandatory live check ("bots reach the target AND fight: kills &gt;
/// 0"). On the 221 map <c>m2xspiderw</c> (both teams home in separate sectors,
/// rendezvous Alderan 1052) it stages a FAIR, SYMMETRIC face-off: <c>N</c>
/// same-loadout combat fighters per side, both sent to the SAME neutral
/// rendezvous sector (no lone-target / home-field asymmetry), staging OFF, for
/// a measured window. It reports, every few seconds, the metrics that the P632
/// hypothesis hinges on:
///   • real kills — cross-checked against the server's authoritative
///     <c>[combat] ship X killed by ship Y</c> lines (same cross-check P539b
///     uses) and the bot-attributed <c>GetCombatSummary</c>,
///   • real fire — shots / damaging hits / hit-rate,
///   • target acquisition — how many bots hold a NON-NULL engagement target,
///   • per-bot VISIBLE ENEMY count — the number of cross-side ships in each
///     bot's ClientWorld (via <see cref="BotManager.DiagnoseWorlds"/>), the
///     direct test of "port-mode cross-side visibility" — the P632 hypothesis.
///   • botsReached — the mandatory reach check (bots crossed into Alderan).
///
/// Model: <c>ALLEGIANCE_LIVE_P632=1</c> enables it; <c>_SIZE</c> (fighters/
/// side, default 30), <c>_SECONDS</c> (window, default 150),
/// <c>_SHIPUPDATE_MODEL</c> (port|native, default port) selects the broadcast
/// mode, <c>_DEBUG</c> (default 0 = NON-debug, the 221/P622 faithful map)
/// toggles DebugMode, <c>_TAG</c> labels the output file. Writes the trace to
/// <c>$TMPDIR/p632_&lt;tag&gt;.txt</c> and prints a RESULT_P632 line. Pure
/// measurement: records, does not assert (the FAILING-FIRST regression
/// assertion lives in <see cref="Assert_port_bots_kill_in_window"/> which is
/// gated off by default so the measurement probe never aborts a run early).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P632PortModeZeroKillsLiveTests
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
            DebugMode = Debug() != 0,   // the 221/P622 map (Ganymede/Organia/Alderan)
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { // P797 harness option: the harness bots cannot scout yet (P798).
            Name = "P632 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p632-ticker" };
            _thread.Start();
        }
        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _server.TickSim(); }
                catch { }
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

    internal sealed record Outcome(
        string Tag, string Model, int Size, int Debug, double Window,
        long KillsA, long KillsB, long KillsTotal,
        long DeathsA, long DeathsB,
        long Shots, long Hits, double HitRate, long FriendlyHits,
        int BotsReached, int BotsTotal,
        int MaxTargeted, int MaxVisEnemies);

    private static readonly Regex KillRx = new(@"\[combat\] ship (?<victim>-?\d+) killed by ship (?<killer>-?\d+)", RegexOptions.Compiled);

    private static bool IsGated()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P632") == "1") return false;
        Console.WriteLine("[P632] live repro SKIPPED — set ALLEGIANCE_LIVE_P632=1 to run");
        return true;
    }
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P632_SIZE"), out var s) && s > 0 ? s : 30;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P632_SECONDS"), out var s) && s > 0 ? s : 150;
    private static int Debug() => Environment.GetEnvironmentVariable("ALLEGIANCE_P632_DEBUG") == "1" ? 1 : 0;
    private static string Model() => Environment.GetEnvironmentVariable("ALLEGIANCE_P632_SHIPUPDATE_MODEL") ?? "port";
    private static bool Staging() => Environment.GetEnvironmentVariable("ALLEGIANCE_P632_STAGING") == "1";
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P632_TAG") ?? "default";

    /// <summary>Run ONE fair symmetric face-off and report the outcome.</summary>
    internal static Outcome Run(int size, double seconds, string model, int dbg, string tag)
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
            Console.WriteLine($"P632 {tag}: booted m2xspiderw :{server.BoundPort} shipUpdateModel={model} debug={dbg} size={size}/side window={seconds:F0}s");

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
            Console.WriteLine($"P632 {tag}: spawned {botIds.Count} bots (A:{size} B:{size}) rendezvous={rendezvous} staging={(Staging() ? "on" : "off")} — fighting {seconds:F0}s (model={model})");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            var reached = new HashSet<short>();
            var everPresent = new HashSet<short>(botIds);
            var lastPresent = new HashSet<short>(botIds);
            int maxTargeted = 0, maxVis = 0, poll = 0;
            var perfLines = new List<string>();
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                double el = (DateTime.UtcNow - t0).TotalSeconds;
                var diags = mgr.DiagnoseWorlds().ToList();
                var present = new HashSet<short>(diags.Select(d => d.ShipId));
                lastPresent = present;
                foreach (var d in diags) if (d.Sector == rendezvous) reached.Add(d.ShipId);
                var views = mgr.ActiveBots();
                int targeted = views.Count(v => v.Target is { } t && botIds.Contains((short)t));
                int visEnemies = diags.Count > 0
                    ? (int)Math.Round(diags.Average(d => d.CrossSideShips.Count(cs => cs.Visible)))
                    : 0;
                int visMax = diags.Count > 0 ? diags.Max(d => d.CrossSideShips.Count(cs => cs.Visible)) : 0;
                maxTargeted = Math.Max(maxTargeted, targeted);
                maxVis = Math.Max(maxVis, visMax);
                Console.WriteLine($"P632 {tag}: t={el:F0}s reached={reached.Count} present={present.Count} targeted={targeted} avgVisEnemies={visEnemies} maxVis={visMax}");
                // P636 — surface the decision-loop perf (pass distribution + per-bot
                // decision rate) so a run directly measures the transit-collapse fix.
                if (poll % 5 == 4)
                {
                    var ps = mgr.GetPerfStats(null);
                    string pl = $"P636PERF {tag}: t={el:F0}s passMean={ps.DecisionPass.MeanMs:F1} passP50={ps.DecisionPass.P50Ms:F1} passP95={ps.DecisionPass.P95Ms:F1} passMax={ps.DecisionPass.MaxMs:F1} decisionHz={ps.DecisionRateHz:F3} cpu={ps.Resources.CpuFraction:F1} gen0={ps.Resources.Gen0} gen2={ps.Resources.Gen2} botCount={ps.Cadence.BotCount} snapAgeMean={ps.SnapAgeMs.MeanMs:F1} snapAgeP95={ps.SnapAgeMs.P95Ms:F1} snapAgeMax={ps.SnapAgeMs.MaxMs:F0} pubPs={ps.SnapshotStats.PublishPerSec:F1} pubMsPs={ps.SnapshotStats.PublishMsPerSec:F1} decLagMean={ps.SnapshotStats.DecodeLagMean:F1} decLagP95={ps.SnapshotStats.DecodeLagP95:F1} decLagMax={ps.SnapshotStats.DecodeLagMax:F0}";
                    Console.WriteLine(pl);
                    perfLines.Add(pl);
                }
                poll++;
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

            long shots = cs.ShotsFired, hits = cs.ShipHits;
            double hr = shots > 0 ? (double)hits / shots : 0;
            var o = new Outcome(tag, model, size, dbg, seconds,
                killsA, killsB, killsA + killsB, deathsA, deathsB,
                shots, hits, hr, cs.FriendlyHits,
                reached.Count, botIds.Count, maxTargeted, maxVis);
            Console.WriteLine($"RESULT_P632 {tag} model={model} debug={dbg} size={size}/side window={seconds:F0}s "
                + $"botsReached={o.BotsReached}/{o.BotsTotal} killsA={killsA} killsB={killsB} killsTotal={o.KillsTotal} "
                + $"deathsA={deathsA} deathsB={deathsB} shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} "
                + $"shieldHits={cs.EnemyShieldHits} hullHits={cs.EnemyHullHits} "
                + $"maxTargeted={maxTargeted} maxVisibleEnemies={maxVis}");

            var sb = new StringBuilder();
            sb.AppendLine($"--- P632 {tag} model={model} debug={dbg} ---");
            sb.AppendLine($"RESULT botsReached={o.BotsReached}/{o.BotsTotal} killsA={killsA} killsB={killsB} killsTotal={o.KillsTotal} deathsA={deathsA} deathsB={deathsB} shots={shots} hits={hits} hitRate={hr:P1} friendly={cs.FriendlyHits} shieldHits={cs.EnemyShieldHits} hullHits={cs.EnemyHullHits} maxTargeted={maxTargeted} maxVisibleEnemies={maxVis}");
            foreach (var v in mgr.ActiveBots())
                sb.AppendLine($"ACTIVE side={v.Side} sector={v.Sector} target={v.Target} hull={v.Hull}");
            sb.AppendLine("--- P636 DECISION PERF (snapshot fix) ---");
            foreach (var pl in perfLines) sb.AppendLine("  " + pl);
            sb.AppendLine("--- SERVER LOG (kills/warp/collision tail) ---");
            foreach (var ln in log.Split('\n'))
                if (KillRx.IsMatch(ln) || ln.Contains("[warp] ship ", StringComparison.Ordinal) || ln.Contains("[collision] ship ", StringComparison.Ordinal))
                    sb.AppendLine("  " + ln.Trim());
            var dump = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p632_{tag}.txt");
            File.WriteAllText(dump, sb.ToString());
            Console.WriteLine($"P632 {tag} wrote {dump}");
            return o;
        }
        finally
        {
            Console.SetOut(saved);
            if (oldModel is null) Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", null);
            else Environment.SetEnvironmentVariable("ALLEGIANCE_SHIP_UPDATE_MODEL", oldModel);
        }
    }

    /// <summary>Repro driver: measure the current broadcast model's outcome.</summary>
    [Fact]
    public void Record_port_mode_fair_fight()
    {
        if (IsGated()) return;
        Run(Size(), Seconds(), Model(), Debug(), Tag());
    }

    /// <summary>The forward mandatory live check, as a gated assertion: in a
    /// 30v30 fair face-off the bots must reach the target AND kill. Gated off
    /// by default so the pure-measurement probe above never aborts; enable with
    /// ALLEGIANCE_LIVE_P632_ASSERT=1 in the regression harness.</summary>
    [Fact]
    public void Assert_port_bots_kill_in_window()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P632_ASSERT") != "1")
        {
            Console.WriteLine("[P632] kill assertion SKIPPED — set ALLEGIANCE_LIVE_P632_ASSERT=1");
            return;
        }
        var o = Run(Size(), Seconds(), Model(), Debug(), Tag());
        Assert.True(o.KillsTotal > 0,
            $"P632 mandatory check failed: {o.Size}v{o.Size} {o.Model} bots reached {o.BotsReached}/{o.BotsTotal} but killed 0 in {o.Window:F0}s");
    }
}
