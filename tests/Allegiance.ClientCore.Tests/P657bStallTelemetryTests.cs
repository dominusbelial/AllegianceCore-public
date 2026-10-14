using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using ModelContextProtocol.Protocol;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.ClientCore.Tests;

public sealed class P657bStallTelemetryTests
{
    private sealed class Clock { internal double Now; internal double Read() => Now; }
    private static StallMetrics.RuntimeSample Runtime(double total = 100, double max = 0,
        long gen0 = 10, long gen1 = 5, long gen2 = 2, int threads = 4, long pending = 0, long completed = 20)
        => new(total, max, gen0, gen1, gen2, threads, pending, completed);

    [Fact]
    public void MinuteAggregatesAndResets_WhileReadersPreserveCompletedMinute()
    {
        var clock = new Clock();
        var metrics = new StallMetrics(clock.Read, Runtime());
        metrics.EndPass(12);
        clock.Now = 100;
        metrics.Observe(3, Runtime(112, 8, 12, 6, 2, 6, 4), new(12, 11, 2, 5200));
        metrics.EndPass(1250);
        clock.Now = 60000;
        var result = metrics.Observe(1100, Runtime(140, 20, 15, 7, 3, 8, 0, 50), new(12, 0, 0, 5));
        var minute = Assert.IsType<StallWindow>(result.Minute);
        Assert.Equal(60000, minute.DurationMs);
        Assert.Equal(1250, minute.DecisionPassMaxMs);
        Assert.Equal(20, minute.GcPauseMaxMs);
        Assert.Equal(40, minute.GcPauseTotalMs);
        Assert.Equal((5L, 2L, 1L), (minute.GcGen0, minute.GcGen1, minute.GcGen2));
        Assert.Equal(1100, minute.WatchdogLateMaxMs);
        Assert.Equal((11, 2), (minute.SessionsReadOver2sMax, minute.SessionsReadOver5sMax));
        Assert.Equal((8, 4L), (minute.ThreadPoolThreadsMax, minute.ThreadPoolPendingMax));
        Assert.True(result.Incident);
        Assert.Equal(minute, metrics.Snapshot().LastCompletedMinute);
        Assert.Equal(metrics.Snapshot(), metrics.Snapshot()); // Reads do not drain.
        var current = metrics.Snapshot().CurrentMinute;
        Assert.Equal(0, current.DecisionPassMaxMs);
        Assert.Equal(0, current.GcPauseMaxMs);
        Assert.Equal(0, current.GcPauseTotalMs);
        Assert.Equal(0, current.WatchdogLateMaxMs);
        Assert.Equal(0, current.SessionsReadOver2sMax);
        clock.Now = 120000;
        metrics.EndPass(9);
        var next = Assert.IsType<StallWindow>(metrics.Observe(2, Runtime(142, 2, 16, 7, 3), default).Minute);
        Assert.Equal(9, next.DecisionPassMaxMs);
        Assert.Equal(2, next.GcPauseTotalMs);
        Assert.Equal((1L, 0L, 0L), (next.GcGen0, next.GcGen1, next.GcGen2));
    }

    [Fact]
    public void DelayedMinuteUsesActualDuration_AndDoesNotReplayEmptyWindows()
    {
        var clock = new Clock();
        var metrics = new StallMetrics(clock.Read, Runtime());
        clock.Now = 185000;
        var result = metrics.Observe(184900, Runtime(300, 1500), default);
        Assert.Equal(185000, result.Minute!.DurationMs);
        Assert.Equal(200, result.Minute.GcPauseTotalMs);
        Assert.Equal(0, metrics.Snapshot().CurrentMinute.DurationMs);
        clock.Now = 185100;
        Assert.Null(metrics.Observe(0, Runtime(300), default).Minute);
    }

    [Theory]
    [InlineData(1000, 10, false)]
    [InlineData(1001, 0, true)]
    [InlineData(0, 11, true)]
    [InlineData(999, 9, false)]
    public void IncidentThresholdsAreStrict(double late, int starved, bool expected)
        => Assert.Equal(expected, StallMetrics.IsIncident(late, starved));

    [Fact]
    public void IncidentEpisodesAreRateLimited_AndRecoverImmediately()
    {
        var clock = new Clock();
        var metrics = new StallMetrics(clock.Read, Runtime());
        bool Observe(int starved) => metrics.Observe(0, Runtime(), new(12, starved, 0, 2500)).Incident;
        Assert.True(Observe(11));
        clock.Now = 100;
        Assert.False(Observe(11));
        clock.Now = 9999;
        Assert.False(Observe(11));
        clock.Now = 10000;
        Assert.True(Observe(11));
        Assert.False(Observe(0));
        clock.Now = 10100;
        Assert.True(Observe(11));
        Assert.Equal(3, metrics.Snapshot().IncidentCount);
    }

