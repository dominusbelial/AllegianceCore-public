using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1072WebSocketBotsLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Fields)!.GetValue(value)!;

    internal sealed class Rig : IDisposable
    {
        internal readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        internal readonly object Gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _ticker;
        private Exception? _error;
        internal Rig(int tickSleep = 25, double cap = 0)
        {
            var config = P733ResearchTests.Config(8);
            config.BenchmarkSeed = 1072;
            config.Games[0].Name = "P1072 loopback";
            config.Games[0].CivIds = [25, 25];
            config.Games[0].CivSeed = 1072;
            config.Games[0].MaxTeamImbalance = 1;
            config.Games[0].StartCountdownSeconds = 1;
            config.Games[0].MapGen = new() { Type = "Brawl", Seed = 1072,
                PlayerSectorAsteroids = 0, PlayerSectorSpecialAsteroids = 0, PlayerSectorMineableAsteroids = 0,
                NeutralSectorAsteroids = 0, NeutralSectorSpecialAsteroids = 0, NeutralSectorMineableAsteroids = 0 };
            config.LiveSettings!.AutoNextMatch!.Enabled = true;
            config.LiveSettings.AutoNextMatch.IntermissionSeconds = 2;
            config.Showcase.MatchCapSeconds = cap;
            config.Reconnect = new() { Enabled = true, GraceSeconds = 30 };
            config.Transport = new() { MaxConnectionsPerIp = 256 };
            config.WebSocket = new() { Enabled = true, Port = 0, BindAddress = "127.0.0.1",
                AllowLoopbackDevelopment = true, AllowedOrigins = ["http://127.0.0.1"] };
            Server.Boot(config);
            _ticker = new Thread(() =>
            {
                try { while (!_stop.IsCancellationRequested) { lock (Gate) Server.TickSim(); Thread.Sleep(tickSleep); } }
                catch (Exception ex) { _error = ex; }
            }) { IsBackground = true, Name = "P1072-loopback-tick" };
            _ticker.Start();
        }
        internal async Task Until(Func<bool> done, double seconds = 30)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < seconds)
            { Assert.Null(_error); await Task.Delay(50); }
            Assert.Null(_error);
            Assert.True(done(), "P1072 loopback deadline exceeded");
        }
        internal BotTransportOptions Options(double fraction = 1) => new("ws", $"ws://127.0.0.1:{Server.WebSocketPort}/game", "http://127.0.0.1", fraction);
        internal void Drop(string name)
        {
            lock (Gate)
            {
                var names = Field<Dictionary<ConnectionId, string>>(Server, "_peerNames");
                var host = Field<ServerHost>(Server, "_host");
                host.Transport!.ClosePeer(names.Single(p => p.Value == name).Key, DisconnectReason.ReadErrorOrEof);
            }
        }
        public void Dispose()
        {
            _stop.Cancel(); Assert.True(_ticker.Join(10000)); Server.Dispose(); _stop.Dispose();
        }
    }

    private static async Task<(BotManager Manager, BackfillDriver Driver, CancellationTokenSource Stop, Task Driving)> Fleet(Rig rig, int seats, BotTransportOptions options)
    {
        var manager = new BotManager("127.0.0.1", rig.Server.BoundPort, backfill: true, transport: options);
        var stop = new CancellationTokenSource();
        var driver = new BackfillDriver(manager, seats);
        var driving = driver.RunAsync(stop.Token);
        try
        {
            await rig.Until(() => !driving.IsFaulted && manager.TransportClients.Length == seats * 2
                && manager.BackfillRoster(Allegiance.Core.Identity.BotNameRules.Defaults()).Pilots.Length == seats * 2
                && manager.ActiveBots().Count == (seats - 1) * 2, 60);
            Assert.False(driving.IsFaulted, driving.Exception?.ToString());
            return (manager, driver, stop, driving);
        }
        catch { stop.Cancel(); try { await driving; } catch (OperationCanceledException) { } manager.Dispose(); stop.Dispose(); throw; }
    }
    private static async Task StopFleet((BotManager Manager, BackfillDriver Driver, CancellationTokenSource Stop, Task Driving) fleet)
    {
        fleet.Stop.Cancel();
        try { await fleet.Driving; } catch (OperationCanceledException) { }
        finally { fleet.Manager.Dispose(); fleet.Stop.Dispose(); }
    }

    [SkippableFact]
    public async Task All_ws_2v2_fights_cap_ends_and_auto_next_starts()
    {
        Require.Artwork();
        using var rig = new Rig(10, 120);
        var fleet = await Fleet(rig, 2, rig.Options());
        try
        {
            var first = rig.Server.Missions[0];
            await rig.Until(() => fleet.Manager.GetCombatSummary().ShotsFired > 0, 45);
            Assert.All(fleet.Manager.TransportClients, p => Assert.Equal("ws", p.Session.ActiveTransportProtocol));
            await rig.Until(() => !ReferenceEquals(first, rig.Server.Missions[0])
                && rig.Server.Missions[0].Stage == MissionStage.Started, 90);
            Assert.Equal(MissionStage.Over, first.Stage);
            Assert.Equal(0, rig.Server.SimPassStageFailures);
            output.WriteLine($"All-ws 2v2: shots={fleet.Manager.GetCombatSummary().ShotsFired}; first={first.Stage}; next={rig.Server.Missions[0].Stage}");
        }
        finally { await StopFleet(fleet); }
    }

    [SkippableFact]
    public async Task Mixed_10v10_exact_split_reconnect_keeps_every_route()
    {
        Require.Artwork();
        string? saved = Environment.GetEnvironmentVariable("ALLEGIANCE_RECONNECT_GRACE_SECONDS");
        Environment.SetEnvironmentVariable("ALLEGIANCE_RECONNECT_GRACE_SECONDS", "10");
        try
        {
            using var rig = new Rig();
            var fleet = await Fleet(rig, 10, rig.Options(.5));
            try
            {
                var clients = fleet.Manager.TransportClients;
                var before = clients.ToDictionary(p => p.Name, p => p.Session.ActiveTransportProtocol);
                Assert.Equal(10, before.Values.Count(p => p == "ws"));
                Assert.Equal(10, before.Values.Count(p => p == "tcp"));
                // Drop one ordinary combat seat on each route, keeping probe/commanders alive.
                var names = fleet.Manager.ActiveBots().Select(b => b.Name).ToHashSet();
                var victims = new[] { "tcp", "ws" }.Select(protocol => clients.First(p => names.Contains(p.Name) && before[p.Name] == protocol)).ToArray();
                foreach (var victim in victims.Where(p => before[p.Name] == "ws"))
                {
                    victim.Session.WebSocketEndpoint = WebSocketEndpoint.FromConfiguration("ws://127.0.0.1:1/game");
                    victim.Session.WebSocketOrigin = "http://wrong.invalid";
                }
                for (int reconnect = 1; reconnect <= 2; reconnect++)
                {
                    foreach (var victim in victims) rig.Drop(victim.Name);
                    await rig.Until(() => victims.All(p => p.Session.ResumeSuccesses >= reconnect && p.Session.Connected && !p.Session.Reconnecting));
                }
                Assert.All(clients, p => Assert.Equal(before[p.Name], p.Session.ActiveTransportProtocol));
                await Task.Delay(1100); // let the normal one-second metric sample observe both resumes
                lock (rig.Gate) typeof(TickProfiler).GetMethod("ForceReport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                output.WriteLine($"Mixed: TCP={before.Values.Count(p => p == "tcp")} WS={before.Values.Count(p => p == "ws")}; resumed={victims.Length}");
                Assert.All(fleet.Manager.ActiveBots(), b => Assert.Contains(b.Transport, new[] { "tcp", "ws" }));
            }
            finally { await StopFleet(fleet); }
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_RECONNECT_GRACE_SECONDS", saved); }
    }

    // Default short regression; the lane A/B explicitly sets 300 seconds and
    // preserves the raw stdout/stderr. Server and bots share this lane process.
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Comparison_10v10(bool websocket)
    {
        Require.Artwork();
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("P1072_COMPARE_SECONDS"), out int duration) ? duration : 5;
        string? evidence = Environment.GetEnvironmentVariable("P1072_EVIDENCE_DIR");
        var original = Console.Out;
        var originalError = Console.Error;
        StreamWriter? file = evidence is null ? null : new(Path.Combine(evidence, websocket ? "ws.log" : "tcp.log")) { AutoFlush = true };
        if (file is not null) { var writer = TextWriter.Synchronized(file); Console.SetOut(writer); Console.SetError(writer); }
        try
        {
            using var rig = new Rig(50);
            var fleet = await Fleet(rig, 10, websocket ? rig.Options() : new());
            try
            {
                Assert.All(fleet.Manager.TransportClients, p => Assert.Equal(websocket ? "ws" : "tcp", p.Session.ActiveTransportProtocol));
                await rig.Until(() => fleet.Manager.GetCombatSummary().ShotsFired > 0, 90);
                lock (rig.Gate) typeof(TickProfiler).GetMethod("ForceReport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                Console.WriteLine($"[comparison-window] start seconds={seconds} protocol={(websocket ? "ws" : "tcp")}");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                lock (rig.Gate) typeof(TickProfiler).GetMethod("ForceReport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
                Console.WriteLine("[comparison-window] end");
                Assert.False(fleet.Driving.IsFaulted, fleet.Driving.Exception?.ToString());
                var combat = fleet.Manager.GetCombatSummary();
                Assert.True(combat.ShotsFired > 0, "comparison must contain real combat");
                Assert.Equal(0, rig.Server.SimPassStageFailures);
                output.WriteLine($"Lane comparison: ws={websocket} measuredSeconds={seconds} combat={JsonSerializer.Serialize(combat)}");
            }
            finally { await StopFleet(fleet); }
        }
        finally { Console.SetOut(original); Console.SetError(originalError); file?.Dispose(); }
    }
}
