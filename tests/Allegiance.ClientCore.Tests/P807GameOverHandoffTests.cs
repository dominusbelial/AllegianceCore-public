using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P807 — the game-over handoff at showcase scale (394 connected peers, the rig's
/// 200v200 march). The handoff was ~90 ms of <c>rotate-migrate</c> plus ~80 ms of
/// <c>gameOverMigration</c> at 394 peers, because the P765 migration still did
/// two superlinear things PER MIGRANT: it re-scanned every identity latch
/// collection to clear that peer's entries, and it fanned a PLAYERINFO announce
/// plus a JOIN_SIDE broadcast out to the whole mission. Now the latch reset is
/// ONE pass per collection (<c>RunMigrationLatchSweep</c>) and a rotation reseat
/// is silent on the wire: each peer's own phase-3 join burst carries every
/// ship's PLAYERINFO and the final lobby state, exactly like native's reset
/// (which re-announces nothing, fsmission.cpp:2604-2622, 3804-3828).
/// <para>The test pins: no handoff tick/step over budget at 394 peers, every peer
/// receives GAME_OVER before its reset burst, and every peer lands in the fresh
/// NotStarted mission on its original side. A second, small test runs two
/// consecutive rotations so the queue/drain path is exercised twice.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P807GameOverHandoffTests(ITestOutputHelper output)
{
    private readonly List<(ConnectionSession Peer, string Name)> _diagnosticPeers = [];
    private readonly Dictionary<ConnectionSession, short> _priorIds = [];
    private int _match;
    private string _wait = "setup";
    private int _waitIterations;
    private bool _advancingSim;
    private readonly Stopwatch _waitTime = new();
    private readonly List<string> _completedWaits = [];

    private void DumpState(Srv server)
    {
        output.WriteLine($"wait={_wait} match={_match} advanceSim={_advancingSim} iterations={_waitIterations} wallMs={_waitTime.Elapsed.TotalMilliseconds:0.0} " +
            $"simNow={server.SimNow:0.000} processors={Environment.ProcessorCount} serverGc={System.Runtime.GCSettings.IsServerGC}");
        output.WriteLine($"autoSeatSeconds={server.Config.SideSelectAutoSeatSeconds} simPassFailures={server.SimPassStageFailures}");
        if (server.TestPublisher is { } publisher)
            output.WriteLine($"snapshots built={server.SnapshotsBuilt} published={Interlocked.Read(ref publisher.PublishedSnapshots)} " +
                $"coalesced={Interlocked.Read(ref publisher.CoalescedSnapshots)} failures={Interlocked.Read(ref publisher.EncodeFailures)}");
        foreach (var mission in server.Missions)
        {
            output.WriteLine($"mission stage={mission.Stage} time={mission.LastUpdate:0.000}");
            foreach (var side in mission.Sides)
                output.WriteLine($"side={side.Id} ready={side.Ready} forceReady={side.ForceReady} active={side.Active} " +
                    $"peers={server.Ships.Count(s => s.Ship.Mission == mission && s.Ship.SideId == side.Id)}");
        }
        foreach (var name in new[] { "_pendingRotations", "_playerReady", "_pendingSideSelect", "_startCountdownDeadline", "_autoRestartDeadline" })
        {
            var value = typeof(Srv).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            output.WriteLine($"{name} count={((System.Collections.ICollection)value).Count}");
            if (value is System.Collections.IDictionary map)
                foreach (System.Collections.DictionaryEntry entry in map) output.WriteLine($"  {entry.Key}={entry.Value}");
            else if (value is System.Collections.IEnumerable list)
                foreach (var tail in list)
                    foreach (var field in tail.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(f => f.Name.EndsWith("Cursor", StringComparison.Ordinal) || f.Name == "LatchSweepStep"))
                        output.WriteLine($"  {field.Name}={field.GetValue(tail)}");
        }
        foreach (var (peer, name) in _diagnosticPeers)
        {
            if (!Monitor.TryEnter(peer.Gate, TimeSpan.FromMilliseconds(100)))
            {
                output.WriteLine($"peer={name} state lock busy; connected={peer.Connected}");
                continue;
            }
            try
            {
                output.WriteLine($"peer={name} connected={peer.Connected} old={(_priorIds.TryGetValue(peer, out var old) ? old.ToString() : "none")} " +
                    $"current={peer.World.MyShipId?.ToString() ?? "null"} joined={peer.World.JoinedShipId?.ToString() ?? "null"} side={peer.World.MySideId?.ToString() ?? "null"} " +
                    $"stage={peer.World.Mission?.Stage} countdown={peer.World.CountdownRemainingSeconds()} joinError={peer.World.JoinError} " +
                    $"ready=[{string.Join(",", peer.World.Roster.ReadyByShipId.Select(p => $"{p.Key}:{p.Value}"))}]");
            }
            finally { Monitor.Exit(peer.Gate); }
        }
        foreach (var live in server.Ships)
            output.WriteLine($"server ship={live.Ship.Id} side={live.Ship.SideId} fresh={server.Missions.Contains(live.Ship.Mission!)}");
    }

    private const int ShowcasePeers = 394;   // the rig's 200v200 march (394 connected)

    private static ServerConfig Config(int peers, bool profiler = false) => new()
    {
        Port = 0,
        SideSelectAutoSeatSeconds = 0.2,   // ConnectionSession peers never send POSITIONREQ
        Transport = new() { MaxConnections = 1024, MaxConnectionsPerIp = 1024 },
        // Keep the 394-peer fixture's explicitly calibrated historical tiers path;
        // its phase budgets measure migration, not the O(peers²) teammate stream.
        // The 24-peer correctness fixture uses the current baseline (tiers off).
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, InterestTiers = profiler },
        Profiler = profiler
            ? new() { Enabled = true, SlowTickCapture = true, SlowTickMs = 40, ReportIntervalSeconds = 100_000 }
            : null,
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new()
        {
            Name = "P807", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            MaxTeamImbalance = 32767, MaxPlayersPerTeam = Math.Max(200, peers / 2 + 5),
            MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 },
        }],
    };

    private static bool Read(ConnectionSession peer, Func<bool> predicate)
    { lock (peer.Gate) return predicate(); }

    private static T ReadValue<T>(ConnectionSession peer, Func<T> get)
    { lock (peer.Gate) return get(); }

    private static string CaptureLog(StringWriter log)
    {
        // Console.SetOut wraps the writer in a synchronized TextWriter. Share
        // its lock while reading the underlying buffer, which other threads append.
        lock (Console.Out) return log.ToString();
    }

    // Simulation predicates need ticks; client receipt predicates only need the
    // receive/publisher threads to drain work already offered. Advancing sim while
    // awaiting receipt can build a fresh backlog faster than loopback decodes it.
    private void Pump(Srv server, Func<bool> done, bool advanceSim = true,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string condition = "")
    {
        _wait = condition;
        _advancingSim = advanceSim;
        _waitTime.Restart();
        for (_waitIterations = 0; _waitIterations < 100_000 && _waitTime.Elapsed < TimeSpan.FromSeconds(30); _waitIterations++)
        {
            if (done())
            {
                if (_match > 0)
                    _completedWaits.Add($"match={_match} advanceSim={advanceSim} simTicks={(advanceSim ? _waitIterations : 0)} iterations={_waitIterations} wallMs={_waitTime.Elapsed.TotalMilliseconds:0.0} wait={condition}");
                return;
            }
            if (advanceSim) server.TickSim();
            Thread.Sleep(1);
        }
        Assert.True(done(), $"loopback condition timed out: match={_match} wait={condition} advanceSim={advanceSim} iterations={_waitIterations} wallMs={_waitTime.Elapsed.TotalMilliseconds:0.0}");
    }

    private ConnectionSession Connect(Srv server, string name, ulong id)
    {
        var peer = new ConnectionSession();
        _diagnosticPeers.Add((peer, name));
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => server.Ships.Any(s => s.Ship.Name == name && s.Ship.SideId is >= 0));
        var assigned = server.Ships.Single(s => s.Ship.Name == name).Ship;
        Pump(server, () => Read(peer, () => peer.World.MyShipId == assigned.Id && peer.World.MySideId == assigned.SideId), advanceSim: false);
        return peer;
    }

    private static bool PendingRotationsEmpty(Srv server)
    {
        var list = (System.Collections.ICollection)typeof(Srv)
            .GetField("_pendingRotations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        return list.Count == 0;
    }

    private static void End(Srv server, string reason) => typeof(Srv)
        .GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [server.Missions[0], (short)0, reason]);

    /// <summary>Every fmid in one packed server packet (message = u16 cbmsg +
    /// u16 fmid + body, trailing 4-byte CRC). Allocation-free apart from the
    /// caller's iterator; used only by the test's order-capture seam.</summary>
    private static IEnumerable<ushort> Fmids(byte[] packet)
    {
        int end = packet.Length - FedPacket.CrcWidth;
        for (int pos = 0; pos + 4 <= end;)
        {
            int size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(pos));
            if (size < 4 || pos + size > end) yield break;
            yield return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(pos + 2));
            pos += size;
        }
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void P807_394_peer_handoff_is_cheap_ordered_and_migrates_every_peer()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        using var server = new Srv();
        Console.SetOut(log);
        try
        {
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config(ShowcasePeers, profiler: true));
            var conns = new List<ConnectionSession>();
            for (int i = 0; i < ShowcasePeers; i++)
                conns.Add(Connect(server, $"P807-p{i:D4}", 807_000ul + (ulong)i));

            // Let the join bursts settle, then drain the transport before the
            // handoff so the measurement is the sim work, not test-side backlog.
            for (int i = 0; i < 5; i++) { server.TickSim(); Thread.Sleep(50); }
            Thread.Sleep(500);

            var oldIds = conns.Select(c => ReadValue(c, () => c.World.MyShipId!.Value)).ToArray();
            var oldSides = conns.Select(c => ReadValue(c, () => c.World.MySideId!.Value)).ToArray();
            _match = 1;
            for (int i = 0; i < conns.Count; i++) _priorIds[conns[i]] = oldIds[i];
            var old = server.Missions[0];
            old.Stage = MissionStage.Started;

            // Per-peer wire order: GAME_OVER(62) must be OFFERED before the reset
            // burst's JOINED_MISSION(95)/SET_CLUSTER(7). SendPeer is guaranteed
            // and per-peer ordered, so offer order is receive order.
            var goSeen = new HashSet<ConnectionId>();
            var orderViolations = new HashSet<ConnectionId>();
            var orderGate = new object();
            server.TestPublicationCapture = (peer, bytes, _, _) =>
            {
                // With the snapshot pipeline on, broadcast deliveries are offered
                // from the publisher thread while the handoff's direct sends run
                // on the sim thread.
                lock (orderGate)
                {
                    foreach (var fmid in Fmids(bytes))
                    {
                        if (fmid == GameOver.Fmid) goSeen.Add(peer);
                        else if ((fmid == JoinedMission.Fmid || fmid == SetCluster.Fmid) && !goSeen.Contains(peer))
                            orderViolations.Add(peer);
                    }
                }
            };

            End(server, "P807 handoff");
            double declareMs = server.LastGameOverDeclareMs;
            double declareGcMs = server.LastGameOverDeclareGcPauseMs;
            double setupMs = server.LastRotateMigrateSetupMs;

            var fresh = server.Missions[0];
            Assert.NotSame(old, fresh);
            Assert.Equal(MissionStage.NotStarted, fresh.Stage);

            Pump(server, () => PendingRotationsEmpty(server));
            // The server-side queue drains when the last burst is OFFERED; each
            // client applies it on its own receive thread, so wait for every
            // world to hold the fresh ship (and its result screen) before reading.
            Pump(server, () => Enumerable.Range(0, ShowcasePeers).All(i =>
                Read(conns[i], () => conns[i].World.MyShipId is { } id && id != oldIds[i]
                    && conns[i].World.GameOverResult is not null)), advanceSim: false);
            server.TestPublicationCapture = null;

            // Correctness: every peer still connected, got the result screen,
            // sits in the fresh mission on its original side with a fresh ship.
            for (int i = 0; i < ShowcasePeers; i++)
            {
                var peer = conns[i];
                Assert.True(Read(peer, () => peer.World.GameOverResult is not null),
                    $"peer {i} never received GAME_OVER");
                short newId = ReadValue(peer, () => peer.World.MyShipId!.Value);
                Assert.NotEqual(oldIds[i], newId);
                Assert.Equal(oldSides[i], ReadValue(peer, () => peer.World.MySideId!.Value));
                var ship = server.Ships.Single(s => s.Ship.Id == newId).Ship;
                Assert.Same(fresh, ship.Mission);
                Assert.Equal(oldSides[i], ship.SideId);
            }
            Assert.Equal(0, server.SimPassStageFailures);
            Assert.DoesNotContain("[simpass]", CaptureLog(log));
            Assert.Empty(orderViolations);
            Assert.Equal(ShowcasePeers, goSeen.Count);
            Assert.True(server.GameOverMigrationTicks > 0, "the migration must actually spread over ticks");

            // Budgets. The phase slice is the P807 regression: on master the
            // per-migrant latch scan + mission-wide reseat fan-out pushed a single
            // reseat batch past 100 ms at this size (rig: gameOverMigration 80 ms,
            // rotate-migrate 87.3 ms); now the largest slice is the join burst.
            double phaseBudget = PerfCalibration.Scale(80.0);
            double setupBudget = PerfCalibration.Scale(30.0);
            double migrationStepBudget = PerfCalibration.Scale(100.0);
            var stepSamples = CaptureLog(log).Split('\n')
                .Select(line => line.Contains("step:gameOverMigration=")
                    ? line[(line.IndexOf("step:gameOverMigration=", StringComparison.Ordinal)
                            + "step:gameOverMigration=".Length)..]
                    : null)
                .Where(v => v is not null)
                .Select(v => double.Parse(v![..v!.IndexOf("ms", StringComparison.Ordinal)], CultureInfo.InvariantCulture))
                .ToArray();
            double maxStep = stepSamples.DefaultIfEmpty(0).Max();

            output.WriteLine($"peers={ShowcasePeers} declareMs={declareMs:0.0} declareGcMs={declareGcMs:0.0} " +
                $"declareCpuMs={declareMs - declareGcMs:0.0} rotateMigrateSetupMs={setupMs:0.0} " +
                $"migrationTicks={server.GameOverMigrationTicks} maxPhaseMs={server.MaxGameOverMigrationPhaseMs:0.0} " +
                $"maxGameOverMigrationStepMs={maxStep:0.0} budgets phase={phaseBudget:0.0} setup={setupBudget:0.0} step={migrationStepBudget:0.0}");

            Assert.True(server.MaxGameOverMigrationPhaseMs < phaseBudget,
                $"largest migration phase slice {server.MaxGameOverMigrationPhaseMs:0.0}ms exceeded {phaseBudget:0.0}ms at {ShowcasePeers} peers");
            Assert.True(setupMs < setupBudget,
                $"rotate-migrate setup {setupMs:0.0}ms exceeded {setupBudget:0.0}ms at {ShowcasePeers} peers");
            Assert.True(maxStep < migrationStepBudget,
                $"a gameOverMigration step took {maxStep:0.0}ms (budget {migrationStepBudget:0.0}ms) at {ShowcasePeers} peers");
            // The game-over tick's own sim work (excluding process-wide GC pauses,
            // which the 394 in-process test clients dominate locally; the rig has
            // no in-process clients) stays under the 100 ms target with margin.
            Assert.True(declareMs - declareGcMs < PerfCalibration.Scale(150.0),
                $"game-over declare sim work {declareMs - declareGcMs:0.0}ms exceeded budget");
        }
        catch
        {
            output.WriteLine("Captured server console:\n" + CaptureLog(log));
            try { DumpState(server); }
            catch (Exception diagnosticError) { output.WriteLine($"State capture failed: {diagnosticError}"); }
            throw;
        }
        finally
        {
            foreach (string wait in _completedWaits) output.WriteLine(wait);
            try { foreach (var (peer, _) in _diagnosticPeers) peer.Dispose(); }
            finally { Console.SetOut(saved); }
        }
    }

    /// <summary>
    /// P807 — two consecutive rotations (game over → next match → game over) on
    /// the deferred queue: each handoff drains completely, every peer lands in
    /// that handoff's fresh mission, and the second match actually starts in
    /// between (all peers ready up). The 394-peer test above covers the cost; this
    /// covers the queue/drain path twice.
    /// </summary>
    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void P807_two_consecutive_handoffs_still_work()
    {
        Require.Artwork();
        const int peers = 24;
        var saved = Console.Out;
        using var log = new StringWriter();
        using var server = new Srv();
        Console.SetOut(log);
        try
        {
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config(peers));
            var conns = new List<ConnectionSession>();
            for (int i = 0; i < peers; i++)
                conns.Add(Connect(server, $"P807r-p{i:D3}", 807_500ul + (ulong)i));

            for (int match = 1; match <= 2; match++)
            {
                var oldIds = conns.Select(c => ReadValue(c, () => c.World.MyShipId!.Value)).ToArray();
                _match = match;
                for (int i = 0; i < peers; i++) _priorIds[conns[i]] = oldIds[i];
                var old = server.Missions[0];
                old.Stage = MissionStage.Started;
                End(server, $"P807 rotation {match}");
                var fresh = server.Missions[0];
                Assert.NotSame(old, fresh);
                Assert.Equal(MissionStage.NotStarted, fresh.Stage);

                Pump(server, () => PendingRotationsEmpty(server));
                Pump(server, () => Enumerable.Range(0, peers).All(i =>
                    Read(conns[i], () => conns[i].World.MyShipId is { } id && id != oldIds[i])), advanceSim: false);
                for (int i = 0; i < peers; i++)
                {
                    short newId = ReadValue(conns[i], () => conns[i].World.MyShipId!.Value);
                    Assert.NotEqual(oldIds[i], newId);
                    Assert.Same(fresh, server.Ships.Single(s => s.Ship.Id == newId).Ship.Mission);
                }

                if (match == 1)
                {
                    foreach (var peer in conns)
                        peer.SendPlayerReady(true, ReadValue(peer, () => peer.World.MyShipId!.Value));
                    Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
                }
            }
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"peers={peers} two rotations drained; second match started; simPassFailures=0");
        }
        catch
        {
            output.WriteLine("Captured server console:\n" + CaptureLog(log));
            try { DumpState(server); }
            catch (Exception diagnosticError) { output.WriteLine($"State capture failed: {diagnosticError}"); }
            throw;
        }
        finally
        {
            foreach (string wait in _completedWaits) output.WriteLine(wait);
            try { foreach (var (peer, _) in _diagnosticPeers) peer.Dispose(); }
            finally { Console.SetOut(saved); }
        }
    }
}