    [Fact]
    public void PoolStarvationRequiresBacklogAndNoCompletedWork_AndResetsOnProgress()
    {
        var clock = new Clock();
        var metrics = new StallMetrics(clock.Read, Runtime());
        metrics.Observe(0, Runtime(pending: 5), default);
        clock.Now = 1000;
        Assert.False(metrics.Observe(0, Runtime(pending: 5), default).Report.ThreadPoolStarvationSuspected);
        clock.Now = 1001;
        var starved = metrics.Observe(0, Runtime(pending: 5), default).Report;
        Assert.True(starved.ThreadPoolStarvationSuspected);
        Assert.Equal(1001, starved.ThreadPoolNoProgressMs);
        clock.Now = 1100;
        Assert.False(metrics.Observe(0, Runtime(pending: 5, completed: 21), default).Report.ThreadPoolStarvationSuspected);
        clock.Now = 1200;
        Assert.Equal(0, metrics.Observe(0, Runtime(pending: 0, completed: 21), default).Report.ThreadPoolNoProgressMs);
        Assert.Equal(1001, metrics.Snapshot().CurrentMinute.ThreadPoolNoProgressMaxMs);
    }

    [Fact]
    public void LossSnapshotPreservesWatchdogAgeAndIncidentState_AndShowsUnfinishedPass()
    {
        var clock = new Clock();
        var metrics = new StallMetrics(clock.Read, Runtime());
        metrics.BeginPass();
        clock.Now = 2100;
        Assert.True(metrics.Observe(2000, Runtime(), default).Incident);
        clock.Now = 7100;
        var loss = metrics.SnapshotAtLoss(Runtime(150, 25), new(20, 12, 11, 7000));
        Assert.Equal(5000, loss.WatchdogSampleAgeMs);
        Assert.Equal(2000, loss.WatchdogLateMs);
        Assert.Equal(7100, loss.DecisionPassInProgressMs);
        Assert.Equal(50, loss.CurrentMinute.GcPauseTotalMs);
        Assert.Equal(25, loss.CurrentMinute.GcPauseMaxMs);
        Assert.Equal(1, loss.IncidentCount);
        Assert.False(metrics.Observe(2000, Runtime(150), default).Incident);
        metrics.EndPass(7100);
        Assert.Equal(0, metrics.Snapshot().DecisionPassInProgressMs);
        Assert.Equal(7100, metrics.Snapshot().CurrentMinute.DecisionPassMaxMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstrumentedReadUsesFullFrameArrival_AndForwardsRealReceiveDispatcher(bool combat)
    {
        var clock = new Clock();
        var logs = new List<string>();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), logs.Add);
        using var client = combat ? (IDisposable)new ConnectionSession() : new BotClass();
        var observed = watchdog.Track((ITransportSite)client, "read-bot");
        var transport = combat ? ObservedSession.CombatTransport((ConnectionSession)client) :
            ObservedSession.PlainTransport((BotClass)client);
        Assert.Same(observed.Transport, transport); // Accessor binding exercised without sockets.
        clock.Now = 2000;
        Assert.Equal(0, watchdog.ReceiveSnapshot().Over2Seconds);
        clock.Now = 5000;
        Assert.Equal(1, watchdog.ReceiveSnapshot().Over2Seconds);
        Assert.Equal(0, watchdog.ReceiveSnapshot().Over5Seconds);
        clock.Now = 5001;
        Assert.Equal(1, watchdog.ReceiveSnapshot().Over5Seconds);
        observed.OnReceive(ConnectionId.Server, FedPacket.Pack(new BuyLoadoutAck { HullId = 15, FLaunch = true }.Encode()));
        Assert.Equal(0, observed.ReadAgeMs);
        var acks = combat ? ((ConnectionSession)client).BuyLoadoutAcks : ((BotClass)client).BuyLoadoutAcks;
        Assert.Equal((short)15, Assert.Single(acks).HullId);
        watchdog.Close(client);
        watchdog.Close(client);
        Assert.Equal(0, watchdog.ReceiveSnapshot().Sessions);
        Assert.Contains("reason=our-disposal", Assert.Single(logs));
    }

