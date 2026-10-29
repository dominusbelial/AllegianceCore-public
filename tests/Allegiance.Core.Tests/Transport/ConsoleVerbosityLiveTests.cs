using System.Diagnostics;
using System.Text;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P318 (stability review finding H6) — live (real loopback sockets + real
/// in-process server) proof of both claims in the task:
/// <para>
/// (1) <b>Measured line-volume reduction</b>: with the verbosity gate at its
/// default (off) setting, the high-frequency per-tick/per-message diagnostic
/// lines ([ships], [ctrl], [shipstatus], [teamshipstatus], the per-tick missile
/// broadcasts, every refused inbound message, and the NPC stand-by lines) are
/// silent, so the server emits only the low-frequency event lines. Turning the
/// gate on (verbosity true) reproduces the pre-change unconditional behaviour
/// line-for-line, giving a real before/after lines-per-second measurement on the
/// same harness and bot count.
/// </para>
/// <para>
/// (2) <b>No freeze on a stalled stdout consumer</b>: the gated lines are routed
/// through <see cref="ConsoleLog"/>'s bounded non-blocking sink (a background
/// drain thread + a bounded queue that drops when full), so even with verbosity
/// ON (worst case: every per-tick line enabled) and stdout redirected to a writer
/// that sleeps on every write (simulating a slow/stalled pipe or journal
/// consumer), the sim thread never blocks — it keeps advancing the mission clock
/// and delivering per-tick updates to healthy peers in bounded wall time.
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ConsoleVerbosityLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(bool verbose) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        ConsoleVerbose = verbose,
        Games =
        [
            new Gm { Name = "ConsoleVerbosity", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 42u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000001ul, DrmHash = "pv318",
    };

    private static Srv Boot(bool verbose)
    {
        var server = new Srv();
        int placed = 0;
        server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(placed * 120f, 0f, 0f), new Vector3(5f, 0f, 0f), Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        server.Boot(Config(verbose));
        return server;
    }

    private static List<FltBot> SeatBots(Srv server, int count)
    {
        var bots = new List<FltBot>();
        for (int i = 0; i < count; i++)
        {
            var bot = new FltBot();
            bot.Connect(server.BoundPort, $"pv{i}");
            bot.SendLogonRequest(Request($"pv{i}"));
            bots.Add(bot);
        }
        for (int i = 0; i < 4000 && !bots.All(b => b.ShipAssignment is not null); i++) { server.TickSim(); Thread.Sleep(1); }
        if (!bots.All(b => b.ShipAssignment is not null))
            throw new TimeoutException("bots did not get seated");
        return bots;
    }

    /// <summary>A TextWriter that counts the number of complete lines written to it.</summary>
    private sealed class CountingWriter : TextWriter
    {
        public long Lines;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) { if (value != null) Lines += value.Count(c => c == '\n'); }
        public override void WriteLine(string? value) { Lines++; if (value != null) Lines += value.Count(c => c == '\n'); }
    }

    /// <summary>A TextWriter that sleeps on every write — simulates a slow/stalled
    /// stdout consumer (a pipe/journal/docker-logs reader that is not keeping up).</summary>
    private sealed class StallingWriter : TextWriter
    {
        private readonly int _perWriteMs;
        public StallingWriter(int perWriteMs) => _perWriteMs = perWriteMs;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => Thread.Sleep(_perWriteMs);
        public override void Write(char[] buffer, int index, int count) => Thread.Sleep(_perWriteMs);
        public override void WriteLine(string? value) => Thread.Sleep(_perWriteMs);
    }

    /// <summary>⭐ Real measured line-volume reduction with the gate at default (off).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_gate_off_emits_far_fewer_console_lines_than_verbose_on()
    {
        Require.Artwork();
        const int Bots = 24;
        const int Ticks = 240;          // 12 sim-seconds @ 20 Hz
        const int WarmupTicks = 60;     // settle join-burst churn before measuring

        var after = new CountingWriter();
        var oldOut = Console.Out;
        long beforeLines = 0, afterLines = 0;
        long beforePerSec = 0, afterPerSec = 0;
        try
        {
            // ---- "after" (gate off = default) ----
            using (var sOff = Boot(verbose: false))
            {
                var botsOff = SeatBots(sOff, Bots);
                foreach (var b in botsOff) b.ResetHeavyShipsUpdates();
                for (int i = 0; i < WarmupTicks; i++) sOff.TickSim();

                Console.SetOut(after);
                var wall = Stopwatch.StartNew();
                for (int i = 0; i < Ticks; i++) sOff.TickSim();
                double wallSec = Math.Max(wall.Elapsed.TotalSeconds, 1e-6);
                ConsoleLog.Flush();                       // drain while 'after' is still the target
                Console.SetOut(oldOut);
                afterLines = after.Lines;
                afterPerSec = (long)(afterLines / wallSec);

                // healthy peers still tick (per-tick updates flow even with logging silent)
                Assert.True(botsOff.All(b => b.HeavyShipsUpdates.Count > 0),
                    "healthy peers must still receive per-tick updates with the gate off");
            }

            // ---- "before" (gate on = reproduces pre-change unconditional lines) ----
            var before = new CountingWriter();
            using (var sOn = Boot(verbose: true))
            {
                var botsOn = SeatBots(sOn, Bots);
                foreach (var b in botsOn) b.ResetHeavyShipsUpdates();
                for (int i = 0; i < WarmupTicks; i++) sOn.TickSim();

                Console.SetOut(before);
                var wall = Stopwatch.StartNew();
                for (int i = 0; i < Ticks; i++) sOn.TickSim();
                double wallSec = Math.Max(wall.Elapsed.TotalSeconds, 1e-6);
                ConsoleLog.Flush();                       // drain while 'before' is still the target
                Console.SetOut(oldOut);
                beforeLines = before.Lines;
                beforePerSec = (long)(beforeLines / wallSec);

                Assert.True(botsOn.All(b => b.HeavyShipsUpdates.Count > 0),
                    "healthy peers must still receive per-tick updates with the gate on");
            }
        }
        finally
        {
            Console.SetOut(oldOut);
            ConsoleLog.ResetForTest();
        }

        // Gate on reproduces a large per-tick line volume; gate off collapses it to
        // near-zero (only the low-frequency event lines). Require at least a 10x drop
        // (real measurement is usually far larger — the named lines are per-tick).
        Assert.True(beforePerSec > 100, $"verbose baseline should produce real per-tick volume (got {beforePerSec}/s)");
        Assert.True(afterPerSec * 10 < beforePerSec,
            $"gate-off must be a measured reduction (before {beforePerSec}/s vs after {afterPerSec}/s)");
    }

    /// <summary>⭐ The core stability claim: a stalled/slow stdout consumer cannot
    /// block the sim thread even with verbosity ON (worst case, every per-tick line
    /// enabled).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Perf")]
    public void Stalled_stdout_does_not_block_the_sim_thread_even_verbose_on()
    {
        Require.Artwork();
        const int Bots = 24;
        const int Ticks = 300;          // 15 sim-seconds @ 20 Hz
        var oldOut = Console.Out;

        try
        {
            // Boot + seat on the REAL console first (avoid blocking during join burst).
            using var server = Boot(verbose: true);      // worst case: all gated lines on
            var bots = SeatBots(server, Bots);
            foreach (var b in bots) b.ResetHeavyShipsUpdates();
            int warmup = 0;
            for (int i = 0; i < 120; i++) { server.TickSim(); warmup = i; }
            _ = warmup;

            // Now redirect stdout to a writer that SLEEPS on every write — a consumer
            // far slower than it can be fed. With the bounded non-blocking sink, the
            // sim thread's Write returns immediately even as the drain thread blocks.
            Console.SetOut(new StallingWriter(perWriteMs: 30));
            var sw = Stopwatch.StartNew();
            double startSim = server.SimNow;
            for (int i = 0; i < Ticks; i++) server.TickSim();
            double simAdvanced = server.SimNow - startSim;
            long wallMs = sw.ElapsedMilliseconds;

            // Healthy peers keep receiving per-tick updates even while stdout is stalled.
            Assert.True(bots.All(b => b.HeavyShipsUpdates.Count > 0),
                "peers must keep receiving per-tick updates while stdout is stalled");

            // The sim advanced the full window (clock keeps moving — no freeze).
            Assert.True(simAdvanced >= Ticks / 20.0 * 0.95,
                $"mission clock must keep advancing during stdout stall (advanced {simAdvanced}s of {Ticks / 20.0}s)");

            // And it did so in bounded wall time: the per-tick gated lines would have
            // blocked ~#lines×30ms if written synchronously on the sim thread. With the
            // non-blocking sink the whole 15-s window completes well under that bound.
            Assert.True(wallMs < 8_000,
                $"sim must not block on a stalled stdout (wall {wallMs}ms for {Ticks} ticks, verbose-on)");
        }
        finally
        {
            Console.SetOut(oldOut);
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>⭐ P619c scope 2 — the P318 stalled-stdout claim EXTENDED to the
    /// plain <c>Console.WriteLine</c> path: with <see cref="ConsoleLog.Install"/>
    /// active (the process-wide redirect a real server run enables), the sim
    /// thread's bare <c>Console.WriteLine</c> calls (e.g. the <c>[egress]</c>
    /// window line, every plain write on the broadcast/sim path) cost an enqueue
    /// — so a stalled stdout consumer stalls only the drain thread, never the
    /// tick. The mission clock keeps advancing, healthy peers keep receiving
    /// per-tick updates, and the whole window completes in bounded wall time.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Perf")]
    public void Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout()
    {
        Require.Artwork();
        const int Bots = 8;
        const int Ticks = 300;          // 15 sim-seconds @ 20 Hz
        var oldOut = Console.Out;
        try
        {
            using var server = Boot(verbose: false);
            var bots = SeatBots(server, Bots);
            foreach (var b in bots) b.ResetHeavyShipsUpdates();
            for (int i = 0; i < 60; i++) server.TickSim();   // warmup join burst on real console

            // Activate the process-wide non-blocking redirect (normally done by
            // ServerMain.RunAsync) and point the drain at a consumer that sleeps
            // 30 ms per write — far slower than it can be fed.
            ConsoleLog.Install();
            ConsoleLog.PointSinkForTest(new StallingWriter(perWriteMs: 30));

            var sw = Stopwatch.StartNew();
            double startSim = server.SimNow;
            for (int i = 0; i < Ticks; i++) server.TickSim();
            double simAdvanced = server.SimNow - startSim;
            long wallMs = sw.ElapsedMilliseconds;

            Assert.True(bots.All(b => b.HeavyShipsUpdates.Count > 0),
                "peers must keep receiving per-tick updates while stdout is stalled");
            Assert.True(simAdvanced >= Ticks / 20.0 * 0.95,
                $"mission clock must keep advancing during stdout stall (advanced {simAdvanced}s of {Ticks / 20.0}s)");
            Assert.True(wallMs < 8_000,
                $"sim must not block on a stalled stdout (wall {wallMs}ms for {Ticks} ticks, plain WriteLine path)");
        }
        finally
        {
            ConsoleLog.Uninstall();
            Console.SetOut(oldOut);
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>⭐ Item 2: an NPC stuck in a stand-by condition (no mineable rock
    /// reachable) must log its "[npc] … standing by" line only ONCE on entering the
    /// stuck state — not once per tick the state holds (20 Hz in production). Drives
    /// the real private decision cycle directly on a sector-less Miner (no rock is
    /// reachable by construction: <c>FindNearestMinableAsteroidAnyCluster</c> returns
    /// null when <c>Sector == null</c>, Ship.cs:1333), which is the deterministic,
    /// repeatable stuck premise.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stuck_npc_standby_logs_once_not_per_tick()
    {
        Require.Artwork();
        const int Calls = 200;       // pre-fix a stuck miner logs ~1 line per call
        var oldOut = Console.Out;
        var sw = new StringWriter();
        long once;
        try
        {
            // Boot a real server to obtain a REAL DataHullTypeIGC (the Ship constructor
            // requires one; its Shape/MineCapacity/Ore fields don't matter for this branch).
            using var server = Boot(verbose: true);
            var hull = server.Missions.Single().Core.HullTypes.Values.First();

            // A bare Miner NPC with NO sector (never PlaceShip'd) → the mine-search
            // returns null every call (FindNearestMinableAsteroidAnyCluster:1333 returns
            // null when Sector is null), so DecideMiningOrSelling ALWAYS takes the
            // "no mineable asteroid" stand-by branch. Drive the real (private) decision
            // method repeatedly, exactly as the per-tick cycle would.
            var ship = new Ship((short)7701, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            {
                IsNpc = true,
                PilotType = Ship.PilotTypeMiner,
            };
            var decide = typeof(Ship).GetMethod("DecideMiningOrSelling",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(Ship), "DecideMiningOrSelling");

            ConsoleLog.Verbose = true;                      // worst case: stand-by lines enabled
            Console.SetOut(sw);
            for (int i = 0; i < Calls; i++) decide.Invoke(ship, [false]); // ordinary (not tapped) decision
            ConsoleLog.Flush();                             // drain emitted lines into sw
            Console.SetOut(oldOut);
            var text = sw.ToString();
            once = text.Split('\n').Count(l => l.Contains("standing by"));
        }
        finally
        {
            Console.SetOut(oldOut);
            ConsoleLog.ResetForTest();
        }

        // The transition guard must collapse the per-call flood into a single line.
        // Require exactly one stand-by for the whole repeated-stuck episode.
        Assert.True(once == 1, $"stuck NPC stand-by must log on transition only (got {once} lines over {Calls} calls, expected 1)");
    }
}
