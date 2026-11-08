using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Native resets all players and the mission synchronously before the next
/// start check (FedSrv/fsmission.cpp:3433-3546; FedSrv/FedSrv.CPP:8234-8241).
/// The port's bounded migration must preserve that prerequisite.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P965RestartMigrationBarrierTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(Srv server, string name) =>
        (T)typeof(Srv).GetField(name, Private)!.GetValue(server)!;

    private static void Call(Srv server, string name, params object[] args) =>
        typeof(Srv).GetMethod(name, Private)!.Invoke(server, args);

    private static bool Pending(Srv server) => Field<ICollection>(server, "_pendingRotations").Count > 0;

    private static bool Read(ConnectionSession peer, Func<bool> condition)
    { lock (peer.Gate) return condition(); }

    private static void Pump(Srv server, Func<bool> done, bool advanceSim = true)
    {
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 20_000 && watch.Elapsed < TimeSpan.FromSeconds(30); i++)
        {
            if (done()) return;
            if (advanceSim) server.TickSim();
            Thread.Sleep(1);
        }
        Assert.True(done(), $"loopback wait timed out; sim={server.SimNow:R} pending={Pending(server)}");
    }

    private static ServerConfig Config(int peers, bool autoRestart = true) => new()
    {
        Port = 0,
        SideSelectAutoSeatSeconds = 0.1,
        Transport = new() { MaxConnections = 128, MaxConnectionsPerIp = 128 },
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, InterestTiers = false },
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new()
        {
            Name = "P965", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            AutoRestart = autoRestart,
            // GameConfig accepts zero; this is the shortest valid restart.
            RestartCountdownSeconds = 0, StartCountdownSeconds = 0,
            MaxTeamImbalance = 32767, MaxPlayersPerTeam = peers,
            MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 1, PlayerSectorSpecialAsteroids = 1 },
        }],
    };

    private static ConnectionSession Connect(Srv server, int index)
    {
        string name = $"P965-{index:D3}";
        var peer = new ConnectionSession();
        try
        {
            peer.Connect(server.BoundPort, name);
            peer.SendLogonRequest(new()
            {
                CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
                ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                Time = 1, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16],
                SteamID = 965_000ul + (ulong)index,
            });
            Pump(server, () => server.Ships.Any(s => s.Ship.Name == name && s.Ship.SideId is >= 0));
            var ship = server.Ships.Single(s => s.Ship.Name == name).Ship;
            Pump(server, () => Read(peer, () => peer.World.MyShipId == ship.Id && peer.World.MySideId == ship.SideId), false);
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }

    private static void End(Srv server) => Call(server, "DeclareGameOver", server.Missions[0], (short)0, "P965 barrier");

    private static string Log(StringWriter log)
    { lock (Console.Out) return log.ToString(); }

    private sealed class WireOrder
    {
        private readonly object _gate = new();
        private readonly HashSet<ConnectionId> _gameOver = [];
        private readonly HashSet<ConnectionId> _joined = [];
        private readonly HashSet<ConnectionId> _joinState = [];
        private readonly HashSet<ConnectionId> _entered = [];
        private readonly List<string> _events = [];
        private readonly List<string> _violations = [];

        public void Capture(ConnectionId peer, byte[] bytes, int key, bool guaranteed)
        {
            lock (_gate)
            {
                int end = bytes.Length - FedPacket.CrcWidth;
                for (int pos = 0; pos + 4 <= end;)
                {
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
                    if (size < 4 || pos + size > end) break;
                    ushort fmid = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 2));
                    string? kind = null;
                    if (fmid == GameOver.Fmid) { _gameOver.Add(peer); kind = "GAME_OVER"; }
                    if (fmid == JoinedMission.Fmid)
                    {
                        if (!_gameOver.Contains(peer)) _violations.Add($"{peer}: reset before GAME_OVER");
                        _joined.Add(peer); kind = "JOINED_MISSION";
                    }
                    if (fmid == GameState.Fmid && _joined.Contains(peer) && _joinState.Add(peer))
                        kind = "join GAME_STATE"; // last join-sync message before optional ENTER_GAME
                    if (fmid == MissionStageMessage.Fmid)
                    {
                        int stage = MissionStageMessage.Decode(bytes.AsSpan(pos, size)).Stage;
                        if (stage is MissionStageMessage.StageStarting or MissionStageMessage.StageStarted)
                        {
                            kind = $"stage {(MissionStage)stage}";
                            if (_joinState.Count < _gameOver.Count)
                                _violations.Add($"{peer}: {kind} after {_joinState.Count}/{_gameOver.Count} join bursts");
                        }
                    }
                    if (fmid == EnterGame.Fmid)
                    {
                        _entered.Add(peer); kind = "ENTER_GAME";
                        if (_joinState.Count < _gameOver.Count)
                            _violations.Add($"{peer}: ENTER_GAME after {_joinState.Count}/{_gameOver.Count} join bursts");
                    }
                    if (kind is not null) _events.Add($"{_events.Count + 1}: {peer} {kind}");
                    pos += size;
                }
            }
        }

        public bool AllEntered(int count) { lock (_gate) return _entered.Count == count; }

        public void Verify(int peers, ITestOutputHelper output)
        {
            lock (_gate)
            {
                foreach (var e in _events) output.WriteLine(e);
                Assert.Equal(peers, _gameOver.Count);
                Assert.Equal(peers, _joined.Count);
                Assert.Equal(peers, _joinState.Count);
                Assert.Equal(peers, _entered.Count);
                Assert.True(_violations.Count == 0, string.Join("\n", _violations));
            }
        }
    }

    [SkippableTheory, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    [InlineData(24)]
    [InlineData(64)]
    public void Auto_restart_waits_for_every_reseat_and_join_burst_without_moving_its_target(int peers) =>
        RunAutoRestart(peers);

    [SkippableTheory, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Auto_next_match_keeps_the_absolute_intermission_and_briefing_targets(bool pastBriefing) =>
        RunAutoRestart(24, arena: true, pastBriefing);

    private void RunAutoRestart(int peers, bool arena = false, bool pastBriefing = false)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        using var server = new Srv();
        var conns = new List<ConnectionSession>();
        Console.SetOut(log);
        try
        {
            server.SpawnInitialMinersAtBoot = false;
            var config = Config(peers);
            config.LiveSettings!.AutoNextMatch = new() { Enabled = arena, IntermissionSeconds = 0 };
            server.Boot(config);
            for (int i = 0; i < peers; i++) conns.Add(Connect(server, i));
            var old = server.Missions[0];
            var originals = server.TestSnapshotPeers().ToDictionary(p => p.Conn, p => (p.Ship.Id, p.Ship.SideId));
            old.Stage = MissionStage.Started;
            var order = new WireOrder();
            server.TestPublicationCapture = order.Capture;
            End(server);
            var fresh = server.Missions[0];
            double target = Field<Dictionary<Mission, double>>(server, "_autoRestartDeadline")[fresh];
            double expectedStart = target + (arena ? 15 : 0);
            // Emulate an overdue migration without adding CPU load or changing
            // the configured per-tick slice. The original deadline must survive.
            if (pastBriefing) typeof(Srv).GetField("_simNow", Private)!.SetValue(server, expectedStart + 30);
            Assert.NotSame(old, fresh);
            Assert.True(Pending(server), "must exercise deferred (>8 peer) migration");

            var earlyStages = new List<string>();
            int ticks = 0;
            while (Pending(server) && ticks++ < 200)
            {
                server.TickSim();
                if (fresh.Stage != MissionStage.NotStarted)
                    earlyStages.Add($"tick={ticks} sim={server.SimNow:R} stage={fresh.Stage}");
                if (Field<Dictionary<Mission, double>>(server, "_autoRestartDeadline").TryGetValue(fresh, out double held))
                    Assert.Equal(target, held);
            }
            Assert.False(Pending(server));
            output.WriteLine($"peers={peers} migrationTicks={ticks} barrierReleasedAt={server.SimNow:R} originalTarget={target:R}");
            foreach (string stage in earlyStages) output.WriteLine("EARLY " + stage);
            Pump(server, () => fresh.Stage == MissionStage.Started);
            Pump(server, () => order.AllEntered(peers) && conns.All(p => Read(p,
                () => p.World.Mission?.Stage == (int)MissionStage.Started)), false);
            foreach (var p in server.TestSnapshotPeers())
            {
                Assert.Same(fresh, p.Ship.Mission);
                Assert.NotEqual(originals[p.Conn].Id, p.Ship.Id);
                Assert.Equal(originals[p.Conn].SideId, p.Ship.SideId);
            }
            // The console records completion of each entire join function,
            // independently of the publisher's final join GAME_STATE marker.
            string[] events = Log(log).Split('\n').Where(l => l.StartsWith("[reseat]", StringComparison.Ordinal)
                || l.StartsWith("[start]", StringComparison.Ordinal)).ToArray();
            foreach (string e in events) output.WriteLine(e);
            order.Verify(peers, output);
            Assert.Empty(earlyStages);
            int[] reseats = events.Select((line, i) => (line, i)).Where(e => e.line.Contains("-> fresh mission side", StringComparison.Ordinal)).Select(e => e.i).ToArray();
            int[] bursts = events.Select((line, i) => (line, i)).Where(e => e.line.Contains("re-sent fresh-joiner join burst", StringComparison.Ordinal)).Select(e => e.i).ToArray();
            Assert.Equal(peers, reseats.Length);
            Assert.Equal(peers, bursts.Length);
            Assert.True(reseats.Max() < bursts.Min());
            Assert.True(bursts.Max() < Array.FindIndex(events, e => e.StartsWith("[start]", StringComparison.Ordinal)));
            Assert.Equal(expectedStart, fresh.CountdownStart);
            Assert.Equal(0, server.SimPassStageFailures);
        }
        catch { output.WriteLine(Log(log)); throw; }
        finally
        {
            server.TestPublicationCapture = null;
            foreach (var peer in conns) peer.Dispose();
            Console.SetOut(saved);
        }
    }

    [SkippableTheory, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Ready_and_manual_starts_wait_then_ready_resumes_and_other_missions_are_unblocked(bool debug)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        using var server = new Srv();
        var conns = new List<ConnectionSession>();
        Console.SetOut(log);
        try
        {
            var config = Config(24, autoRestart: false);
            config.LiveSettings!.DebugMode = debug;
            config.Games[0].AllowEmptyTeams = true; // also exercises the early debug start
            config.Games.Add(new()
            {
                Name = "P965-other", AutoStart = false, AllowEmptyTeams = true,
                Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
                MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 1, PlayerSectorSpecialAsteroids = 1 },
            });
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(config);
            for (int i = 0; i < 24; i++) conns.Add(Connect(server, i));
            var ready = Field<Dictionary<ConnectionId, bool>>(server, "_playerReady");
            foreach (var p in server.TestSnapshotPeers()) ready[p.Conn] = true;
            server.Missions[0].Stage = MissionStage.Started;
            End(server);
            var fresh = server.Missions[0];
            Assert.Equal(MissionStage.NotStarted, fresh.Stage);
            Assert.True(Pending(server));
            var other = server.Missions[1];
            // START_GAME converges on this same method after its owner/balance
            // checks. Invoke it directly so packet scheduling cannot mask a bypass.
            Call(server, "StartCountdown", other, 0f);
            Assert.Equal(MissionStage.Starting, other.Stage);
            int ticks = 0;
            while (Pending(server) && ticks++ < 200)
            {
                Call(server, "TryAutoStart", fresh);
                Call(server, "StartCountdown", fresh, 0f);
                Assert.Equal(MissionStage.NotStarted, fresh.Stage);
                server.TickSim();
                if (Pending(server)) Assert.Equal(MissionStage.NotStarted, fresh.Stage);
                Assert.Equal(MissionStage.Started, other.Stage);
            }
            Assert.False(Pending(server));
            Assert.Equal(MissionStage.Starting, fresh.Stage); // no new ready edge is required
            Pump(server, () => fresh.Stage == MissionStage.Started);
            Assert.Equal(24, server.Ships.Count(s => ReferenceEquals(s.Ship.Mission, fresh) && s.Ship.SideId >= 0));
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"debug={debug} ticks={ticks}; manual/ready held; independent mission started; preserved-ready resumed");
        }
        catch { output.WriteLine(Log(log)); throw; }
        finally
        {
            foreach (var peer in conns) peer.Dispose();
            Console.SetOut(saved);
        }
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void Existing_start_countdown_cannot_complete_during_migration_and_keeps_its_target()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        using var server = new Srv();
        var conns = new List<ConnectionSession>();
        Console.SetOut(log);
        try
        {
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config(24, autoRestart: false));
            for (int i = 0; i < 24; i++) conns.Add(Connect(server, i));
            server.Missions[0].Stage = MissionStage.Started;
            End(server);
            var fresh = server.Missions[0];
            // Seed an already armed, expired countdown to exercise its driver
            // independently of the NotStarted start guards.
            double target = server.SimNow - 1;
            fresh.Stage = MissionStage.Starting;
            var deadlines = Field<Dictionary<Mission, double>>(server, "_startCountdownDeadline");
            deadlines[fresh] = target;
            Assert.True(Pending(server));
            int ticks = 0;
            while (Pending(server) && ticks++ < 200)
            {
                server.TickSim();
                Assert.Equal(MissionStage.Starting, fresh.Stage);
                Assert.Equal(target, deadlines[fresh]);
            }
            Assert.False(Pending(server));
            server.TickSim();
            Assert.Equal(MissionStage.Started, fresh.Stage);
            Assert.Equal(target, fresh.CountdownStart);
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"existing countdown held {ticks} migration ticks; originalTarget={target:R}; completed on next tick");
        }
        catch { output.WriteLine(Log(log)); throw; }
        finally
        {
            foreach (var peer in conns) peer.Dispose();
            Console.SetOut(saved);
        }
    }
}