    [Fact]
    public void FakeClockWatchdogLogsLateWakeupAndMinute_WithoutCatchupOrReaderReset()
    {
        var clock = new Clock();
        var logs = new List<string>();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), logs.Add);
        clock.Now = 100;
        watchdog.Tick();
        Assert.Empty(logs);
        clock.Now = 1201;
        watchdog.Tick();
        Assert.Contains("trigger=watchdog-late", Assert.Single(logs));
        Assert.Equal(1001, watchdog.Metrics.Snapshot().CurrentMinute.WatchdogLateMaxMs);
        clock.Now = 1301;
        watchdog.Tick();
        Assert.Single(logs);
        clock.Now = 60000;
        watchdog.Tick();
        Assert.Single(logs, l => l.StartsWith("[stall-minute]"));
        Assert.NotNull(watchdog.Metrics.Snapshot().LastCompletedMinute);
        Assert.Equal(0, watchdog.Metrics.Snapshot().CurrentMinute.WatchdogLateMaxMs);
    }

    [Fact]
    public void WatchdogLogsReceiveStarvation_WithFreshLossSnapshotAndNoDuplicateLosses()
    {
        var clock = new Clock();
        var logs = new List<string>();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), logs.Add);
        var clients = Enumerable.Range(0, 11).Select(_ => new ConnectionSession()).ToArray();
        var sessions = clients.Select((c, i) => watchdog.Track(c, $"bot-{i}")).ToArray();
        for (int i = 1; i <= 21; i++) { clock.Now = i * 100; watchdog.Tick(); }
        Assert.Contains("trigger=receive-starvation", Assert.Single(logs));
        ObservedSession.ReadFailure(sessions[0].Transport) = "server closed connection (EOF)";
        clock.Now = 5200;
        sessions[0].OnSessionLost();
        sessions[0].OnSessionLost();
        Assert.True(clients[0].Lost);
        var loss = Assert.Single(logs, l => l.StartsWith("[session-loss]"));
        Assert.Contains("reason=server-eof", loss);
        using var doc = JsonDocument.Parse(loss[(loss.IndexOf("snapshot=", StringComparison.Ordinal) + 9)..]);
        Assert.Equal(11, doc.RootElement.GetProperty("sessionsReadOver5s").GetInt32());
        Assert.Equal(3100, doc.RootElement.GetProperty("watchdogSampleAgeMs").GetDouble());
        Assert.Equal(10, watchdog.ReceiveSnapshot().Sessions);
        foreach (var client in clients) watchdog.Close(client);
    }

    [Theory]
    [InlineData("server closed connection (EOF)", false, "server-eof")]
    [InlineData("read I/O error", false, "local-transport-error")]
    [InlineData("connection disposed", true, "our-disposal")]
    public void LossReasonUsesActualTransportState(string detail, bool intentional, string reason)
    {
        var clock = new Clock();
        var logs = new List<string>();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), logs.Add);
        var client = new ConnectionSession();
        var observed = watchdog.Track(client, "quoted\n\"bot");
        ObservedSession.ReadFailure(observed.Transport) = detail;
        ObservedSession.IntentionalShutdown(observed.Transport) = intentional;
        observed.OnSessionLost();
        watchdog.Close(client);
        var line = Assert.Single(logs);
        Assert.Contains($"reason={reason}", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void LocalReceiveExceptionIsLoggedOnce_WithTypeAndDetail()
    {
        var clock = new Clock();
        var logs = new List<string>();
        var client = new ThrowingClient();
        var observed = new ObservedSession(client, "broken", clock.Read,
            (_, reason, detail) => logs.Add($"{reason} {detail}"));
        Assert.Throws<InvalidOperationException>(() => observed.OnReceive(ConnectionId.Server, []));
        observed.OnSessionLost();
        Assert.Equal("local-exception InvalidOperationException: decode failed", Assert.Single(logs));
        observed.Transport.Shutdown();
    }

    private sealed class ThrowingClient : ITransportSite
    {
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => throw new InvalidOperationException("decode failed");
        public void OnSessionLost() { }
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
    }

    [Fact]
    public void PendingSpawnRollbackDisposesAndUntracksSession_WithOneReason()
    {
        var clock = new Clock();
        var logs = new List<string>();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), logs.Add);
        Assert.Throws<TimeoutException>((Action)(() =>
        {
            using var pending = new PendingOwnership(watchdog.Close);
            var client = pending.Own(new ConnectionSession());
            watchdog.Track(client, "failed-spawn");
            throw new TimeoutException("launch timed out");
        }));
        Assert.Equal(0, watchdog.ReceiveSnapshot().Sessions);
        Assert.Contains("reason=our-disposal", Assert.Single(logs));
    }

    [Fact]
    public void UnavailableLogWriterDoesNotInterruptClientCleanup_AndReportsDroppedLines()
    {
        var clock = new Clock();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(),
            _ => throw new IOException("err.log unavailable"));
        var client = new ConnectionSession();
        watchdog.Track(client, "bot");
        watchdog.Close(client);
        Assert.Equal(0, watchdog.ReceiveSnapshot().Sessions);
        Assert.Equal(1, watchdog.Snapshot().LogDroppedLines);
    }

    [Fact]
    public void DedicatedWatchdogHasHighestPriority_AndLoggerFlushesOnShutdown()
    {
        using var sampled = new ManualResetEventSlim();
        using var logged = new ManualResetEventSlim();
        int samples = 0;
        Thread? sampleThread = null, logThread = null;
        bool logIsPoolThread = true;
        using var watchdog = new StallWatchdog(runtime: () =>
        {
            if (Interlocked.Increment(ref samples) > 1 && Thread.CurrentThread.Name == "allegiance-mcp-stall-watchdog")
            {
                sampleThread = Thread.CurrentThread;
                sampled.Set();
            }
            return Runtime();
        }, write: _ =>
        {
            logThread = Thread.CurrentThread;
            logIsPoolThread = logThread.IsThreadPoolThread;
            logged.Set();
        });
        Assert.True(sampled.Wait(TimeSpan.FromSeconds(2)), "Dedicated watchdog did not tick");
        Assert.Equal("allegiance-mcp-stall-watchdog", sampleThread!.Name);
        Assert.Equal(ThreadPriority.Highest, sampleThread.Priority);
        Assert.False(sampleThread.IsThreadPoolThread);
        var client = new ConnectionSession();
        watchdog.Track(client, "shutdown-bot");
        // Dispose closes remaining sessions and drains their loss log via a
        // separate dedicated thread; neither ThreadPool work nor TCP is needed.
        watchdog.Dispose();
        Assert.True(logged.IsSet);
        Assert.Equal("allegiance-mcp-stall-log", logThread!.Name);
        Assert.False(logIsPoolThread);
        Assert.False(sampleThread.IsAlive);
        Assert.False(logThread.IsAlive);
    }

    [Fact]
    public void GetPerfStatsExposesMinuteAndLiveStallFields_AndReadsDoNotResetThem()
    {
        var clock = new Clock();
        using var watchdog = new StallWatchdog(false, clock.Read, () => Runtime(), _ => { });
        watchdog.Metrics.EndPass(1500);
        clock.Now = 60000;
        watchdog.Metrics.Observe(1200, Runtime(170, 40, 13, 6, 3, 16, 42), new(390, 24, 18, 8000));
        var manager = (BotManager)RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
        void Set(string field, object value) => typeof(BotManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, value);
        Set("_gate", new object());
        Set("_metricsGate", new object());
        Set("_passTimesMs", new List<double>());
        Set("_removedBots", new List<BotManager.RemovedBotView>());
        Set("_stallTelemetry", watchdog);
        Set("_cadence", new DecisionMetrics.LoopCadence(50));
        var botType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
        Set("_bots", (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(botType))!);
        var args = new Dictionary<string, JsonElement> { ["bot"] = JsonSerializer.SerializeToElement("does-not-match") };
        var response = McpTools.Handle("get_perf_stats", args, manager);
        Assert.False(response.IsError);
        using var doc = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
        var stall = doc.RootElement.GetProperty("stall");
        var minute = stall.GetProperty("lastCompletedMinute");
        Assert.Equal(1500, minute.GetProperty("decisionPassMaxMs").GetDouble());
        Assert.Equal(40, minute.GetProperty("gcPauseMaxMs").GetDouble());
        Assert.Equal(70, minute.GetProperty("gcPauseTotalMs").GetDouble());
        Assert.Equal(3, minute.GetProperty("gcGen0").GetInt64());
        Assert.Equal(1, minute.GetProperty("gcGen1").GetInt64());
        Assert.Equal(1, minute.GetProperty("gcGen2").GetInt64());
        Assert.Equal(16, stall.GetProperty("threadPoolThreads").GetInt32());
        Assert.Equal(42, stall.GetProperty("threadPoolPending").GetInt64());
        Assert.False(stall.GetProperty("threadPoolStarvationSuspected").GetBoolean());
        Assert.Equal(1200, minute.GetProperty("watchdogLateMaxMs").GetDouble());
        Assert.Equal(24, stall.GetProperty("sessionsReadOver2s").GetInt32());
        Assert.Equal(18, stall.GetProperty("sessionsReadOver5s").GetInt32());
        Assert.Equal(390, stall.GetProperty("sessions").GetInt32()); // Bot filter does not hide process stalls.
        Assert.Equal(0, stall.GetProperty("currentMinute").GetProperty("gcPauseTotalMs").GetDouble());
        var second = McpTools.Handle("get_perf_stats", args, manager);
        Assert.Equal(Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text,
            Assert.IsType<TextContentBlock>(Assert.Single(second.Content)).Text);
    }
}
