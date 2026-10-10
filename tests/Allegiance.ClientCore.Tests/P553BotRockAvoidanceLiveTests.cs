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
/// P553 — why MCP combat bots still die on asteroids (and each other) despite
/// the shared native dodge law being wired into <c>CombatFlyTo</c>. Boots the
/// real 221 configuration (in-process NON-debug <c>m2xspiderw</c> server, 20 Hz,
/// civIds [25,3], testHarness testUnlock) and drives a staged 5v5 into the
/// rendezvous Alderan (1052) — the 134-asteroid field — for `seconds`, then:
///   • classifies EVERY collision death by the other body's type (ship /
///     asteroid / station + id) from the enriched server `[collision]` lines,
///   • records the victim's server-side speed and per-bot state (transit vs
///     engaged) at death,
///   • correlates each collision death with whether the bot's own shared
///     dodge law FIRED in the seconds before it (the P553 dodge-fire trace on
///     BotManager).
/// Pure measurement (records + dumps; no assertion here — callers assert).
/// Gated behind ALLEGIANCE_LIVE_P553=1; traces written to $TMPDIR/p553_*.txt.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P553BotRockAvoidanceLiveTests
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
            DebugMode = false,           // the REAL map (Ganymede/Organia/Alderan), not the debug Omega/Plato
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { Name = "P553 (221 cfg)", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] },
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "p553-ticker" };
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
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P553") == "1") return false;
        Console.WriteLine("[P553] live proofs SKIPPED — set ALLEGIANCE_LIVE_P553=1 to run");
        return true;
    }
    private static int Size() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P553_SIZE"), out var s) ? s : 5;
    private static double Seconds() => double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P553_SECONDS"), out var s) && s > 0 ? s : 240;
    private static int TechA() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P553_TECH_A"), out var t) ? t : 0;
    private static int TechB() => int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P553_TECH_B"), out var t) ? t : 0;
    private static string Tag() => Environment.GetEnvironmentVariable("ALLEGIANCE_P553_TAG") ?? "alderan";

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                foreach (var model in sec.Models)
                    if (model is Ship s && s.Id == id) return s;
        return null;
    }

    private static (Srv server, ServerTicker ticker, BotManager mgr, StampTee tee, TextWriter saved, DateTime bootUtc)
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
        return (server, ticker, mgr, tee, saved, DateTime.UtcNow);
    }

    private static readonly object _reloc = new();
    private static int _runSeq;

    /// <summary>P553 — the pure collision-death classifier shared by the live
    /// harness and its unit tests. A death is a GUNFIRE kill when the victim has
    /// a credited ship killer; otherwise it is a COLLISION/other death, whose
    /// BODY is named by the collision event involving the victim (as EITHER
    /// party, within ±0.3 s of the eject) — "ship &lt;id&gt;", "asteroid &lt;id&gt;",
    /// "station &lt;id&gt;" or "rock/base" (unmatched ⇒ ""). Never throws; the
    /// classifier is what lets a live proof tell a rock death from a ship-ram.</summary>
    internal static string ClassifyDeath(
        short victim, double t, IReadOnlySet<short> gunfire,
        IReadOnlyList<(double T, short A, short B, string Other)> collisions)
    {
        if (gunfire.Contains(victim)) return "";
        var bodies = new List<string>();
        foreach (var c in collisions.Where(c => (c.A == victim || c.B == victim) && Math.Abs(c.T - t) <= 0.3))
        {
            // Ship-ship: the OTHER ship is the body (per-party identity — a
            // "ship B" label on the line is the other of party A, not of party B).
            if (c.A != c.B)
                bodies.Add(c.A == victim ? $"ship {c.B}" : $"ship {c.A}");
            else
                bodies.Add(c.Other);    // rock / station (partyB == partyA)
        }
        return bodies.Distinct().Count() > 0 ? string.Join("|", bodies.Distinct()) : "";
    }

    /// <summary>⭐ COLLISION-DEATH CLASSIFIER tests (pure, always run): the
    /// measurement's classification of a bot death into gunfire-vs-collision and
    /// — for a collision — the other body's type (ship / asteroid / station)
    /// must be correct, because the P553 live tables and the before/after bar
    /// are built from it.</summary>
    public sealed class P553ClassifierTests
    {
        private static IReadOnlyList<(double T, short A, short B, string Other)> Cols(params (double T, short A, short B, string Other)[] c) => c;
        private static IReadOnlySet<short> GF(params short[] v) => new HashSet<short>(v);

        [Fact]
        public void Gunfire_kill_is_not_a_collision() =>
            Assert.Equal("", P553BotRockAvoidanceLiveTests.ClassifyDeath(1, 10.0, GF(1), Cols()));

        [Fact]
        public void Collision_with_asteroid_names_the_rock() =>
            Assert.Equal("asteroid 555",
                P553BotRockAvoidanceLiveTests.ClassifyDeath(1, 10.0, GF(),
                    Cols((10.03, 1, 1, "asteroid 555"))));

        [Fact]
        public void Collision_with_station_names_the_base() =>
            Assert.Equal("station 432",
                P553BotRockAvoidanceLiveTests.ClassifyDeath(1, 10.0, GF(),
                    Cols((9.95, 1, 1, "station 432"))));

        [Fact]
        public void Ship_ship_ram_names_the_other_ship_for_either_party()
        {
            // Party1 died: its collision line names the other ship.
            Assert.Equal("ship 8123",
                P553BotRockAvoidanceLiveTests.ClassifyDeath(8101, 10.0, GF(),
                    Cols((10.01, 8101, 8123, "ship 8123"))));
            // Party2 died: the same line (victim as party B) is found too.
            Assert.Equal("ship 8101",
                P553BotRockAvoidanceLiveTests.ClassifyDeath(8123, 10.0, GF(),
                    Cols((10.01, 8101, 8123, "ship 8123"))));
        }

        [Fact]
        public void Old_unrelated_collision_is_not_attributed_to_the_death() =>
            Assert.Equal("",
                P553BotRockAvoidanceLiveTests.ClassifyDeath(1, 10.0, GF(),
                    Cols((20.0, 1, 2, "ship 2"))));   // 10 s later — outside ±0.3 s
    }

    /// <summary>Run ONE staged 5v5 into Alderan (221 config) and measure collision
    /// deaths (by body type) + dodge-fire correlation. Records; no assertion.</summary>
    private static (string Summary, string Path) RunStagedAlderan(int size, int techA, int techB, double seconds, string tag)
    {
        int seq = Interlocked.Increment(ref _runSeq);
        var sb = new StringBuilder();
        var dumpPath = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", $"p553_{tag}_seq{seq}.txt");

        var (server, ticker, mgr, tee, saved, bootUtc) = Boot(out var port);
        try
        {
            sb.AppendLine($"P553 {tag} booted 221cfg(m2xspiderw NON-debug) :{port} size={size} techA={techA} techB={techB} window={seconds:F0}s staging");
            var sides = mgr.ValidSides();
            short a = sides[0], b = sides[1];
            var hull = Catalog.DefaultFighterHull();
            var partsA = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techA).Parts;
            var partsB = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techB).Parts;
            sb.AppendLine($"LOADOUT sideA(tech{techA}): {string.Join(", ", partsA.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()))}");
            sb.AppendLine($"LOADOUT sideB(tech{techB}): {string.Join(", ", partsB.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()))}");

            const short alderan = 1052;
            var clusters = mgr.ProbeClusters();
            foreach (var kv in clusters)
                sb.AppendLine($"SECTOR {kv.Key} '{kv.Value.Name}'");

            var botIds = new HashSet<short>();
            var sideById = new Dictionary<short, short>();
            var nameByShip = new Dictionary<short, string>();
            for (int i = 0; i < size; i++)
            {
                var ra = mgr.SpawnBot(hull.Id, hull.Name, a, partsA, "combat", alderan, null, null, useStaging: true);
                botIds.Add(ra.ShipId); sideById[ra.ShipId] = a; nameByShip[ra.ShipId] = ra.Name;
                var rb = mgr.SpawnBot(hull.Id, hull.Name, b, partsB, "combat", alderan, null, null, useStaging: true);
                botIds.Add(rb.ShipId); sideById[rb.ShipId] = b; nameByShip[rb.ShipId] = rb.Name;
            }
            sb.AppendLine($"SPAWNED {botIds.Count} bots (A:{size} B:{size}) mode=staging target=Alderan 1052");

            var t0 = DateTime.UtcNow;
            var deadline = t0.AddSeconds(seconds);
            // Per-bot speed/pos/sector sampler (~every 500ms, from the server-side ship).
            var speedSamples = new Dictionary<short, List<(double Utc, float Speed, short Sector)>>();
            int poll = 0;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                poll++;
                foreach (var id in botIds)
                {
                    var s = ServerShip(server, id);
                    if (s is null) continue;
                    float spd = s.Velocity.Length();
                    short sec = s.Sector is { } scc ? scc.Id : (short)-1;
                    (speedSamples.TryGetValue(id, out var lst) ? lst : speedSamples[id] = new())
                        .Add(((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds, spd, sec));
                }
            }

            // ── Collision-death classification from the stamped server log ──
            string log = tee.Captured;
            // A gunfire kill's credited-killer line can arrive AFTER the eject
            // (P107 ordering), so a victim is gunfire iff it EVER gets a credited
            // killer; otherwise the eject is a collision/other death.
            var gunfireVictims = new HashSet<short>();
            var victimsSide = new Dictionary<short, short>();
            foreach (var id in botIds) victimsSide[id] = sideById[id];
            var ejects = new List<(double T, short Victim)>();       // death events (utc-seconds)
            var gunfire = new HashSet<short>();
            foreach (var line in log.Split('\n'))
            {
                var tm = Regex.Match(line, @"^\[t=([\d.]+)\]");
                double t = tm.Success ? double.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
                var mEj = Regex.Match(line, @"\[eject\] player ship (\d+) ejected");
                if (mEj.Success && victimsSide.ContainsKey(short.Parse(mEj.Groups[1].Value)))
                    ejects.Add((bootUtc.Subtract(DateTime.UnixEpoch).TotalSeconds + t, short.Parse(mEj.Groups[1].Value)));
                var mK = Regex.Match(line, @"\[combat\] ship (\d+) killed by ship (-?\d+)");
                if (mK.Success && victimsSide.ContainsKey(short.Parse(mK.Groups[1].Value))
                    && int.Parse(mK.Groups[2].Value) >= 0)
                    gunfire.Add(short.Parse(mK.Groups[1].Value));
            }

            // Collect collisions normalized to (time, partyA, partyB, otherBody):
            // for a ship-ship line partyA/partyB are the two ships and otherBody
            // names the OTHER ship; for a rock/station line partyB == partyA (so
            // only partyA can be a death victim) and otherBody is the body desc.
            var collisions = new List<(double T, short A, short B, string Other)>();
            foreach (var line in log.Split('\n'))
            {
                var tm = Regex.Match(line, @"^\[t=([\d.]+)\]");
                double t = tm.Success ? double.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
                var mc = Regex.Match(line, @"\[collision\] ship (\d+) x (ship (\d+)|asteroid (\d+)|station (\d+)|rock/base) sector (\d+)");
                if (!mc.Success) continue;
                short aShip = short.Parse(mc.Groups[1].Value);
                if (!victimsSide.ContainsKey(aShip)) continue;
                string other = mc.Groups[2].Value;
                if (mc.Groups[3].Success)      // ship-ship: partyB is the other ship
                    collisions.Add((bootUtc.Subtract(DateTime.UnixEpoch).TotalSeconds + t, aShip, short.Parse(mc.Groups[3].Value), other));
                else                            // rock/station: partyB == partyA
                    collisions.Add((bootUtc.Subtract(DateTime.UnixEpoch).TotalSeconds + t, aShip, aShip, other));
            }

            // Dodge-fire trace (bot id → sorted utc list).
            var dodgeByBot = new Dictionary<short, List<double>>();
            foreach (var df in mgr.DodgeTrace())
                (dodgeByBot.TryGetValue(df.ShipId, out var dl) ? dl : dodgeByBot[df.ShipId] = new()).Add(df.WallUtc);
            foreach (var k in dodgeByBot.Keys) dodgeByBot[k].Sort();

            // Classify each eject: gunfire vs collision (match a same-ship collision
            // within ±0.3s of the eject for the body type).
            var perSide = new Dictionary<short, Dictionary<string, int>>();
            int aSide = a, bSide = b;
            sb.AppendLine("\nPER-DEATH bot, side, time, cause, body, sector, speedNearDeath(m/s), dodgedRecent");
            foreach (var (t, victim) in ejects.OrderBy(e => e.T))
            {
                short side = victimsSide[victim];
                bool gf = gunfire.Contains(victim);
                var bucket = perSide.TryGetValue(side, out var d) ? d : perSide[side] = new();
                string cause = gf ? "gunfire" : "collision";
                bucket["all"] = bucket.GetValueOrDefault("all") + 1;
                if (gf) { bucket["gunfire"] = bucket.GetValueOrDefault("gunfire") + 1; }
                else bucket["collision"] = bucket.GetValueOrDefault("collision") + 1;

                string body = "-", sector = "-"; float speed = float.NaN; int dodgeN = 0; bool dodged = false;
                if (!gf)
                {
                    body = ClassifyDeath(victim, t, gunfire, collisions);
                    // sector + speed near death: nearest sample ≤ death time.
                    if (speedSamples.TryGetValue(victim, out var ss))
                    {
                        var last = ss.LastOrDefault(x => x.Utc <= t);
                        if (last.Utc != 0 || ss.Count > 0)
                        {
                            var prev = ss.Where(x => x.Utc <= t).DefaultIfEmpty(default).Last();
                            speed = prev.Utc != 0 ? prev.Speed : float.NaN;
                            sector = prev.Sector >= 0 ? prev.Sector.ToString() : "-";
                        }
                    }
                    // dodge correlation: fires in the 2 s before death.
                    if (dodgeByBot.TryGetValue(victim, out var dl))
                    {
                        dodgeN = dl.Count(u => u >= t - 2.0 && u <= t);
                        dodged = dodgeN > 0;
                    }
                    bucket[$"coll-{body.Split('|')[0]}"] = bucket.GetValueOrDefault($"coll-{body.Split('|')[0]}") + 1;
                }
                sb.AppendLine($"  DEATH bot={victim} name={nameByShip.GetValueOrDefault(victim)} side={side} t={t - bootUtc.Subtract(DateTime.UnixEpoch).TotalSeconds:F1}s cause={cause} body={body} sector={sector} speed={(float.IsNaN(speed) ? "-" : speed.ToString("F0"))} dodgeFires2s={dodgeN} dodged={dodged}");
            }

            // ── aggregate ──
            var csum = mgr.GetCombatSummary();
            long shotsA = csum.Bots.Where(x => x.Side == a).Sum(x => x.ShotsFired);
            long shotsB = csum.Bots.Where(x => x.Side == b).Sum(x => x.ShotsFired);
            long killsA = csum.Bots.Where(x => x.Side == a).Sum(x => x.Kills);
            long killsB = csum.Bots.Where(x => x.Side == b).Sum(x => x.Kills);
            int gfa = perSide.GetValueOrDefault(a)?.GetValueOrDefault("gunfire") ?? 0;
            int gfb = perSide.GetValueOrDefault(b)?.GetValueOrDefault("gunfire") ?? 0;
            int cola = perSide.GetValueOrDefault(a)?.GetValueOrDefault("collision") ?? 0;
            int colb = perSide.GetValueOrDefault(b)?.GetValueOrDefault("collision") ?? 0;
            int rockA = 0, rockB = 0, shipA = 0, shipB = 0, statA = 0, statB = 0;
            foreach (var (side, d) in perSide)
            {
                foreach (var (k, v) in d)
                {
                    if (side == a && k.StartsWith("coll-")) { if (k.Contains("asteroid")) rockA += v; else if (k.Contains("ship")) shipA += v; else if (k.Contains("station")) statA += v; }
                    else if (side == b && k.StartsWith("coll-")) { if (k.Contains("asteroid")) rockB += v; else if (k.Contains("ship")) shipB += v; else if (k.Contains("station")) statB += v; }
                }
            }
            long totalDodge = mgr.DodgeTrace().Count;
            sb.AppendLine($"\nRESULT techA={techA} techB={techB} shotsA={shotsA} shotsB={shotsB} killsA={killsA} killsB={killsB}");
            sb.AppendLine($"COLLISION_DEATHS rockA={rockA} rockB={rockB} shipA={shipA} shipB={shipB} stationA={statA} stationB={statB} totalA={cola} totalB={colb}");
            sb.AppendLine($"GUNFIRE_DEATHS gunfireA={gfa} gunfireB={gfb}");
            sb.AppendLine($"DODGE dodgeFiresTotal={totalDodge}");

            // Raw collision lines for inspection.
            sb.AppendLine("\nRAW_COLLISION");
            foreach (var line in log.Split('\n'))
                if (line.Contains("[collision]")) sb.AppendLine("  " + line.Trim());
            // Raw eject / combat (death audit) lines for the spawned bots.
            sb.AppendLine("\nRAW_DEATHS");
            foreach (var line in log.Split('\n'))
            {
                var t = line.Trim();
                bool botLine = botIds.Any(bid => Regex.IsMatch(t, $@"ship {bid}( |\b)|player ship {bid} "));
                if (botLine && (t.Contains("[eject]") || t.Contains("[combat]")))
                    sb.AppendLine("  " + t);
            }

            File.WriteAllText(dumpPath, sb.ToString());
            Console.WriteLine($"P553 wrote {dumpPath}");
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

    /// <summary>Topology/field probe: dump the 221 map sectors + asteroid density
    /// (the premise: Alderan 1052 has 134 asteroids).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_221_field()
    {
        Require.Artwork();
        if (IsGated()) return;
        var sb = new StringBuilder();
        var (server, ticker, mgr, tee, saved, _) = Boot(out var port);
        sb.AppendLine($"P553 FIELD 221cfg m2xspiderw :{port}");
        foreach (var m in server.Missions)
            foreach (var sec in m.Sectors)
                sb.AppendLine($"  DENSITY sector {sec.Id} '{sec.Name}' asteroids={sec.Mineable.Count} stations={sec.StationRuntimes.Count}");
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", "p553_field.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.SetOut(saved);
        try { mgr.Dispose(); } catch { }
        try { ticker.Dispose(); } catch { }
        try { server.Dispose(); } catch { }
    }

    /// <summary>Primary measurement entry: one staged 5v5 into Alderan (env-driven
    /// size/tech/tag). RECORDS (no assert).</summary>
    [Fact]
    public void Record_staged_alderan()
    {
        if (IsGated()) return;
        var (summary, _) = RunStagedAlderan(Size(), TechA(), TechB(), Seconds(), Tag());
        foreach (var line in summary.Split('\n'))
            if (line.StartsWith("RESULT ") || line.StartsWith("COLLISION_DEATHS ") || line.StartsWith("GUNFIRE_DEATHS ") || line.StartsWith("DODGE "))
                Console.WriteLine(line);
    }
}
