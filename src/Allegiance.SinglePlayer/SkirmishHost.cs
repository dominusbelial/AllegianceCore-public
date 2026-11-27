using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Allegiance.Core;
using Srv = Allegiance.Server.Server;

namespace Allegiance.SinglePlayer;

/// <summary>Owns the embedded sim and its external bot supervisor for one local game.</summary>
public sealed partial class SkirmishHost : IDisposable
{
    private readonly Srv _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _simGate = new();
    private readonly ProcessLifetime _ownership = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _driver;
    private Thread? _ticker;
    private Task? _reader, _stderr;
    private InternetHostPublisher? _internet;
    private long _request, _heartbeat;
    private int _disposed;
    private readonly object _stopGate = new();
    private Task? _stopping;
    private Action[] _workerExits = [];
    private string? _failure;
    private int[] _workerIds = [];
    private int _focusSector = -1;
    public void SetFocusSector(short? sector) => Volatile.Write(ref _focusSector, sector ?? -1);
    private JsonElement _stats;
    private bool _decisionWarning;
    private readonly object _statsGate = new();
    private readonly List<double> _benchmarkTicks = [];
    private bool _measureTicks;
    public void BeginMeasurement() { lock (_benchmarkTicks) { _benchmarkTicks.Clear(); _measureTicks = true; } }
    public double[] TickSamples { get { lock (_benchmarkTicks) return _benchmarkTicks.ToArray(); } }
    public SkirmishOptions Options { get; }
    public SkirmishBudget Budget { get; }
    public ushort Port => _server.BoundPort;
    public bool Paused => _server.SinglePlayerPaused;
    public double SimulationSeconds => _server.SimNow;
    public string DirectoryPath { get; }
    public string? Failure => Volatile.Read(ref _failure)
        ?? (_ready.Task.IsCompletedSuccessfully && Environment.TickCount64 - Interlocked.Read(ref _heartbeat) > 15000
            ? "Bot driver has not responded for 15 seconds" : null);
    public bool BotsAlive => _ready.Task.IsCompletedSuccessfully && _disposed == 0 && Failure is null;
    public int[] BotProcessIds => _driver is { } driver ? new[] { driver.Id }.Concat(_workerIds).ToArray() : [];
    public JsonElement Stats { get { lock (_statsGate) return _stats; } }
    internal Srv Server => _server;
    internal T OnSimulation<T>(Func<Srv, T> action) { lock (_simGate) return action(_server); }

    private SkirmishHost(SkirmishOptions options, string directory)
    {
        Options = options;
        _server = new() { AllowSinglePlayerPause = !options.HostLan, RequireLanListeners = options.HostLan, RequireUdpIntroductions = options.HostInternet };
        PlayerSide = options.StartAsSpectator ? null : (short)0;
        // P889 — size the bot driver from the same shared-machine budget the
        // server resolver uses (client + bot reserve), not a flat "cores - 2".
        Budget = SkirmishBudget.ForOptions(options, Environment.ProcessorCount);
        DirectoryPath = directory;
    }

    public static Task<SkirmishHost> StartAsync(SkirmishOptions options, string? driverPath = null,
        string? artwork = null, CancellationToken cancellationToken = default, string commanderPilotName = "skirmish-test-pilot")
        => StartInDirectoryAsync(options, Path.Combine(Path.GetTempPath(), SkirmishRunFiles.Prefix + Guid.NewGuid().ToString("N")),
            driverPath, artwork, cancellationToken, commanderPilotName);

