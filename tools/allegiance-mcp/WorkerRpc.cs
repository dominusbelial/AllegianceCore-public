using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Allegiance.Tools.McpSpawner;

internal sealed record WorkerReply(bool IsError, JsonElement Data)
{
    internal static WorkerReply FromTool(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        JsonElement data;
        try { data = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { data = JsonSerializer.SerializeToElement(new { error = text }); }
        return new(result.IsError == true, data);
    }
}
internal sealed record WorkerState(CoordinationState Coordination, JsonElement Bots, long SampleAgeMs = 0, long Epoch = 0, long RequiredEpoch = 0);
internal interface IWorker : IDisposable
{
    int Index { get; }
    int Pid { get; }
    bool Alive { get; }
    string? Failure { get; }
    Task<WorkerReply> CallAsync(string method, JsonElement parameters, CancellationToken ct);
}

/// <summary>One multiplexed JSON-RPC pipe pair per child; stdout has only protocol data.</summary>
internal sealed class ProcessWorker : IWorker
{
    private readonly Process _process;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerReply>> _pending = new();
    private readonly Task _reader, _stderr;
    private long _next;
    private string? _failure;
    private int _disposed;
    private readonly EventHandler _exit;
    public int Index { get; }
    public int Pid { get; }
    public bool Alive => Volatile.Read(ref _failure) is null && !_process.HasExited;
    public string? Failure => Volatile.Read(ref _failure);

    internal static ProcessWorker Start(string host, ushort port, int index, int count)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        // dotnet app.dll and the published apphost both launch this exact assembly.
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { host, port.ToString(), "--worker", $"{index + 1}/{count}", "--parent", Environment.ProcessId.ToString() }) info.ArgumentList.Add(arg);
        return new(info, index, Path.Combine(Environment.CurrentDirectory, $"err.worker-{index + 1}.log"));
    }

    internal ProcessWorker(ProcessStartInfo info, int index, string stderrPath)
    {
        Index = index;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.UseShellExecute = false;
        // Open first: a log-path error must not leak a started process. Unbuffered
        // (bufferSize 1): each copied chunk reaches the file at once, so a crashed or
        // killed supervisor still leaves the worker's last stderr on disk.
        var log = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        try { _process = Process.Start(info) ?? throw new IOException("worker failed to start"); }
        catch { log.Dispose(); throw; }
        Pid = _process.Id;
        _stderr = Task.Run(async () => { await using (log) await _process.StandardError.BaseStream.CopyToAsync(log); });
        _reader = ReadAsync();
        _exit = (_, _) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += _exit;
    }

    private void Fail(string reason)
    {
        if (Interlocked.CompareExchange(ref _failure, reason, null) is null)
        {
            // A broken protocol pipe must not leave untracked bots fighting.
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        foreach (var (id, pending) in _pending)
            if (_pending.TryRemove(id, out _)) pending.TrySetException(new IOException(Failure));
    }
    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var response = document.RootElement;
                long id = response.GetProperty("id").GetInt64();
                if (!_pending.TryRemove(id, out var pending)) continue;
                if (response.TryGetProperty("error", out var error)) pending.TrySetException(new IOException(error.ToString()));
                else pending.TrySetResult(response.GetProperty("result").Deserialize<WorkerReply>()!);
            }
            Fail("worker stdout closed (process exited or pipe lost)");
        }
        catch (Exception ex) { Fail($"worker protocol failed: {ex.Message}"); }
    }

    public async Task<WorkerReply> CallAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (!Alive) throw new IOException(Failure ?? "worker exited");
        long id = Interlocked.Increment(ref _next);
        var pending = new TaskCompletionSource<WorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await _write.WaitAsync(ct);
            try
            {
                if (!Alive) throw new IOException(Failure ?? "worker exited");
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), ct);
                await _process.StandardInput.FlushAsync(ct);
            }
            finally { _write.Release(); }
            return await pending.Task.WaitAsync(ct);
        }
        catch (IOException ex) { Fail(ex.Message); throw; }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= _exit;
        Fail("supervisor stopped worker");
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { _process.WaitForExit(2000); Task.WaitAll([_reader, _stderr], 2000); } catch (AggregateException) { }
        _process.Dispose();
    }

    internal static async Task ServeAsync(BotManager manager, CancellationToken ct)
    {
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        using var writes = new SemaphoreSlim(1);
        using var tools = new SemaphoreSlim(1);
        var requests = new List<Task>();
        try
        {
            while (await Console.In.ReadLineAsync(ct) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement.Clone();
                requests.RemoveAll(t => t.IsCompleted);
                requests.Add(Task.Run(async () =>
                {
                    long id = request.GetProperty("id").GetInt64();
                    string method = request.GetProperty("method").GetString()!;
                    var args = request.GetProperty("params");
                    object response;
                    try
                    {
                        WorkerReply result;
                        switch (method)
                        {
                            case "__focus": manager.SetFocusSector(args.ValueKind == JsonValueKind.Null ? null : args.GetInt16()); result = new(false, JsonSerializer.SerializeToElement(true, BotFocusJson.Default.Boolean)); break;
                            case "__pause": manager.SetPaused(args.GetBoolean()); result = new(false, JsonSerializer.SerializeToElement(true)); break;
                            case "__ping": result = new(false, JsonSerializer.SerializeToElement(new { pid = Environment.ProcessId })); break;
                            case "__hold": manager.HoldCoordination(args.GetBoolean()); result = new(false, JsonSerializer.SerializeToElement(true)); break;
                            case "__decisions": manager.SetCoordination(args.Deserialize<CoordinationDecisions>()!); result = new(false, JsonSerializer.SerializeToElement(true)); break;
                            case "__state":
                                result = new(false, JsonSerializer.SerializeToElement(manager.WorkerSnapshot(WorkerReply.FromTool(McpTools.Handle("list_active_bots", null, manager)).Data))); break;
                            default:
                                await tools.WaitAsync(ct);
                                try
                                {
                                    if (method == "__perf")
                                    {
                                        var report = WorkerReply.FromTool(McpTools.Handle("get_perf_stats", args.Deserialize<Dictionary<string, JsonElement>>(), manager));
                                        string? filter = args.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.String ? bot.GetString() : null;
                                        result = new(report.IsError, JsonSerializer.SerializeToElement(new { Report = report.Data, Cadence = manager.CadenceSamples(filter) }));
                                    }
                                    else
                                    {
                                        manager.PlanOnly = method == "__plan";
                                        result = WorkerReply.FromTool(McpTools.Handle(manager.PlanOnly ? "spawn_scenario" : method, args.Deserialize<Dictionary<string, JsonElement>>(), manager));
                                    }
                                }
                                finally { manager.PlanOnly = false; tools.Release(); }
                                break;
                        }
                        response = new { jsonrpc = "2.0", id, result };
                    }
                    catch (Exception ex) { response = new { jsonrpc = "2.0", id, error = new { code = -32603, message = ex.Message } }; }
                    await writes.WaitAsync(ct);
                    try { await output.WriteLineAsync(JsonSerializer.Serialize(response)); }
                    finally { writes.Release(); }
                }, ct));
            }
        }
        finally { manager.HoldCoordination(true); }
        // The process owns all tasks and peers. No wait for long spawn calls after EOF.
    }
}
