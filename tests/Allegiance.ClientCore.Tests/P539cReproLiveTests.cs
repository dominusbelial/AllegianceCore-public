using System.Text;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539c — per-side reproduction of the ONE-SIDED acquisition / park-at-the-aleph
/// behaviours the showcase bar exposed (P539b §6b). Drives a real
/// <see cref="BotManager"/> (real TCP, in-process m2xspiderw debug server, 20 Hz)
/// with a real combat spawn on BOTH sides and, every ~2 s, snapshots each bot's
/// OWN <see cref="ClientWorld"/> (via <see cref="BotManager.DiagnoseWorlds"/>)
/// alongside the server's authoritative view of the same ships/warps. The dump is
/// compared per side to find WHERE one side's bots diverge (wrong cross-side ship
/// SectorId on acquisition, or wrong/stale warp record on transit).
///
/// Gated behind ALLEGIANCE_LIVE_P539C_REPRO=1; writes a per-tick trace to
/// $TMPDIR/p539c_repro_{tag}.txt plus a parsed summary to stdout.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P539cReproLiveTests
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
            ShowMap = true, Name = "P539c Repro", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p539c-ticker" };
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

    private static bool IsGated()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P539C_REPRO") == "1") return false;
        Console.WriteLine("[P539c] repro SKIPPED — set ALLEGIANCE_LIVE_P539C_REPRO=1 to run");
        return true;
    }

    private static short Rendezvous() =>
        (short)(int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539C_SECTOR"), out var s) && s > 0 ? s : 1052);

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                foreach (var model in sec.Models)
                    if (model is Ship s && s.Id == id)
                        return s;
        return null;
    }

    private static Sector? ServerSectorOf(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                foreach (var model in sec.Models)
                    if (model is Ship s && s.Id == id)
                        return sec;
        return null;
    }

    /// <summary>Authoritative server warp record (cluster/pos/radius/dest) for a
    /// warp that lives in <paramref name="sector"/> and whose partner is the
    /// <paramref name="destCluster"/> — or null.</summary>
    private static (short Id, short Cluster, double[] Pos, short Radius, short Dest)? ServerWarpLeadingTo(Srv server, Sector sector, short destCluster)
    {
        foreach (var rw in sector.Warps)
        {
            if (rw.Destination is { } d && d.Id == destCluster)
            {
                var p = rw.Warp.Position is { Length: 3 } fp ? new double[] { fp[0], fp[1], fp[2] } : new double[] { 0, 0, 0 };
                return (rw.Warp.WarpId, rw.Warp.ClusterId, p, rw.Warp.Radius, rw.Warp.DestinationId);
            }
        }
        return null;
    }

    /// <summary>The bot-world target warp: a sector warp whose partner-warp's
    /// cluster equals <paramref name="destCluster"/>.</summary>
    private static (short Id, short Cluster, double[] Pos, short Radius, short Dest)? WorldWarpLeadingTo(BotManager.WarpDiag? sel, IEnumerable<BotManager.WarpDiag> all, short destCluster)
    {
        var w = all.FirstOrDefault(x => x.Dest != 0 && all.Any(p => p.Id == x.Dest && p.Cluster == destCluster));
        if (w is null) return null;
        return (w.Id, w.Cluster, w.Pos, w.Radius, w.Dest);
    }

    private static string V(double[] p) => $"({p[0]:F0},{p[1]:F0},{p[2]:F0})";

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Repro_side_dependent_acquisition_and_transit()
    {
        Require.Artwork();
        if (IsGated()) return;
        int size = int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539C_SIZE"), out var sz) ? sz : 2;
        double window = double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P539C_SECONDS"), out var w) && w > 0 ? w : 120;

        var tee = new TeeWriter(Console.Out);
        var saved = Console.Out;
        string probeTag = Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX") ?? "p0";
        string dumpPath = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p539c_repro_size{size}_probe{probeTag}.txt");
        var sb = new StringBuilder();
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(MapConfig(20));
            Console.SetOut(tee);
            short rendezvous = Rendezvous();
            sb.AppendLine($"P539C-REPRO booted m2xspiderw :{server.BoundPort} size={size} rendezvous={rendezvous} window={window:F0}s");
            using var ticker = new ServerTicker(server);
            using var mgr = new BotManager("127.0.0.1", server.BoundPort);
            if (!mgr.ServerReady) throw new InvalidOperationException("probe must validate");

            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            var hull = Catalog.DefaultFighterHull();
            var partsA = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
            var partsB = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;

            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();
            for (int i = 0; i < size; i++)
            {
                var r = mgr.SpawnBot(hull.Id, hull.Name, a, partsA, "combat", rendezvous);
                botIds.Add(r.ShipId); sideById[r.ShipId] = a;
                var r2 = mgr.SpawnBot(hull.Id, hull.Name, b, partsB, "combat", rendezvous);
                botIds.Add(r2.ShipId); sideById[r2.ShipId] = b;
            }
            sb.AppendLine($"P539C-REPRO spawned {botIds.Count} bots (A:{size} side{a} B:{size} side{b})");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(window);
            long sampleIdx = 0;
            var hits = new Dictionary<short, int>();  // per-ship: how often sampled in the rendezvous
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
                sampleIdx++;
                foreach (var d in mgr.DiagnoseWorlds())
                {
                    var srvShip = ServerShip(server, d.ShipId);
                    var srvSec = ServerSectorOf(server, d.ShipId);
                    short srvSector = srvSec?.Id ?? -1;
                    string srvState = srvShip is null ? "absent"
                        : $"vel({srvShip.Velocity.X:F0},{srvShip.Velocity.Y:F0},{srvShip.Velocity.Z:F0}) dead={srvShip.Dead} stn={srvShip.StationId?.ToString() ?? "-"}";
                    // The bot's intended warp toward the rendezvous (single-hop here):
                    var worldWarp = WorldWarpLeadingTo(null, d.AllWarps, rendezvous);
                    var srvWarp = srvSec is { } sc ? ServerWarpLeadingTo(server, sc, rendezvous) : null;
                    sb.AppendLine($"S{d.SampleIdxTick(sampleIdx)} name={d.Name} side={d.Side} ship={d.ShipId} " +
                                  $"worldSector={d.Sector} serverSector={srvSector} srv={srvState} pos={V(d.Pos)} ownTick={d.OwnLastTick} target={d.Target}");
                    sb.AppendLine($"   worldWarpToRdv={(worldWarp is { } ww ? $"id{ww.Id} cl{ww.Cluster} pos{V(ww.Pos)} r{ww.Radius} dest{ww.Dest}" : "none")} " +
                                  $"serverWarpToRdv={(srvWarp is { } sw ? $"id{sw.Id} cl{sw.Cluster} pos{V(sw.Pos)} r{sw.Radius} dest{sw.Dest}" : "none")}");
                    foreach (var cs in d.CrossSideShips)
                        sb.AppendLine($"   xs ship={cs.ShipId} sector={cs.Sector} pos={V(cs.Pos)} lastTick={cs.LastTick} visible={cs.Visible}");
                    if (d.Sector == rendezvous && d.Sector == srvSector) hits[d.ShipId] = (hits.TryGetValue(d.ShipId, out var h) ? h : 0) + 1;
                }
                if (sampleIdx % 10 == 0)
                {
                    var it = mgr.GetCombatSummary();
                    sb.AppendLine($"INTERIM t={(DateTime.UtcNow - t0).TotalSeconds:F0}s shots={it.ShotsFired} hits={it.ShipHits} kills={it.Kills}");
                }
            }
            sb.AppendLine($"ARRIVED(at rendezvous) " + string.Join(" ", botIds.OrderBy(x => x).Select(id => $"{id}={sideById[id]}:[{(hits.TryGetValue(id, out var h) ? h : 0)}/{window / 2:F0}]")));

            var csum = mgr.GetCombatSummary();
            foreach (var r in csum.Bots)
                sb.AppendLine($"COMBAT {r.Name} side={r.Side} shots={r.ShotsFired} hits={r.ShipHits} shieldHits={r.EnemyShieldHits} hullHits={r.EnemyHullHits} friendly={r.FriendlyHits} kills={r.Kills}");
            sb.AppendLine($"COMBAT-TOTAL shotsA={csum.Bots.Where(x => x.Side == a).Sum(x => x.ShotsFired)} hitsA={csum.Bots.Where(x => x.Side == a).Sum(x => x.ShipHits)} " +
                          $"killsA={csum.Bots.Where(x => x.Side == a).Sum(x => x.Kills)} " +
                          $"shotsB={csum.Bots.Where(x => x.Side == b).Sum(x => x.ShotsFired)} hitsB={csum.Bots.Where(x => x.Side == b).Sum(x => x.ShipHits)} " +
                          $"killsB={csum.Bots.Where(x => x.Side == b).Sum(x => x.Kills)} " +
                          $"probeSide={mgr.ProbeSide}");

            string log = tee.Captured;
            // Transit evidence from the server log.
            var transit = new Regex(@"\[(?:warp|transit|setcluster|cluster)\][^\n]*", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>();
            foreach (Match m in transit.Matches(log))
            {
                var t = m.Value.Trim();
                bool mentionsBot = botIds.Any(id => t.Contains($" {id} ") || t.EndsWith($" {id}") || t.Contains($"ship {id}"));
                if (mentionsBot && seen.Add(t)) sb.AppendLine($"LOG {t}");
            }
            // p433-diag lines (route health per bot).
            foreach (var line in log.Split('\n'))
                if (line.Contains("[p433-diag]", StringComparison.Ordinal))
                    sb.AppendLine($"DIAG {line.Trim()}");
            // combat / collision / cross-side export / per-peer target promotion
            foreach (var line in log.Split('\n'))
            {
                var t = line.Trim();
                if (!t.Contains('[')) continue;
                bool bot = botIds.Any(id => t.Contains($"{id}"));
                if (!bot) continue;
                if (t.Contains("[combat]", StringComparison.Ordinal) || t.Contains("[collision]", StringComparison.Ordinal)
                    || t.Contains("[xexport]", StringComparison.Ordinal) || t.Contains("[eject]", StringComparison.Ordinal)
                    || t.Contains("[ships] peer", StringComparison.Ordinal))
                    sb.AppendLine($"LOG2 {t}");
            }

            File.WriteAllText(dumpPath, sb.ToString() + "\n\n--- SERVER LOG (tail) ---\n" + DiagExt.Tail(log, 60000));
            Console.WriteLine($"P539C-REPRO wrote {dumpPath}");
        }
        finally
        {
            Console.SetOut(saved);
        }
    }
}

internal static class DiagExt
{
    public static long SampleIdxTick(this BotManager.BotWorldDiag d, long idx) => idx;
    public static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];
}
