using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P900FleetRecoveryLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Two_real_restarts_replay_fresh_map_civs_and_remaining_slots()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output, workers: 2);
        await run.Spawn(3, target: "1063");
        var before = await run.Inventory();
        string removed = before[0].GetProperty("name").GetString()!;
        await run.Call("despawn_bot", new { name = removed });
        await run.WaitFleet(f => f.GetProperty("requested").GetInt32() == 5);
        int expected = 5;
        for (int i = 0; i < 2; i++)
        {
            await run.KillServer();
            await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == expected);
            // Cross several bounded backoff steps, rather than restarting instantly.
            if (i == 0)
            {
                var canceled = await run.Call("despawn_bot", new { name = before[1].GetProperty("name").GetString() });
                Assert.Equal(1, canceled.GetProperty("stopped").GetInt32());
                expected--;
                await Task.Delay(8000);
            }
            await run.StartServer(i == 0 ? "m2xspiderw.igc" : "XXV_simple.igc", i == 0 ? [25, 3] : [1, 18]);
            var full = await run.WaitFleet(f => f.GetProperty("active").GetInt32() == expected && f.GetProperty("ok").GetBoolean());
            Assert.True(full.GetProperty("epoch").GetInt64() >= i + 1);
            var after = await run.Inventory();
            Assert.Equal(expected, after.Length);
            if (i == 0)
                Assert.All(after, b => Assert.True(b.GetProperty("targetSector").ValueKind == JsonValueKind.Null
                    || b.GetProperty("targetSector").GetInt16() != 1063, "Sedna/1063 belongs to the old map"));
            Assert.Contains(i == 0 ? "civIds [25, 3]" : "civIds [1, 18]", run.ServerLog());
            Assert.DoesNotContain(after, b => b.GetProperty("name").GetString() == removed);
            Assert.DoesNotContain(after, b => before.Any(old => old.GetProperty("name").GetString() == b.GetProperty("name").GetString()));
            await run.Eventually(() => run.Proxy.LiveNames.Count(n => n.StartsWith("research-cmd")) == 2);
            Assert.Single(run.Proxy.LiveNames, n => n.StartsWith("research-cmd0-"));
            Assert.Single(run.Proxy.LiveNames, n => n.StartsWith("research-cmd1-"));
            Assert.Equal(2, run.Proxy.LiveNames.Count(n => n.StartsWith("mcp-probe-")));
        }
    }

    [SkippableFact]
    public async Task Named_target_holds_on_the_same_map_and_yields_on_a_map_without_it()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output, workers: 2);
        await run.Spawn(2, target: "Sedna");
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 4);
        await run.StartServer("XXV_simple.igc", [1, 3]);
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 4 && f.GetProperty("ok").GetBoolean());
        Assert.All(await run.Inventory(), b => Assert.Equal(1063, b.GetProperty("targetSector").GetInt16()));

        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 4);
        await run.StartServer("m2xspiderw.igc", [25, 3]);
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 4 && f.GetProperty("ok").GetBoolean());
        Assert.All(await run.Inventory(), b => Assert.True(b.GetProperty("targetSector").ValueKind == JsonValueKind.Null
            || b.GetProperty("targetSector").GetInt16() != 1063, "Sedna is not on the new map"));
    }

    [SkippableFact]
    public async Task Deadline_exhaustion_is_visible_and_does_not_retry_forever()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output, deadline: 4);
        await run.Spawn(1);
        await run.KillServer();
        var failed = await run.WaitFleet(f => f.GetProperty("state").GetString() == "failed");
        Assert.Equal(2, failed.GetProperty("requested").GetInt32());
        Assert.Equal(2, failed.GetProperty("failed").GetInt32());
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("deadline exhausted", failed.GetProperty("failure").GetString());
        long attempts = run.Proxy.Attempts;
        await run.StartServer();
        await Task.Delay(2500);
        Assert.Equal(attempts, run.Proxy.Attempts);
        Assert.Empty(await run.Inventory());
    }

    [SkippableFact]
    public async Task Partial_respawn_reports_failed_slots_without_replaying_successes()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        await run.Spawn(2);
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 4);
        run.Proxy.RejectNextBot = true;
        await run.StartServer();
        var partial = await run.WaitFleet(f => f.GetProperty("state").GetString() == "degraded");
        Assert.Equal(4, partial.GetProperty("requested").GetInt32());
        Assert.Equal(3, partial.GetProperty("active").GetInt32());
        Assert.Equal(1, partial.GetProperty("failed").GetInt32());
        Assert.False(partial.GetProperty("ok").GetBoolean());
        long attempts = run.Proxy.Attempts;
        await Task.Delay(2500);
        Assert.Equal(attempts, run.Proxy.Attempts);
        Assert.Equal(3, (await run.Inventory()).Length);
    }

    [SkippableFact]
    public async Task Stop_and_clear_during_recovery_cancel_pending_slots_and_late_transport()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        foreach (string operation in new[] { "stop_bots", "clear_bots" })
        {
            await run.Spawn(1);
            await run.KillServer();
            await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 2);
            run.Proxy.HoldNextBot = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await run.StartServer();
            await run.Eventually(() => run.Proxy.HeldBot is not null);
            // A real handshake/spawn is in flight in the old process epoch.
            await run.Call(operation, new { });
            run.Proxy.HoldNextBot!.TrySetResult();
            var stopped = await run.WaitFleet(f => f.GetProperty("requested").GetInt32() == 0 && f.GetProperty("recovering").GetInt32() == 0);
            Assert.Equal("stopped", stopped.GetProperty("state").GetString());
            await Task.Delay(1500);
            Assert.Empty(await run.Inventory());
            Assert.DoesNotContain(run.Proxy.LiveNames, n => n.StartsWith("mcp-w", StringComparison.Ordinal));
            // A stopped supervisor can receive a new operator fleet request.
            await run.RestartDriver();
        }
    }

    [SkippableFact]
    public async Task Lone_bot_drop_does_not_rebuild_or_refill_it_on_later_server_restart()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        await run.Spawn(2);
        var initial = await run.Inventory();
        string name = initial[0].GetProperty("name").GetString()!;
        run.Proxy.Drop(name);
        var lone = await run.WaitFleet(f => f.GetProperty("requested").GetInt32() == 3 && f.GetProperty("active").GetInt32() == 3);
        Assert.Equal(0, lone.GetProperty("epoch").GetInt64());
        Assert.Equal(initial.Skip(1).Select(b => b.GetProperty("name").GetString()).Order(),
            (await run.Inventory()).Select(b => b.GetProperty("name").GetString()).Order());
        await run.KillServer();
        await run.StartServer();
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 3 && f.GetProperty("epoch").GetInt64() == 1);
        Assert.Equal(3, (await run.Inventory()).Length);
    }

    [SkippableFact]
    public async Task Lone_probe_drop_needs_fleet_loss_before_recovery()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        await run.Spawn(1);
        string probe = Assert.Single(run.Proxy.LiveNames, n => n.StartsWith("mcp-probe-"));
        run.Proxy.Drop(probe);
        await Task.Delay(2000);
        var stillActive = await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 2);
        Assert.Equal(0, stillActive.GetProperty("epoch").GetInt64());
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 2);
        await run.StartServer();
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 2 && f.GetProperty("epoch").GetInt64() == 1);
    }

    [SkippableFact]
    public async Task Multiple_requested_presets_each_replay_once()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        await run.Spawn(1);
        await run.Spawn(1, showcase: true);
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 4);
        await run.StartServer();
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 4 && f.GetProperty("ok").GetBoolean());
        Assert.Equal(4, (await run.Inventory()).Length);
    }

    [SkippableFact]
    public async Task Despawn_new_name_during_partial_replay_cancels_its_stable_slot()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output);
        await run.Spawn(2);
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 4);
        run.Proxy.HoldBotOrdinal = 2;
        run.Proxy.HoldNextBot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await run.StartServer();
        await run.Eventually(() => run.Proxy.HeldBot is not null);
        var fresh = Assert.Single(await run.Inventory());
        string name = fresh.GetProperty("name").GetString()!;
        var canceled = await run.Call("despawn_bot", new { name });
        Assert.Equal(1, canceled.GetProperty("stopped").GetInt32());
        run.Proxy.HoldNextBot!.TrySetResult();
        var full = await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 3 && f.GetProperty("ok").GetBoolean());
        Assert.Equal(3, full.GetProperty("requested").GetInt32());
        Assert.DoesNotContain(await run.Inventory(), b => b.GetProperty("name").GetString() == name);
    }

    [SkippableFact]
    public async Task Skirmish_shaped_exit_disables_recovery_and_reaps_workers()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output, skirmish: true);
        await run.KillServer();
        long attempts = run.Proxy.Attempts;
        var clock = Stopwatch.StartNew();
        await run.CloseDriverInput();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
        await Task.Delay(2000);
        Assert.Equal(attempts, run.Proxy.Attempts);
        Assert.Empty(run.Proxy.LiveNames);
    }

    [SkippableFact]
    public async Task One_hundred_showcase_bots_return_after_real_restart_with_paced_connections()
    {
        Require.Artwork();
        await using var run = await FleetRun.Start(output, workers: 2, deadline: 240);
        await run.Spawn(50, showcase: true);
        await run.KillServer();
        await run.WaitFleet(f => f.GetProperty("recovering").GetInt32() == 100);
        int start = run.Proxy.Greetings.Count;
        var watch = Stopwatch.StartNew();
        await run.StartServer();
        await run.WaitFleet(f => f.GetProperty("active").GetInt32() == 100 && f.GetProperty("ok").GetBoolean(), 240);
        double fullSeconds = watch.Elapsed.TotalSeconds;
        var greetings = run.Proxy.Greetings.Skip(start).Where(g => g.Name.StartsWith("mcp-w", StringComparison.Ordinal)).ToArray();
        Assert.Equal(100, greetings.Length);
        double spread = (greetings.Max(g => g.At) - greetings.Min(g => g.At)).TotalSeconds;
        Assert.True(spread > 1, $"100 joins must be spread, observed {spread}s");
        Assert.Equal(100, (await run.Inventory()).Length);
        run.Record(new { bots = 100, workers = 2, fullSeconds, spreadSeconds = spread,
            peakJoinsPer100Ms = greetings.GroupBy(g => g.At.Ticks / TimeSpan.TicksPerMillisecond / 100).Max(g => g.Count()) });
    }

    private sealed class FleetRun : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _work, _dotnet, _art;
        private readonly int _workers;
        private readonly double _deadline;
        private readonly bool _skirmish;
        private readonly int _serverPort;
        private Process? _server, _driver;
        private readonly List<Task> _logs = new();
        private int _id, _boot;
        internal readonly Forwarder Proxy;

        private FleetRun(ITestOutputHelper output, int workers, double deadline, bool skirmish)
        {
            _output = output; _workers = workers; _deadline = deadline; _skirmish = skirmish;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent!;
            _work = Path.Combine(root.FullName, ".scratch", "p900", "live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_work);
            _dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            _art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); _serverPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Proxy = new Forwarder(_serverPort);
            output.WriteLine("Evidence: " + _work);
        }
        internal static async Task<FleetRun> Start(ITestOutputHelper output, int workers = 1, double deadline = 90, bool skirmish = false)
        {
            var run = new FleetRun(output, workers, deadline, skirmish);
            try { await run.StartServer(); await run.RestartDriver(); return run; }
            catch { await run.DisposeAsync(); throw; }
        }
        private Process StartProcess(string assembly, params string[] args)
        {
            var info = new ProcessStartInfo(_dotnet) { WorkingDirectory = _work, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(assembly);
            foreach (string arg in args) info.ArgumentList.Add(arg);
            info.Environment[ExternalPaths.ArtworkEnvVar] = _art;
            info.Environment["DOTNET_gcServer"] = "1";
            info.Environment["DOTNET_PROCESSOR_COUNT"] = "4";
            info.Environment.Remove("ALLEGIANCE_RECONNECT_GRACE_SECONDS");
            info.Environment.Remove("ALLEGIANCE_SKIRMISH_COMMANDER");
            info.Environment.Remove("ALLEGIANCE_MCP_PROBE_SIDE_IDX");
            var process = Process.Start(info)!;
            _logs.Add(Drain(process.StandardError, Path.Combine(_work, $"{process.Id}-stderr.log")));
            return process;
        }
        private static async Task Drain(StreamReader reader, string path)
        {
            await using var file = new StreamWriter(path);
            while (await reader.ReadLineAsync() is { } line) { await file.WriteLineAsync($"{DateTime.UtcNow:O} {line}"); await file.FlushAsync(); }
        }
        internal async Task StartServer(string map = "XXV_simple.igc", int[]? civs = null)
        {
            string config = Path.Combine(_work, $"server-{++_boot}.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                artPath = _art, staticCore = "PCore001.igc", port = _serverPort, updatesPerSecond = 20,
                liveSettings = new { testHarness = new { enabled = false, money = 1000000, ignoreTechGate = false } },
                broadcast = new { immutableSnapshot = true, encodeOnce = true, parallelBuild = true, shipUpdateModel = "native", maxDegreeOfParallelism = 4 },
                transport = new { mode = "pool", poolWorkers = 4, maxConnections = 256, maxConnectionsPerIp = 256 },
                sim = new { nativeGotoPlan = true },
                showcase = new { techMode = "research", incomeMultiplier = 5000, researchTimeScale = 1 },
                games = new[] { new { name = "P900 loopback", map, teams = 2, civIds = civs ?? [1, 3], sideNames = new[] { "Alpha", "Bravo" }, maxPlayersPerTeam = 120, maxTeamImbalance = 32767 } }
            }));
            _server = StartProcess(typeof(Allegiance.Server.Server).Assembly.Location, "--config", config);
            _logs.Add(Drain(_server.StandardOutput, Path.Combine(_work, $"server-{_boot}.log")));
            await Eventually(async () =>
            {
                Assert.False(_server.HasExited, "server process exited");
                try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, _serverPort); return true; }
                catch (SocketException) { return false; }
            });
        }
        internal async Task KillServer()
        {
            Assert.NotNull(_server);
            _server.Kill(entireProcessTree: true); await _server.WaitForExitAsync(); _server.Dispose(); _server = null;
        }
        internal async Task RestartDriver()
        {
            if (_driver is not null) { if (!_driver.HasExited) _driver.Kill(entireProcessTree: true); await _driver.WaitForExitAsync(); _driver.Dispose(); }
            var args = new List<string> { "127.0.0.1", Proxy.Port.ToString(), "--workers", _workers.ToString(), "--recovery-deadline-seconds", _deadline.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (_skirmish) args.AddRange(["--skirmish", "1/1"]);
            _driver = StartProcess(typeof(BotManager).Assembly.Location, args.ToArray());
            if (_skirmish)
            {
                await _driver.StandardInput.WriteLineAsync("start");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                string? line = await _driver.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.Contains("\"ready\"", line);
            }
            else
            {
                await Rpc("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "p900", version = "1" } });
                await _driver.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            }
        }
        private async Task<JsonElement> Rpc(string method, object args)
        {
            int id = ++_id;
            await _driver!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = args }));
            await _driver.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            while (await _driver.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("id", out var response) || response.GetInt32() != id) continue;
                Assert.False(doc.RootElement.TryGetProperty("error", out _), line);
                return doc.RootElement.GetProperty("result").Clone();
            }
            throw new IOException("driver output closed; logs: " + _work);
        }
        internal async Task<JsonElement> Call(string name, object args)
        {
            var result = await Rpc("tools/call", new { name, arguments = args });
            return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        }
        internal async Task Spawn(int perSide, bool showcase = false, string? target = null)
        {
            int prior = (await Inventory()).Length;
            var args = new Dictionary<string, object> { ["refill"] = false, ["finale_after_minutes"] = 0 };
            if (showcase) { args["preset"] = "showcase"; args["count_a"] = perSide; args["count_b"] = perSide; }
            else { args["size"] = perSide; args["recover"] = true; }
            if (target is not null) args["target_sector"] = target;
            var result = await Call("spawn_scenario", args);
            Assert.Equal(perSide, result.GetProperty("sideA").GetProperty("spawned").GetInt32());
            Assert.Equal(perSide, result.GetProperty("sideB").GetProperty("spawned").GetInt32());
            await WaitFleet(f => f.GetProperty("active").GetInt32() == prior + perSide * 2);
        }
        internal string ServerLog() => File.ReadAllText(Path.Combine(_work, $"server-{_boot}.log"));
        internal async Task<JsonElement[]> Inventory() => (await Call("list_active_bots", new { })).EnumerateArray()
            .Where(b => b.TryGetProperty("status", out var s) && s.GetString() == "active").Select(b => b.Clone()).ToArray();
        internal async Task<JsonElement> WaitFleet(Func<JsonElement, bool> predicate, int seconds = 120)
        {
            JsonElement last = default;
            await Eventually(async () => { last = (await Call("get_perf_stats", new { })).GetProperty("fleet").Clone(); return predicate(last); }, seconds,
                () => last.ValueKind == JsonValueKind.Undefined ? "no fleet response" : last.GetRawText());
            _output.WriteLine(last.GetRawText());
            return last;
        }
        internal Task Eventually(Func<bool> predicate, int seconds = 40) => Eventually(() => Task.FromResult(predicate()), seconds);
        internal async Task Eventually(Func<Task<bool>> predicate, int seconds = 40, Func<string>? details = null)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < seconds) { if (await predicate()) return; await Task.Delay(150); }
            Assert.Fail("condition timeout: " + details?.Invoke() + "; " + _work);
        }
        internal async Task CloseDriverInput()
        {
            _driver!.StandardInput.Close();
            // Drain the skirmish's periodic health output while it exits.
            var output = _driver.StandardOutput.ReadToEndAsync();
            await _driver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await output;
        }
        internal void Record(object result)
        {
            string json = JsonSerializer.Serialize(result);
            _output.WriteLine(json);
            File.WriteAllText(Path.Combine(_work, "measurement.json"), json);
        }
        public async ValueTask DisposeAsync()
        {
            Proxy.HoldNextBot?.TrySetResult();
            foreach (var process in new[] { _driver, _server })
                if (process is not null)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(); process.Dispose();
                }
            await Proxy.DisposeAsync();
            await Task.WhenAll(_logs);
        }
    }

    // Byte-for-byte TCP forwarding. Only chosen real sockets are closed/held;
    // server boot, kill, logon, seating, purchase and restart are real processes.
    private sealed class Forwarder : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly int _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly Task _accept;
        private long _attempts;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal long Attempts => Interlocked.Read(ref _attempts);
        internal string[] LiveNames => _clients.Keys.ToArray();
        internal readonly ConcurrentQueue<(string Name, DateTime At)> Greetings = new();
        internal volatile bool RejectNextBot;
        private TaskCompletionSource? _holdNextBot;
        internal TaskCompletionSource? HoldNextBot
        {
            get => Volatile.Read(ref _holdNextBot);
            set { Interlocked.Exchange(ref _holdSeen, 0); Volatile.Write(ref _holdNextBot, value); }
        }
        internal string? HeldBot;
        internal int HoldBotOrdinal = 1;
        private int _holdSeen;
        internal Forwarder(int target) { _target = target; _listener.Start(); _accept = Accept(); }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) _connections.Add(Forward(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task Forward(TcpClient client)
        {
            string? name = null;
            using (client)
            using (var upstream = new TcpClient())
            try
            {
                client.NoDelay = upstream.NoDelay = true;
                byte[] header = new byte[5];
                await client.GetStream().ReadExactlyAsync(header, _stop.Token);
                int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1)));
                Assert.InRange(size, 1, 4096);
                byte[] body = new byte[size];
                await client.GetStream().ReadExactlyAsync(body, _stop.Token);
                name = Encoding.UTF8.GetString(body);
                Interlocked.Increment(ref _attempts);
                Greetings.Enqueue((name, DateTime.UtcNow));
                bool bot = name.StartsWith("mcp-w", StringComparison.Ordinal);
                if (bot && RejectNextBot) { RejectNextBot = false; return; }
                if (bot && HoldNextBot is { Task.IsCompleted: false } hold && Interlocked.Increment(ref _holdSeen) == HoldBotOrdinal)
                {
                    HeldBot = name;
                    await hold.Task.WaitAsync(_stop.Token);
                    HeldBot = null;
                }
                await upstream.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
                _clients[name] = client;
                await upstream.GetStream().WriteAsync(header, _stop.Token);
                await upstream.GetStream().WriteAsync(body, _stop.Token);
                var outbound = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                var inbound = upstream.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                await Task.WhenAny(outbound, inbound);
                client.Dispose(); upstream.Dispose();
                try { await Task.WhenAll(outbound, inbound); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { if (name is not null) _clients.TryRemove(name, out _); }
        }
        internal void Drop(string name) => _clients[name].Dispose();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            foreach (var client in _clients.Values) client.Dispose();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
