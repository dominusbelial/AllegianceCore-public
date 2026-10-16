using System.Diagnostics;
using System.Text.Json;
using Allegiance.Tools.McpSpawner;
using ModelContextProtocol.Protocol;

namespace Allegiance.ClientCore.Tests;

public sealed class P658SupervisorTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Body(CallToolResult result) => JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement.Clone();
    private static WorkerReply Reply(object value, bool error = false) => new(error, Json(value));

    [Theory]
    [InlineData(195, 2, new[] { 98, 97 })]
    [InlineData(13, 2, new[] { 7, 6 })]
    [InlineData(7, 3, new[] { 3, 2, 2 })]
    [InlineData(1, 4, new[] { 1, 0, 0, 0 })]
    [InlineData(0, 2, new[] { 0, 0 })]
    [InlineData(195, 1, new[] { 195 })]
    public void SplitPreservesOddTotalsAndDefault(int total, int workers, int[] expected) => Assert.Equal(expected, WorkerPartition.Split(total, workers));

    [Fact]
    public void PartitionCountsEachSideIndependentlyAndKeepsOriginalSlotIndices()
    {
        var slots = Enumerable.Range(0, 7).Select(i => new SpawnBatch.Slot(i, 0, "fighter", () => default))
            .Concat(Enumerable.Range(7, 5).Select(i => new SpawnBatch.Slot(i, 1, "bomber", () => default))).ToArray();
        var a = new WorkerPartition(0, 2).Select(slots);
        var b = new WorkerPartition(1, 2).Select(slots);
        Assert.Equal(new[] { 0, 2, 4, 6, 7, 9, 11 }, a.Select(s => s.Index));
        Assert.Equal(WorkerPartition.Split(7, 2), new[] { a.Count(s => s.Side == 0), b.Count(s => s.Side == 0) });
        Assert.Equal(WorkerPartition.Split(5, 2), new[] { a.Count(s => s.Side == 1), b.Count(s => s.Side == 1) });
        Assert.Equal(slots, new WorkerPartition(0, 1).Select(slots));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("abc")]
    public void InvalidWorkerCountsFailBeforeStartup(string count) => Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--workers", count]));

    [Fact]
    public void OptionsDefaultAndInternalWorkerAreDistinct()
    {
        Assert.Equal(1, DriverOptions.Parse(["localhost", "6073"]).Workers);
        Assert.Equal(11, McpTools.Definitions().Count);
        Assert.Equal(14, McpTools.Definitions(supervisor: true).Count);
        Assert.Null(DriverOptions.Parse(["localhost", "6073", "--workers", "1"]).Worker);
        Assert.Equal(new WorkerPartition(1, 2), DriverOptions.Parse(["localhost", "6073", "--worker", "2/2"]).Worker);
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--worker", "2/2", "--workers", "2"]));
    }

    private sealed class FakeWorker(int index) : IWorker
    {
        public int Index => index;
        public int Pid => 100 + index;
        public bool Alive { get; set; } = true;
        public string? Failure => Alive ? null : "fake crash";
        public List<string> Calls { get; } = [];
        public Func<string, Task<WorkerReply>>? Handler { get; set; }
        public Action<string, JsonElement>? Observe { get; set; }
        public Task<WorkerReply> CallAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            Calls.Add(method);
            Observe?.Invoke(method, parameters);
            if (!Alive) throw new IOException("fake crash");
            return Handler?.Invoke(method) ?? Task.FromResult(Reply(new { stopped = index + 1, cleared = index + 1 }));
        }
        public void Dispose() => Alive = false;
    }

    [Theory]
    [InlineData("stop_bots", "stopped")]
    [InlineData("clear_bots", "cleared")]
    [InlineData("despawn_bot", "stopped")]
    public async Task LifecycleFansOutAndSumsActualCounts(string name, string key)
    {
        var workers = new[] { new FakeWorker(0), new FakeWorker(1) };
        using var supervisor = new WorkerSupervisor(workers, coordinate: false);
        var body = Body(await supervisor.HandleAsync(name, null, CancellationToken.None));
        Assert.Equal(3, body.GetProperty(key).GetInt32());
        Assert.All(workers, w => Assert.Contains(name, w.Calls));
    }

    [Fact]
    public async Task PrivateWorkerRpcMethodsAreNotExposedAsMcpTools()
    {
        var worker = new FakeWorker(0);
        using var supervisor = new WorkerSupervisor([worker], coordinate: false);
        Assert.True((await supervisor.HandleAsync("__ping", null, CancellationToken.None)).IsError);
        Assert.Empty(worker.Calls);
    }

    private static object Counts(short side, int requested, int spawned, int refilled = 0) => new { side, requested, spawned, failed = requested - spawned, refilled };
    private static object Grant(string name, short side) => new { name, side, grantedHull = 15 };
    private static WorkerReply Plan(int a, int b) => Reply(new { sideA = Counts(0, a, 0), sideB = Counts(1, b, 0) });
    private static WorkerReply State(params (string Name, short Side, string Status)[] bots) => Reply(new WorkerState(CoordinationState.Empty,
        Json(bots.Select(b => new { name = b.Name, side = b.Side, status = b.Status, shipId = 7 }))));

    [Fact]
    public async Task SpawnRunsInParallelAndMergesHonestCountsFailuresAndBotList()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int spawning = 0;
        var workers = new[] { new FakeWorker(0), new FakeWorker(1) };
        foreach (var worker in workers)
        {
            int index = worker.Index;
            worker.Handler = async method =>
            {
                if (method == "__plan") return Plan(index == 0 ? 2 : 1, index == 0 ? 2 : 1);
                if (method == "__hold") return Reply(true);
                if (method == "__state") return index == 0 ? State(("a0", 0, "active"), ("b0", 1, "active")) : State(("a1", 0, "active"), ("b1", 1, "active"));
                if (method == "list_active_bots") return (await worker.Handler!("__state")).Data.Deserialize<WorkerState>() is { } state ? new(false, state.Bots) : throw new Exception();
                Assert.Equal("spawn_scenario", method);
                if (Interlocked.Increment(ref spawning) == 2) started.SetResult();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return Reply(new { sideA = Counts(0, index == 0 ? 2 : 1, 1, index), sideB = Counts(1, index == 0 ? 2 : 1, 1),
                    grants = new[] { Grant($"a{index}", 0), Grant($"b{index}", 1) }, warnings = index,
                    failures = index == 0 ? new[] { new { side = 0, error = "launch timeout" }, new { side = 1, error = "disconnect" } } : [], minefields = Array.Empty<object>() });
            };
        }
        using var supervisor = new WorkerSupervisor(workers, coordinate: false);
        var result = await supervisor.HandleAsync("spawn_scenario", new Dictionary<string, JsonElement> { ["size"] = Json(3) }, CancellationToken.None);
        Assert.False(result.IsError);
        var body = Body(result);
        Assert.Equal(3, body.GetProperty("sideA").GetProperty("requested").GetInt32());
        Assert.Equal(2, body.GetProperty("sideA").GetProperty("spawned").GetInt32());
        Assert.Equal(1, body.GetProperty("sideB").GetProperty("failed").GetInt32());
        Assert.Equal(1, body.GetProperty("sideA").GetProperty("refilled").GetInt32());
        Assert.Equal(4, body.GetProperty("grants").GetArrayLength());
        Assert.Equal(2, body.GetProperty("failures").GetArrayLength());
        Assert.Equal(4, Body(await supervisor.HandleAsync("list_active_bots", null, CancellationToken.None)).GetArrayLength());
        Assert.All(workers, w => Assert.Equal(2, w.Calls.Count(c => c == "__hold")));
    }

    [Fact]
    public void MergeRemovesBotLostWhileAnotherWorkerWasStillSpawning()
    {
        var reply = Reply(new { sideA = Counts(0, 1, 1), sideB = Counts(1, 1, 1), grants = new[] { Grant("gone", 0), Grant("live", 1) }, failures = Array.Empty<object>() });
        var result = SupervisorResults.MergeScenario([Plan(1, 1)], [reply], [State(("gone", 0, "removed"), ("live", 1, "active"))], []);
        Assert.Equal(0, result.GetProperty("sideA").GetProperty("spawned").GetInt32());
        Assert.Equal(1, result.GetProperty("sideA").GetProperty("failed").GetInt32());
        Assert.Single(result.GetProperty("grants").EnumerateArray());
        Assert.Contains("disconnected", result.GetProperty("failures")[0].GetProperty("error").GetString());
    }

    [Fact]
    public void RefilledBotLostDuringParallelSpawnDoesNotInflateRefillCount()
    {
        var reply = Reply(new { sideA = Counts(0, 2, 2, 1), sideB = Counts(1, 0, 0), grants = new[] {
            new { name = "original", side = 0, refilled = false }, new { name = "replacement", side = 0, refilled = true } }, failures = Array.Empty<object>() });
        var body = SupervisorResults.MergeScenario([Plan(2, 0)], [reply], [State(("original", 0, "active"))], []);
        Assert.Equal(1, body.GetProperty("sideA").GetProperty("spawned").GetInt32());
        Assert.Equal(0, body.GetProperty("sideA").GetProperty("refilled").GetInt32());
    }

    [Fact]
    public async Task LaterScenarioStillSpawnsOnHealthyWorkerAndReportsLostPartition()
    {
        var lost = new FakeWorker(0) { Alive = false };
        var healthy = new FakeWorker(1) { Handler = method => Task.FromResult(method switch
        {
            "__plan" => Reply(new { sideA = Counts(0, 1, 0), sideB = Counts(1, 1, 0), totals = new[] { new { side = 0, requested = 3 }, new { side = 1, requested = 3 } } }),
            "spawn_scenario" => Reply(new { sideA = Counts(0, 1, 1), sideB = Counts(1, 1, 1), grants = new[] { Grant("a", 0), Grant("b", 1) }, failures = Array.Empty<object>() }),
            "__state" => State(("a", 0, "active"), ("b", 1, "active")),
            _ => Reply(true),
        }) };
        using var supervisor = new WorkerSupervisor([lost, healthy], coordinate: false);
        var result = await supervisor.HandleAsync("spawn_scenario", null, CancellationToken.None);
        Assert.False(result.IsError);
        var side = Body(result).GetProperty("sideA");
        Assert.Equal(3, side.GetProperty("requested").GetInt32());
        Assert.Equal(1, side.GetProperty("spawned").GetInt32());
        Assert.Equal(2, side.GetProperty("failed").GetInt32());
    }

    [Fact]
    public void ExitDuringSpawnContributesFailedRequestedSlots()
    {
        var lost = Reply(new { error = "child exited" }, true);
        var result = SupervisorResults.MergeScenario([Plan(7, 6)], [lost], [lost], []);
        Assert.Equal(7, result.GetProperty("sideA").GetProperty("failed").GetInt32());
        Assert.Equal(6, result.GetProperty("sideB").GetProperty("failed").GetInt32());
        Assert.Empty(result.GetProperty("grants").EnumerateArray());
    }

    [Fact]
    public void WorkerEnvelopesAndPooledCadenceRetainStallCounters()
    {
        WorkerReply Perf(string name, double interval, int reads, int incidents) => Reply(new
        {
            Report = new { botCount = 1, decisionRateHz = 20.0, fleetDecisionRateHz = 20.0,
                resources = new { rssMb = 100.0 }, stall = new { sessions = 3, sessionsReadOver2s = reads, incidentCount = incidents,
                    receiveAgeMaxMs = interval * 1000, currentMinute = new { gcPauseTotalMs = 4.0, gcPauseMaxMs = interval } } },
            Cadence = new[] { new BotCadence(name, [new(interval, 10)]) },
        });
        var body = SupervisorResults.MergePerf([Perf("a", 1, 1, 2), Perf("b", 3, 2, 4)], [new { worker = 1 }, new { worker = 2 }], []);
        Assert.Equal(2, body.GetProperty("botCount").GetInt32());
        Assert.Equal(2, body.GetProperty("interval").GetProperty("mean").GetDouble());
        Assert.Equal(3, body.GetProperty("interval").GetProperty("p95").GetDouble());
        Assert.Equal("b", body.GetProperty("interval").GetProperty("worstBot").GetString());
        Assert.Equal(6, body.GetProperty("stall").GetProperty("sessions").GetDouble());
        Assert.Equal(6, body.GetProperty("stall").GetProperty("incidentCount").GetDouble());
        Assert.Equal(8, body.GetProperty("stall").GetProperty("currentMinute").GetProperty("gcPauseTotalMs").GetDouble());
        Assert.Equal(3, body.GetProperty("stall").GetProperty("currentMinute").GetProperty("gcPauseMaxMs").GetDouble());
        Assert.Equal(200, body.GetProperty("resources").GetProperty("rssMb").GetDouble());
        Assert.Equal(2, body.GetProperty("workers").GetArrayLength());
    }

    private static CoordinationState Stage(params StageTally[] stages) => new(stages, [], [], [], []);
    [Fact]
    public void WingWaitsForMembersOnBothWorkersAndHonorsSpawnHold()
    {
        var coordinator = new ScenarioCoordinator(new(false, .15, 30), false);
        var key = new StageKey(0, 5, 0, 123);
        var a = Stage(new StageTally(key, 2, 2, 1, 3));
        var b = Stage(new StageTally(key, 2, 1, 1, 3));
        Assert.Empty(coordinator.Decide([a, b], 0, false).Releases);
        b = Stage(new StageTally(key, 2, 2, 1, 3));
        Assert.Equal(key, Assert.Single(coordinator.Decide([a, b], 0, false).Releases));
        Assert.Empty(coordinator.Decide([a, b], 0, true).Releases);
        // Removing a dead child's living members unblocks the surviving wing.
        Assert.Equal(key, Assert.Single(coordinator.Decide([a], 0, false).Releases));
    }

    [Theory]
    [InlineData(2500, 0, 0)]
    [InlineData(0, 1, 2)]
    public async Task SupervisorHoldsAnAssembledWingWhenLiveWorkerDecisionSnapshotIsStale(long age, long epoch, long required)
    {
        var key = new StageKey(0, 5, 0, 123);
        var delivered = new TaskCompletionSource<CoordinationDecisions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new FakeWorker(0)
        {
            Handler = method => Task.FromResult(method == "__state"
                ? Reply(new WorkerState(Stage(new StageTally(key, 2, 2, 30, 1)), Json(Array.Empty<object>()), age, epoch, required)) : Reply(true)),
            Observe = (method, parameters) => { if (method == "__decisions") delivered.TrySetResult(parameters.Deserialize<CoordinationDecisions>()!); },
        };
        using var supervisor = new WorkerSupervisor([worker]);
        var decision = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(decision.Releases);
        Assert.Empty(decision.Defense);
    }

    [Fact]
    public void SynchronizedWingsRespectObjectivesMissionsAndTimeout()
    {
        var coordinator = new ScenarioCoordinator(new(false, .15, 30), true);
        var first = new StageKey(0, 5, 0, 123);
        var second = new StageKey(0, 5, 1, 123);
        var a = Stage(new StageTally(first, 2, 2, 1, 1));
        var b = Stage(new StageTally(second, 2, 1, 1, 3), new(new(0, 8, 0, 123), 1, 0, 0, 1), new(new(0, 5, 0, 456), 1, 0, 0, 1));
        Assert.Empty(coordinator.Decide([a, b], 0, false).Releases);
        a = Stage(new StageTally(first, 2, 2, BotManager.StagingHoldSeconds + BotManager.SyncOffsetSeconds(0, 3, 1), 1));
        Assert.Equal(first, Assert.Single(coordinator.Decide([a, b], 0, false).Releases));
    }

    [Fact]
    public void RecallQuotaIsGlobalRankedOnceAndExpiresAfterQuietMinimum()
    {
        var coordinator = new ScenarioCoordinator(new(true, .15, 3), false);
        var key = new DefenseKey(123, 0, 0, 2);
        CoordinationState Side(string prefix, int hop) => new([], [key], [new(123, 0, 0, 10)],
            Enumerable.Range(0, 10).Select(i => new DefenseCandidate(key, prefix + i, (short)(hop * 10 + i), hop, i)).ToArray(), [new(key, 0)]);
        var a = Side("a", 2); var b = Side("b", 1);
        var deployment = Assert.Single(coordinator.Decide([a, b], 0, false).Defense);
        Assert.Equal(new[] { "b0", "b1", "b2" }, deployment.Names); // cap 3 for 20 bots, not cap 3 per process
        Assert.Equal(deployment.Names, Assert.Single(coordinator.Decide([a, b], 1, false).Defense).Names);
        a = a with { Threats = [] }; b = b with { Threats = [] };
        Assert.Single(coordinator.Decide([a, b], 29, false).Defense);
        Assert.Empty(coordinator.Decide([a, b], 31, false).Defense);
    }

    [Fact]
    public async Task RealChildExitMarksKnownBotsLostAndSupervisorStillServes()
    {
        if (OperatingSystem.IsWindows()) return;
        string log = Path.Combine(Path.GetTempPath(), $"p658-{Guid.NewGuid():N}.log");
        var info = new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("read request; printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"IsError\":false,\"Data\":{\"name\":\"child-bot\",\"side\":0,\"shipId\":7}}}'; echo child-error >&2; read request; exit 17");
        try
        {
            using var child = new ProcessWorker(info, 0, log);
            var healthy = new FakeWorker(1) { Handler = method => Task.FromResult(method == "list_active_bots" ? Reply(Array.Empty<object>()) : Reply(new[] { new { id = 15, name = "fighter" } })) };
            using var supervisor = new WorkerSupervisor([child, healthy], coordinate: false);
            Assert.False((await supervisor.HandleAsync("spawn_bot", null, CancellationToken.None)).IsError);
            var bots = Body(await supervisor.HandleAsync("list_active_bots", null, CancellationToken.None));
            var lost = bots.EnumerateArray().Single(b => b.TryGetProperty("name", out var name) && name.GetString() == "child-bot");
            Assert.Equal("lost", lost.GetProperty("status").GetString());
            Assert.False(child.Alive);
            Assert.False((await supervisor.HandleAsync("list_hulls", null, CancellationToken.None)).IsError);
            Assert.Contains("child-error", await File.ReadAllTextAsync(log));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void DisposingProcessWorkerKillsItsChildAndDrainsStderr()
    {
        if (OperatingSystem.IsWindows()) return;
        string log = Path.Combine(Path.GetTempPath(), $"p658-{Guid.NewGuid():N}.log");
        var info = new ProcessStartInfo("/bin/sh"); info.ArgumentList.Add("-c"); info.ArgumentList.Add("echo worker-start >&2; cat >/dev/null");
        try
        {
            using var worker = new ProcessWorker(info, 0, log);
            using var child = Process.GetProcessById(worker.Pid);
            worker.Dispose();
            Assert.True(child.WaitForExit(2000));
        }
        finally { File.Delete(log); }
    }
}

public sealed partial class BotFixRegressionTests
{
    [Fact]
    public void WorkerStagingUsesSupervisorReleaseAndRejectsHeldOrExpiredDecisions()
    {
        var manager = Manager();
        Set(manager, "<Partition>k__BackingField", new WorkerPartition(0, 2));
        var key = new StageKey(0, 4, 0, 123);
        manager.SetCoordination(new([key], []));
        Assert.True((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)123)!);
        Assert.False((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)456)!);
        manager.HoldCoordination(true);
        Assert.False((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)123)!);
        manager.HoldCoordination(false);
        // An in-flight response from before spawn completion cannot release a new wing.
        manager.SetCoordination(new([key], []));
        Assert.False((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)123)!);
        manager.SetCoordination(new([key], [], manager.CoordinationEpoch));
        Assert.True((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)123)!);
        Set(manager, "_coordinationReceived", Environment.TickCount64 - 3000);
        Assert.False((bool)Call("StageReleaseRequired", manager, (short)0, (short)4, 0, 0.0, null, (uint?)123)!);
    }

    [Trait("Requires", "Artwork")]   // loads PCore001.igc (artwork corpus)
    [Fact]
    public void WorkerAppliesGlobalRecallAndRestoresItsOriginalObjectiveWhenReleased()
    {
        using var reads = new DefenseLiveReads();
        using var session = Session(true);
        DefenseMap(session.World);
        var bot = Bot(session);
        Set(bot, "TargetSector", (short?)4);
        Set(bot, "RoutePath", new List<short> { 1, 2, 3, 4 });
        var manager = Manager(bot);
        Set(manager, "<Partition>k__BackingField", new WorkerPartition(0, 2));
        Set(manager, "_localThreats", new Dictionary<DefenseKey, double>());
        var key = (DefenseKey)Call("DefenseKeyFor", null, bot, (short)1)!;
        manager.SetCoordination(new([], [new(key, ["probe-bot"])]));
        DefensePass(manager, 0, bot);
        Assert.Equal((short?)1, Get<short?>(bot, "DefenseSector"));
        Assert.Equal((short?)4, Get<short?>(bot, "TargetSector"));
        manager.SetCoordination(CoordinationDecisions.Empty);
        DefensePass(manager, 1, bot);
        Assert.Null(Get<short?>(bot, "DefenseSector"));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, Get<List<short>>(bot, "RoutePath"));
    }

    [Fact]
    public void StopAndClearDisconnectRealPeersAndClearRemovalHistory()
    {
        using var telemetry = new StallWatchdog(start: false);
        using var session = Session(true);
        Set(Get<Allegiance.Core.Transport.TcpTransport>(session, "_transport"), "_running", true);
        var manager = Manager(Bot(session));
        Set(manager, "_stallTelemetry", telemetry);
        Assert.Equal(0, manager.StopBots("unknown", false));
        Assert.Equal(1, manager.StopBots("probe-bot", false));
        Assert.False(session.Connected);
        Assert.Single(manager.RemovedBots());
        Assert.Equal(0, manager.StopBots(null, true));
        Assert.Empty(manager.RemovedBots());
    }
}