    internal static async Task<SkirmishHost> StartInDirectoryAsync(SkirmishOptions options, string directory,
        string? driverPath = null, string? artwork = null, CancellationToken cancellationToken = default,
        string commanderPilotName = "skirmish-test-pilot")
    {
        var host = new SkirmishHost(options, directory);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var config = options.CreateConfig(artwork);
            // A LAN game's password never reaches the temp folder; Boot takes the config below.
            if (!options.HostLan)
                await File.WriteAllTextAsync(Path.Combine(host.DirectoryPath, "server.json"), JsonSerializer.Serialize(config, Benchmark.BenchmarkJson.Default.ServerConfig), cancellationToken);
            host._server.TickMeasured += ms => { lock (host._benchmarkTicks) if (host._measureTicks && host._benchmarkTicks.Count < 100000) host._benchmarkTicks.Add(ms); };
            try { host._server.Boot(config); }
            catch (System.Net.Sockets.SocketException ex) when (options.HostLan)
            { throw new IOException($"LAN game port {options.HostPort} (TCP/UDP) is unavailable. Close the other host or choose another port.", ex); }
            host._ticker = new Thread(host.Tick) { IsBackground = true, Name = "skirmish-server", Priority = ThreadPriority.BelowNormal };
            host._ticker.Start();
            var start = new ProcessStartInfo(driverPath ?? FindDriver())
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = host.DirectoryPath, CreateNoWindow = true
            };
            if (start.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string dll = start.FileName;
                start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
                start.ArgumentList.Add(dll);
            }
            foreach (var arg in new[] { "127.0.0.1", host.Port.ToString(CultureInfo.InvariantCulture),
                "--workers", host.Budget.Workers.ToString(CultureInfo.InvariantCulture), "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--skirmish", $"{options.TeamSize - (options.StartAsSpectator ? 0 : 1)}/{options.TeamSize}" }) start.ArgumentList.Add(arg);
            // Inherited by all workers: bound the runtime's compute concurrency and GC heaps.
            start.Environment["DOTNET_PROCESSOR_COUNT"] = host.Budget.CoresPerWorker.ToString(CultureInfo.InvariantCulture);
            start.Environment["DOTNET_gcServer"] = "0";
            // The two sides have two live-world gates. More preparation threads
            // contend for those gates; leave the runtime budget available to I/O.
            start.Environment["ALLEGIANCE_BOT_COMPUTE_THREADS"] = Math.Min(2, host.Budget.CoresPerWorker).ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_ARTWORK_PATH"] = config.ArtPath;
            start.Environment["ALLEGIANCE_BOT_LOD"] = Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_LOD") ?? "on";
            start.Environment["ALLEGIANCE_LOCAL_PILOT"] = WirePilotName(commanderPilotName);
            // Private child environment: never inherited from another local match.
            start.Environment["ALLEGIANCE_SKIRMISH_COMMANDER"] = options.Commander == SkirmishCommander.You ? WirePilotName(commanderPilotName) : "";
            if (options.HostLan)
            {
                // Child-only configuration: never grant privilege from an inherited developer shell.
                start.Environment.Remove("ALLEGIANCE_PRIVILEGE_SECRET");
                start.Environment.Remove("ALLEGIANCE_BENCHMARK_METRICS");
                start.Environment["ALLEGIANCE_LAN_PASSWORD"] = options.Password;
            }
            if (options.Seed is not null) start.Environment["ALLEGIANCE_BENCHMARK_METRICS"] = "1";
            var knobs = options.Knobs;
            start.Environment["ALLEGIANCE_BOT_ACCURACY"] = knobs.Accuracy.ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_BOT_REACTION"] = knobs.Reaction.ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_BOT_AGGRESSION"] = knobs.Aggression.ToString(CultureInfo.InvariantCulture);
            // P814 — item 4c: bot turret gunners ON by default; the setup screen
            // toggle reaches the driver through this env var (GunnerPolicy reads it).
            start.Environment["ALLEGIANCE_GUNNERS"] = options.BotGunners ? "on" : "off";
            host._driver = Process.Start(start) ?? throw new IOException("Bot driver failed to start");
            host._ownership.Attach(host._driver);
            host._stderr = SkirmishRunFiles.DrainLogAsync(host._driver.StandardError.BaseStream,
                () => File.Create(Path.Combine(host.DirectoryPath, "driver.log")));
            host._reader = host.ReadAsync();
            // No worker may start before Windows job assignment has completed.
            await host._driver.StandardInput.WriteLineAsync("start");
            await host._driver.StandardInput.FlushAsync(cancellationToken);
            await host._ready.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            if (options.HostInternet)
                host._internet = await InternetHostPublisher.StartAsync(host._server.UdpGameTransport!, options.InternetLobbyUrl,
                    options.GameName, host.Port, options.SeatCount + 12, () => host._server.LivePlayerCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return host;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref host._failure, ex.Message, null);
            await host.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    // LOGON_REQUEST trims trailing spaces and encodes Latin-1; roster names
    // are fixed at 24 characters (clintlib's native player-name representation).
    internal static string WirePilotName(string name)
    {
        string wire = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.Latin1.GetBytes(name)).TrimEnd(' ');
        return wire[..Math.Min(24, wire.Length)];
    }

    public static string FindDriver()
    {
        string name = OperatingSystem.IsWindows() ? "allegiance-mcp.exe" : "allegiance-mcp";
        string bundled = Path.Combine(AppContext.BaseDirectory, "bot-driver", name);
        if (File.Exists(bundled)) return bundled;
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var root = new DirectoryInfo(origin); root is not null; root = root.Parent)
                if (File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx")))
                    foreach (var configuration in new[] { "Debug", "Release" })
                    {
                        string path = Path.Combine(root.FullName, "tools", "allegiance-mcp", "bin", configuration, "net10.0", name);
                        if (File.Exists(path)) return path;
                    }
        throw new FileNotFoundException("Skirmish bot driver missing. Reinstall the player bundle or build tools/allegiance-mcp.");
    }

    private void Tick()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Work (incl. automatic time dilation) under the lock; the cadence
                // sleep outside it, so the observer's per-frame Observe never waits
                // out the idle part of a tick.
                double cost;
                lock (_simGate) cost = _server.TickWork();
                _server.TickCadence(cost);
            }
        }
        catch (Exception ex) { Fail("Embedded server stopped: " + ex.Message); }
    }

    private void Fail(string message)
    {
        lock (_stopGate)
        {
            if (_disposed != 0) return;
            Interlocked.CompareExchange(ref _failure, message, null);
        }
        _ready.TrySetException(new IOException(message));
        foreach (var (_, pending) in _pending) pending.TrySetResult(message);
    }

    // The heartbeat's healthy bit tracks the driver process and RPC. A decision
    // streak is recoverable and must not fault a pending command or the match.
    internal void ReadHealth(JsonElement root)
    {
        Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);
        var stats = root.GetProperty("stats").Clone();
        lock (_statsGate) _stats = stats;
        if (!root.GetProperty("healthy").GetBoolean())
        {
            Fail("A bot worker stopped. Exit and start a new skirmish.");
            return;
        }

        int worst = 0;
        if (stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("decisionFailures", out var failures))
        {
            worst = failures.GetProperty("consecutiveWorkerPasses").GetInt32();
            foreach (var bot in failures.GetProperty("bots").EnumerateArray())
                worst = Math.Max(worst, bot.GetProperty("consecutiveBotPasses").GetInt32());
        }
        bool degraded = worst >= 3;
        if (degraded && !_decisionWarning)
            Console.Error.WriteLine($"[skirmish] Bot decisions degraded ({worst} consecutive failed passes). See worker logs.");
        _decisionWarning = degraded;
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _driver!.StandardOutput.ReadLineAsync() is { } line)
            {
                if (_stop.IsCancellationRequested) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "ready" when !_ready.Task.IsCompleted:
                        _workerIds = root.GetProperty("pids").Deserialize<int[]>()!;
                        _workerExits = _workerIds.Select(ProcessLifetime.CaptureWorkerExit).ToArray();
                        Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);
                        _ready.TrySetResult();
                        break;
                    case "health":
                        ReadHealth(root);
                        await _writes.WaitAsync(_stop.Token);
                        try
                        {
                            int focus = Volatile.Read(ref _focusSector);
                            var command = new SkirmishFocusCommand(Interlocked.Increment(ref _request), "focus", focus < 0 ? null : (short)focus);
                            await _driver!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, SkirmishProtocolContext.Default.SkirmishFocusCommand).AsMemory(), _stop.Token);
                            await _driver.StandardInput.FlushAsync(_stop.Token);
                        }
                        finally { _writes.Release(); }
                        break;
                    case "reply":
                        if (_pending.TryGetValue(root.GetProperty("id").GetInt64(), out var pending))
                            pending.TrySetResult(root.GetProperty("error").GetString());
                        break;
                }
            }
            Fail("Bot driver exited. Exit and start a new skirmish. Log: " + DirectoryPath);
        }
        catch (Exception ex)
        {
            Fail("Bot driver connection lost: " + ex.Message);
            // A broken protocol or a cancelled focus write does not close the pipe.
            try { await _driver!.StandardOutput.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
            catch (IOException) { } // The read end itself has failed.
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Options.HostLan) throw new InvalidOperationException("LAN games cannot be paused.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        long id = Interlocked.Increment(ref _request);
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await _writes.WaitAsync(ct);
            try
            {
                await _driver!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, command = "pause", paused }).AsMemory(), ct);
                await _driver.StandardInput.FlushAsync(ct);
            }
            finally { _writes.Release(); }
            var error = await reply.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (error is not null) throw new IOException(error);
            lock (_simGate)
            {
                ct.ThrowIfCancellationRequested();
                _server.SetSinglePlayerPaused(paused);
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    public Task StopAsync()
    {
        lock (_stopGate)
        {
            if (_stopping is not null) return _stopping;
            Interlocked.Exchange(ref _disposed, 1);
            string? failure = Failure;
            return _stopping = Task.Run(() => Stop(failure));
        }
    }

    private void Stop(string? failure)
    {
        _stop.Cancel();
        _internet?.Dispose();
        if (Options.HostLan)
        {
            _ticker?.Join();
            lock (_simGate) _server.EndLanHost();
            // Bound drain time for the guaranteed departure on TCP and UDP.
            Thread.Sleep(250);
        }
        foreach (var (_, pending) in _pending) pending.TrySetResult("Skirmish stopped");
        if (_driver is { } driver)
        {
            // Closing a redirected writer can itself block on a full pipe. Keep
            // the grace period independent of it and of any in-flight write.
            var closeInput = Task.Run(() =>
            {
                _writes.Wait();
                try { driver.StandardInput.Close(); }
                catch (IOException) { } // Already closed by the child.
                finally { _writes.Release(); }
            });
            if (!driver.WaitForExit(5000))
            {
                try { driver.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (driver.HasExited) { }
                catch (System.ComponentModel.Win32Exception ex)
                { Console.Error.WriteLine("[skirmish] waiting for driver after tree kill failed: " + ex.Message); }
                driver.WaitForExit();
            }
            _ownership.WaitForChildren(driver.Id);
            _ownership.Dispose();
            try { Task.WhenAll(closeInput, _reader ?? Task.CompletedTask, _stderr ?? Task.CompletedTask).GetAwaiter().GetResult(); }
            catch (IOException ex) { Console.Error.WriteLine("[skirmish] driver pipe closed: " + ex.Message); }
            foreach (var waitForExit in _workerExits) waitForExit();
            driver.Dispose();
        }
        else _ownership.Dispose();
        _ticker?.Join();
        _seatChanges.Wait();
        try { lock (_simGate) _server.Dispose(); }
        finally { _seatChanges.Release(); }
        _stop.Dispose();
        SkirmishRunFiles.Finish(DirectoryPath, failure);
    }
}
