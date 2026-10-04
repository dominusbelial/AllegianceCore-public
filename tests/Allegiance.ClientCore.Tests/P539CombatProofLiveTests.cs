using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539 — the LIVE headless dogfight proof: an in-process
/// <see cref="Srv"/> (m2xspiderw, both teams home in one shared sector so the
/// bots are co-located with no transit), a background <see cref="ServerTicker"/>,
/// and a REAL <see cref="BotManager"/> over real TCP sockets. It spawns
/// <c>size</c> P539 combat bots per side (the dogfight default weapon) and lets
/// them fight for a fixed sim window, then reports the REAL per-bot / per-side
/// shots, ship hits, hit rate and kills from the new stats, plus the bot-bot
/// collision count (grepped from the server's own <c>[collision]</c> log) and
/// the lifepod outcomes (pods that reached a station vs lingering as pods).
/// Runs once at the server's default 20 Hz and once at 10 Hz. Gated behind
/// ALLEGIANCE_LIVE_P539_PROOF=1 (these are multi-minute live drivers; the
/// routine suite keeps them a documented no-op).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P539CombatProofLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg MapConfig(int ups) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        LiveSettings = new Ls
        {
            DebugMode = true,   // reliable warp transit + auto-start (the 221 Alderan config)
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { Name = "P539 Dogfight Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p539-live-ticker" };
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

    private static void RunDogfight(int ups, int size, double windowSeconds)
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P539_PROOF") != "1")
        {
            Console.WriteLine($"[P539] dogfight proof SKIPPED in this configuration — set ALLEGIANCE_LIVE_P539_PROOF=1 to run (ups={ups})");
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
            Console.WriteLine($"P539-DOGFIGHT: booted m2xspiderw :{server.BoundPort} ups={ups} size={size} window={windowSeconds:F0}s");
            using var ticker = new ServerTicker(server);
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            Assert.True(mgr.ServerReady, "BotManager probe must validate against the in-process server");

            var sides = mgr.ValidSides();
            Assert.True(sides.Count >= 2, $"need two sides, got [{string.Join(",", sides)}]");
            short a = sides[0], b = sides[1];

            var hull = Catalog.DefaultFighterHull();
            var parts = Catalog.DogfightParts(hull.Id, hull.Name);
            // Optional lopsided sizing: concentrated fire can break the shield-regen
            // equilibrium and actually kill/eject bots, exercising the pod path.
            int sizeA = int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539_SIZE_A"), out var sa) && sa > 0 ? sa : size;
            int sizeB = int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539_SIZE_B"), out var sb) && sb > 0 ? sb : size;
            var botIds = new HashSet<short>();
            // Side A first, no rendezvous — its home sector is the shared target.
            for (int i = 0; i < sizeA; i++)
                botIds.Add(mgr.SpawnBot(hull.Id, hull.Name, a, parts, "combat").ShipId);

            // The sector side-A bots sit in (m2xspiderw homes side A there).
            short shared = -1;
            for (int tries = 0; tries < 30 && shared < 0; tries++)
            {
                foreach (var v in mgr.ActiveBots())
                    if (v.Side == a && v.Sector >= 0) { shared = v.Sector; break; }
                if (shared < 0) Thread.Sleep(200);
            }
            if (shared < 0) throw new InvalidOperationException("could not read side-A home sector");

            // Side B transits (real warps, debug map) to the same sector, so the
            // two teams are co-located and can dogfight.
            for (int i = 0; i < sizeB; i++)
                botIds.Add(mgr.SpawnBot(hull.Id, hull.Name, b, parts, "combat", shared).ShipId);

            Console.WriteLine($"P539-DOGFIGHT: spawned {botIds.Count} bots (side {a}x{sizeA} + side {b}x{sizeB}) rendezvous sector {shared} — fighting for {windowSeconds:F0}s");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(windowSeconds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                if ((DateTime.UtcNow - t0).TotalSeconds % 60 < 1.5)
                {
                    var it = mgr.GetCombatSummary();
                    Console.WriteLine($"P539-DOGFIGHT: interim t={(DateTime.UtcNow - t0).TotalSeconds:F0}s shots={it.ShotsFired} hits={it.ShipHits} kills={it.Kills}");
                }
            }

            var cs = mgr.GetCombatSummary();
            int collisions = CountBotCollisions(tee.Captured, botIds);
            int ejectsLog = CountId(EjectRx, tee.Captured, botIds);
            int dockedLog = CountId(PodDockRx, tee.Captured, botIds);
            Console.WriteLine($"P539-DOGFIGHT-OUTCOME ups={ups} size={size}: " +
                              $"shots={cs.ShotsFired} shipHits={cs.ShipHits} kills={cs.Kills} hitRate={cs.OverallHitRate:P1} " +
                              $"collisions={collisions} ejects={ejectsLog} podsDocked(log)={dockedLog} " +
                              $"podsCreated={cs.PodsCreated} podsDocked(mcp)={cs.PodsDocked} podsLingering={cs.PodsLingering}");
            foreach (var r in cs.Bots)
                Console.WriteLine($"P539-BOT {r.Name} side={r.Side} shots={r.ShotsFired} hits={r.ShipHits} kills={r.Kills} hitRate={r.HitRate:P1} isPod={r.IsPod} podDocked={r.PodDocked}");

            // Diagnostic dump (also to a scratch file so it survives test-runner buffering).
            var diag = new StringBuilder();
            diag.AppendLine($"--- P539 DIAG ups={ups} ---");
            diag.AppendLine($"OUTCOME shots={cs.ShotsFired} shipHits={cs.ShipHits} kills={cs.Kills} hitRate={cs.OverallHitRate:P1} collisions={collisions} ejects={ejectsLog} podsDocked={cs.PodsDocked} podsLingering={cs.PodsLingering}");
            foreach (var r in cs.Bots)
                diag.AppendLine($"BOT {r.Name} side={r.Side} shots={r.ShotsFired} hits={r.ShipHits} kills={r.Kills} hitRate={r.HitRate:P1} isPod={r.IsPod} podDocked={r.PodDocked}");
            foreach (var v in mgr.ActiveBots())
                diag.AppendLine($"ACTIVE name={v.Name} side={v.Side} sector={v.Sector} target={v.Target} hull={v.Hull} behavior={v.Behavior}");
            var scratch = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", "p539_diag.txt");
            File.WriteAllText(scratch, diag.ToString() + "\n\n--- SERVER LOG (tail) ---\n" + Truncate(tee.Captured, 40000));

            Assert.True(cs.ShotsFired > 0, $"the P539 dogfight bots must fire (lead-aimed) against the co-located enemy (diag: {scratch})");
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[^max..];

    private static int CountId(Regex rx, string log, ISet<short> botIds)
    {
        int n = 0;
        foreach (Match m in rx.Matches(log))
            if (short.TryParse(m.Groups["id"].Value, out var id) && botIds.Contains(id)) n++;
        return n;
    }

    private static readonly Regex EjectRx = new(@"\[eject\] player ship (?<id>-?\d+) ejected", RegexOptions.Compiled);
    private static readonly Regex PodDockRx = new(@"\[lifepod\] player ship (?<id>-?\d+) -> docked Lifepod", RegexOptions.Compiled);

    /// <summary>Count <c>[collision] ship X x ship Y</c> log lines where BOTH
    /// parties are spawned bot ship ids (a bot-bot collision), from the server's
    /// own captured stdout.</summary>
    private static int CountBotCollisions(string log, ISet<short> botIds)
    {
        int n = 0;
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("[collision] ship ", StringComparison.Ordinal) || !line.Contains(" x ship ", StringComparison.Ordinal)) continue;
            var parts = line.Split(['[', ']', ' ', 'x'], StringSplitOptions.RemoveEmptyEntries);
            // parts: "collision", "ship", <id1>, "ship", <id2>, "sector", ...
            if (parts.Length < 5) continue;
            if (short.TryParse(parts[2], out var p1) && short.TryParse(parts[4], out var p2)
                && botIds.Contains(p1) && botIds.Contains(p2))
                n++;
        }
        return n;
    }

    /// <summary>Default 20 Hz server cadence (the rate the 221 log showed bots
    /// firing ~50 shots each).</summary>
    [Fact]
    public void Dogfight_at_20Hz() => RunDogfight(ups: 20, size: 5, windowSeconds: 240);

    /// <summary>10 Hz server cadence (221 showed bots firing only ~8 shots each
    /// here — the fire loop must cope with either rate).</summary>
    [Fact]
    public void Dogfight_at_10Hz() => RunDogfight(ups: 10, size: 5, windowSeconds: 240);
}
