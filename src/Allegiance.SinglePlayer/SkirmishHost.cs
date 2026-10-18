using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Allegiance.Core;
using Srv = Allegiance.Server.Server;

namespace Allegiance.SinglePlayer;

/// <summary>Owns the embedded sim and its external bot supervisor for one local game.</summary>
public sealed class SkirmishHost : IDisposable
{
    private readonly Srv _server = new() { AllowSinglePlayerPause = true };
    private readonly CancellationTokenSource _stop = new();
    private readonly object _simGate = new();
    private readonly ProcessLifetime _ownership = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _driver;
    private Thread? _ticker;
    private Task? _reader, _stderr;
    private long _request, _heartbeat;
    private int _disposed;
    private string? _failure;
    private int[] _workerIds = [];
    private JsonElement _stats;
    private readonly object _statsGate = new();
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

    private SkirmishHost(SkirmishOptions options)
    {
        Options = options;
        Budget = SkirmishBudget.ForCores(Environment.ProcessorCount);
        DirectoryPath = Path.Combine(Path.GetTempPath(), "allegiance-skirmish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public static async Task<SkirmishHost> StartAsync(SkirmishOptions options, string? driverPath = null,
        string? artwork = null, CancellationToken cancellationToken = default)
    {
        var host = new SkirmishHost(options);
        try
        {
            var config = options.CreateConfig(artwork);
            await File.WriteAllTextAsync(Path.Combine(host.DirectoryPath, "server.json"), JsonSerializer.Serialize(config), cancellationToken);
            host._server.Boot(config);
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
                "--skirmish", $"{options.TeamSize - 1}/{options.TeamSize}" }) start.ArgumentList.Add(arg);
            // Inherited by all workers: bound the runtime's compute concurrency and GC heaps.
            start.Environment["DOTNET_PROCESSOR_COUNT"] = host.Budget.CoresPerWorker.ToString(CultureInfo.InvariantCulture);
            start.Environment["DOTNET_gcServer"] = "0";
            start.Environment["ALLEGIANCE_BOT_COMPUTE_THREADS"] = host.Budget.CoresPerWorker.ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_ARTWORK_PATH"] = config.ArtPath;
            var knobs = options.Knobs;
            start.Environment["ALLEGIANCE_BOT_ACCURACY"] = knobs.Accuracy.ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_BOT_REACTION"] = knobs.Reaction.ToString(CultureInfo.InvariantCulture);
            start.Environment["ALLEGIANCE_BOT_AGGRESSION"] = knobs.Aggression.ToString(CultureInfo.InvariantCulture);
            host._driver = Process.Start(start) ?? throw new IOException("Bot driver failed to start");
            host._ownership.Attach(host._driver);
            host._stderr = host.CopyLogAsync(host._driver.StandardError);
            host._reader = host.ReadAsync();
            // No worker may start before Windows job assignment has completed.
            await host._driver.StandardInput.WriteLineAsync("start");
            await host._driver.StandardInput.FlushAsync(cancellationToken);
            await host._ready.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return host;
        }
        catch { host.Dispose(); throw; }
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
                lock (_simGate) _server.Tick(); // Includes fixed cadence and automatic time dilation.
        }
        catch (Exception ex) { Fail("Embedded server stopped: " + ex.Message); }
    }

    private async Task CopyLogAsync(StreamReader source)
    {
        await using var log = File.CreateText(Path.Combine(DirectoryPath, "driver.log"));
        while (await source.ReadLineAsync() is { } line)
        {
            await log.WriteLineAsync(line);
            await log.FlushAsync();
        }
    }

    private void Fail(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.CompareExchange(ref _failure, message, null);
        _ready.TrySetException(new IOException(message));
        foreach (var (_, pending) in _pending) pending.TrySetResult(message);
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _driver!.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "ready":
                        _workerIds = root.GetProperty("pids").Deserialize<int[]>()!;
                        Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);
                        _ready.TrySetResult();
                        break;
                    case "health":
                        Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);
                        lock (_statsGate) _stats = root.GetProperty("stats").Clone();
                        if (!root.GetProperty("healthy").GetBoolean()) Fail("A bot worker stopped. Exit and start a new skirmish.");
                        break;
                    case "reply":
                        if (_pending.TryGetValue(root.GetProperty("id").GetInt64(), out var pending))
                            pending.TrySetResult(root.GetProperty("error").GetString());
                        break;
                }
            }
            Fail("Bot driver exited. Exit and start a new skirmish. Log: " + DirectoryPath);
        }
        catch (Exception ex) { Fail("Bot driver connection lost: " + ex.Message); }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
            lock (_simGate) _server.SetSinglePlayerPaused(paused);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var (_, pending) in _pending) pending.TrySetResult("Skirmish stopped");
        if (_driver is { } driver)
        {
            try
            {
                driver.StandardInput.Close();
                if (!driver.WaitForExit(5000)) { driver.Kill(entireProcessTree: true); driver.WaitForExit(5000); }
                Task.WaitAll(new[] { _reader ?? Task.CompletedTask, _stderr ?? Task.CompletedTask }, 5000);
            }
            catch (InvalidOperationException) { }
            catch (AggregateException) { }
            driver.Dispose();
        }
        _ownership.Dispose();
        _ticker?.Join();
        _server.Dispose();
        _stop.Dispose();
    }
}
