using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.Core;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539 — the SERVER-LOG baseline harness, used for a clean BEFORE/AFTER
/// comparison on the SAME in-process map and the SAME driver. It measures
/// exclusively from the captured SERVER stdout — the markers every build
/// produces identically ([projectile] shots, [projectile-hit], [collision],
/// [eject], [lifepod] docked) — so it compiles and runs against the PRE-fix
/// combat code unchanged. The BEFORE run drives the old default loadout
/// (DefaultParts: dispersion-0 gun + cloak); the AFTER run drives the new
/// dogfight loadout (ALLEGIANCE_P539_DOGFIGHT=1 selects DogfightParts). All
/// six counts are over the spawned bot ship ids only.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P539LogBaselineLiveTests
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
            DebugMode = true,
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { // P797 harness option: the harness bots cannot scout yet (P798).
            Name = "P539 Log Baseline", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private sealed class ServerTicker : IDisposable
    {
        private readonly Srv _server;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        public ServerTicker(Srv server) { _server = server; _thread = new Thread(Loop) { IsBackground = true }; _thread.Start(); }
        private void Loop() { while (!_cts.IsCancellationRequested) { try { _server.TickSim(); } catch { } Thread.Sleep(4); } }
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

    private static readonly Regex Proj = new(@"\[projectile\] ship (?<id>-?\d+) fired", RegexOptions.Compiled);
    private static readonly Regex Hit = new(@"\[projectile-hit\] ship (?<id>-?\d+) hit target (?<t>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex Col = new(@"\[collision\] ship (?<a>-?\d+) x ship (?<b>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex Eject = new(@"\[eject\] player ship (?<id>-?\d+) ejected", RegexOptions.Compiled);
    private static readonly Regex PodDock = new(@"\[lifepod\] player ship (?<id>-?\d+) -> docked Lifepod", RegexOptions.Compiled);

    private static void Run(int ups, int size, double windowSeconds, string label)
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P539_BASELINE") != "1")
        {
            Console.WriteLine($"[P539] log baseline SKIPPED — set ALLEGIANCE_LIVE_P539_BASELINE=1 (label={label})");
            return;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P539_SECONDS"), out var ov) && ov > 0)
            windowSeconds = ov;

        var tee = new TeeWriter(Console.Out);
        var saved = Console.Out;
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(MapConfig(ups));
            Console.SetOut(tee);
            Console.WriteLine($"P539-BASELINE {label}: booted m2xspiderw :{server.BoundPort} ups={ups} size={size} window={windowSeconds:F0}s");
            using var ticker = new ServerTicker(server);
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            Assert.True(mgr.ServerReady, "BotManager probe must validate");

            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            var hull = Catalog.DefaultFighterHull();
            // This baseline driver uses ONLY the pre-existing default loadout
            // (dispersion-0 gun + cloak), so it compiles against the PRE-fix
            // combat code unchanged — the true BEFORE driver. (The AFTER driver
            // is P539CombatProofLiveTests with the dogfight loadout.)
            var parts = Catalog.DefaultParts();

            var botIds = new HashSet<short>();
            for (int i = 0; i < size; i++) botIds.Add(mgr.SpawnBot(hull.Id, hull.Name, a, parts, "combat").ShipId);
            short shared = -1;
            for (int tries = 0; tries < 30 && shared < 0; tries++)
            {
                foreach (var v in mgr.ActiveBots()) if (v.Side == a && v.Sector >= 0) { shared = v.Sector; break; }
                if (shared < 0) Thread.Sleep(200);
            }
            for (int i = 0; i < size; i++) botIds.Add(mgr.SpawnBot(hull.Id, hull.Name, b, parts, "combat", shared).ShipId);
            Console.WriteLine($"P539-BASELINE {label}: {botIds.Count} bots rendezvous {shared} (default-loadout)");

            Thread.Sleep((int)(windowSeconds * 1000));

            string log = tee.Captured;
            int shots = Count(Proj, log, botIds, m => m.Groups["id"].Value);
            int hits = Count(Hit, log, botIds, m => m.Groups["id"].Value);
            int collisions = CountPair(Col, log, botIds);
            int ejects = Count(Eject, log, botIds, m => m.Groups["id"].Value);
            int docked = Count(PodDock, log, botIds, m => m.Groups["id"].Value);
            double hitRate = shots > 0 ? (double)hits / shots : 0.0;

            string outcome = $"P539-LOG-{label} ups={ups}: shots={shots} hits={hits} hitRate={hitRate:P1} collisions={collisions} ejects={ejects} podsDocked={docked}";
            Console.WriteLine(outcome);
            var scratch = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p539_{label}_log_{ups}hz.txt");
            File.WriteAllText(scratch, outcome + "\n");
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static int Count(Regex rx, string log, ISet<short> botIds, Func<Match, string> idSel)
    {
        int n = 0;
        foreach (Match m in rx.Matches(log))
            if (short.TryParse(idSel(m), out var id) && botIds.Contains(id)) n++;
        return n;
    }

    private static int CountPair(Regex rx, string log, ISet<short> botIds)
    {
        int n = 0;
        foreach (Match m in rx.Matches(log))
            if (short.TryParse(m.Groups["a"].Value, out var a) && short.TryParse(m.Groups["b"].Value, out var b)
                && botIds.Contains(a) && botIds.Contains(b)) n++;
        return n;
    }

    [Fact] public void Baseline_20Hz() => Run(20, 5, 240, "BASELINE");
    [Fact] public void Baseline_10Hz() => Run(10, 5, 240, "BASELINE");
}
