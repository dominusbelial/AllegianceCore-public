using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Allegiance.Tools.McpSpawner;

internal sealed class WorkerSupervisor : IDisposable
{
    private readonly IWorker[] _workers;
    private readonly ScenarioCoordinator _coordinator;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _mutation = new(1);
    private readonly object _gate = new();
    private readonly Dictionary<int, JsonElement[]> _knownBots = new();
    private readonly Task _coordination;
    private int _spawning, _next, _disposed;
    private static readonly HashSet<string> ToolNames = McpTools.Definitions(supervisor: true).Select(t => t.Name).ToHashSet();
    private static readonly JsonElement EmptyArgs = JsonSerializer.SerializeToElement(new { });

    internal WorkerSupervisor(IEnumerable<IWorker> workers, bool coordinate = true)
    {
        _workers = workers.ToArray();
        _coordinator = new(BotManager.DefenseSettings.Read(Environment.GetEnvironmentVariable),
            string.Equals(Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SYNC_WINGS"), "on", StringComparison.OrdinalIgnoreCase));
        _coordination = coordinate ? CoordinateAsync() : Task.CompletedTask;
    }
    internal static async Task<WorkerSupervisor> StartAsync(string host, ushort port, int count, CancellationToken ct)
    {
        var workers = new List<IWorker>();
        try
        {
            for (int i = 0; i < count; i++) workers.Add(ProcessWorker.Start(host, port, i, count));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await Task.WhenAll(workers.Select(w => w.CallAsync("__ping", EmptyArgs, timeout.Token)));
            return new(workers);
        }
        catch { foreach (var worker in workers) worker.Dispose(); throw; }
    }

    private async Task<WorkerReply> SafeCall(IWorker worker, string method, JsonElement args, CancellationToken ct)
    {
        try { return await worker.CallAsync(method, args, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(true, JsonSerializer.SerializeToElement(new { worker = worker.Index + 1, error = ex.Message })); }
    }
    private async Task<WorkerReply[]> FanOut(string method, JsonElement args, CancellationToken ct) =>
        await Task.WhenAll(_workers.Select(w => SafeCall(w, method, args, ct)));

    private async Task CoordinateAsync()
    {
        var lastDecisions = CoordinationDecisions.Empty;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(1000);
                // Each worker updates independently. A stalled worker must not freeze healthy ones.
                var current = await Task.WhenAll(_workers.Select(async w =>
                {
                    try
                    {
                        var reply = await SafeCall(w, "__state", EmptyArgs, timeout.Token);
                        if (reply.IsError) return (WorkerState?)null;
                        var state = reply.Data.Deserialize<WorkerState>()!;
                        lock (_gate) Remember(w.Index, state.Bots);
                        return state;
                    }
                    catch (OperationCanceledException) { return null; }
                }));
                // A live but unresponsive worker may contain unstaged members. Hold
                // fleet releases and new recalls until its state is known again.
                bool incomplete = _workers.Where(w => w.Alive).Any(w => current[Array.IndexOf(_workers, w)] is not { SampleAgeMs: < 2000 });
                bool oldEpoch = current.OfType<WorkerState>().Any(s => s.Epoch != s.RequiredEpoch);
                // A spawn epoch transition fences staging without interrupting an
                // existing defence deployment. Missing/stale state fails closed.
                var decisions = incomplete ? CoordinationDecisions.Empty : oldEpoch ? lastDecisions with { Releases = [] }
                    : _coordinator.Decide(current.OfType<WorkerState>().Select(s => s.Coordination),
                        Environment.TickCount64 / 1000.0, Volatile.Read(ref _spawning) > 0);
                if (!incomplete && !oldEpoch) lastDecisions = decisions;
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                sendTimeout.CancelAfter(1000);
                await Task.WhenAll(_workers.Where(w => w.Alive).Select(async w =>
                {
                    try { await SafeCall(w, "__decisions", JsonSerializer.SerializeToElement(decisions with { Epoch = current[Array.IndexOf(_workers, w)]?.Epoch ?? -1 }), sendTimeout.Token); }
                    catch (OperationCanceledException) { }
                }));
                await Task.Delay(200, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[supervisor] coordination error: {ex.Message}"); await Task.Delay(200, _stop.Token); }
        }
    }
    private void Remember(int index, JsonElement bots)
    {
        if (bots.ValueKind == JsonValueKind.Array) _knownBots[index] = bots.EnumerateArray().Select(b => b.Clone()).ToArray();
    }
    private object[] WorkerStatus() => _workers.Select(w => (object)new { worker = w.Index + 1, pid = w.Pid, status = w.Alive ? "running" : "lost", error = w.Failure }).ToArray();
    private JsonElement[] LostBots()
    {
        lock (_gate) return _workers.Where(w => !w.Alive).SelectMany(w => _knownBots.GetValueOrDefault(w.Index) ?? [])
            .Where(b => !b.TryGetProperty("status", out var status) || status.GetString() == "active")
            .Select(b =>
            {
                var row = JsonNode.Parse(b.GetRawText())!.AsObject();
                row["status"] = "lost"; row["reason"] = "worker process lost";
                return JsonSerializer.SerializeToElement(row);
            }).ToArray();
    }
    private static CallToolResult Result(JsonElement data, bool error = false) => new()
    {
        IsError = error, Content = [new TextContentBlock { Text = data.GetRawText() }],
    };

    internal async Task<CallToolResult> HandleAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (!ToolNames.Contains(name)) return Result(JsonSerializer.SerializeToElement(new { error = $"unknown tool '{name}'" }), true);
        var args = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>());
        bool mutating = name is not ("list_active_bots" or "get_perf_stats" or "list_hulls" or "list_parts" or "list_drones");
        if (mutating) await _mutation.WaitAsync(ct);
        try
        {
            if (name == "spawn_scenario") return await SpawnScenarioAsync(args, ct);
            if (name is "list_active_bots" or "list_drones")
            {
                var replies = await FanOut(name, args, ct);
                var rows = new List<JsonElement>();
                for (int i = 0; i < replies.Length; i++)
                {
                    if (replies[i].Data.ValueKind != JsonValueKind.Array) continue;
                    if (name == "list_active_bots") lock (_gate) Remember(_workers[i].Index, replies[i].Data);
                    rows.AddRange(replies[i].Data.EnumerateArray());
                }
                if (name == "list_active_bots") rows.AddRange(LostBots());
                else rows = rows.DistinctBy(r => r.GetRawText()).ToList();
                // Include a worker row even if its last inventory was empty.
                if (name == "list_active_bots") rows.AddRange(_workers.Where(w => !w.Alive).Select(w => JsonSerializer.SerializeToElement(new { worker = w.Index + 1, pid = w.Pid, status = "lost", reason = w.Failure })));
                return Result(JsonSerializer.SerializeToElement(rows), replies.All(r => r.IsError));
            }
            if (name == "get_perf_stats")
            {
                var replies = await FanOut("__perf", args, ct);
                return Result(SupervisorResults.MergePerf(replies, WorkerStatus(), LostBots()), replies.All(r => r.IsError));
            }
            if (name is "stop_bots" or "clear_bots" or "despawn_bot")
            {
                var replies = await FanOut(name, args, ct);
                string key = name == "clear_bots" ? "cleared" : "stopped";
                if (name == "clear_bots") lock (_gate) _knownBots.Clear();
                return Result(JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    [key] = replies.Sum(r => r.Data.TryGetProperty(key, out var value) ? value.GetInt32() : 0),
                    ["workers"] = WorkerStatus(), ["failures"] = replies.Where(r => r.IsError).Select(r => r.Data).ToArray(),
                }), replies.Any(r => r.IsError));
            }
            var live = _workers.Where(w => w.Alive).ToArray();
            if (live.Length == 0) return Result(JsonSerializer.SerializeToElement(new { error = "all workers lost", workers = WorkerStatus() }), true);
            var chosen = live[name == "spawn_bot" ? (Interlocked.Increment(ref _next) - 1) % live.Length : 0];
            var reply = await SafeCall(chosen, name, args, ct);
            if (name == "spawn_bot" && !reply.IsError && reply.Data.TryGetProperty("name", out _))
                lock (_gate)
                {
                    var row = JsonNode.Parse(reply.Data.GetRawText())!.AsObject(); row["status"] = "active";
                    _knownBots[chosen.Index] = (_knownBots.GetValueOrDefault(chosen.Index) ?? []).Append(JsonSerializer.SerializeToElement(row)).ToArray();
                }
            return Result(reply.Data, reply.IsError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Result(JsonSerializer.SerializeToElement(new { error = ex.Message, workers = WorkerStatus() }), true); }
        finally { if (mutating) _mutation.Release(); }
    }

    private async Task<CallToolResult> SpawnScenarioAsync(JsonElement args, CancellationToken ct)
    {
        // Preflight all original plans before spawning; retain each child's exact
        // slot counts so an exit during spawn becomes failed slots, never success.
        var plans = await FanOut("__plan", args, ct);
        if (plans.Where((p, i) => p.IsError && _workers[i].Alive).Any() || plans.All(p => p.IsError))
            return Result(JsonSerializer.SerializeToElement(new { failures = plans.Where(p => p.IsError).Select(p => p.Data), workers = WorkerStatus() }), true);
        var template = plans.First(p => !p.IsError).Data;
        for (int i = 0; i < plans.Length; i++)
            if (plans[i].IsError)
            {
                var totals = template.GetProperty("totals").EnumerateArray().ToArray();
                object Side(int sideIndex) => new { side = totals[sideIndex].GetProperty("side").GetInt16(),
                    requested = WorkerPartition.Split(totals[sideIndex].GetProperty("requested").GetInt32(), _workers.Length)[_workers[i].Index] };
                plans[i] = new(false, JsonSerializer.SerializeToElement(new { sideA = Side(0), sideB = Side(1) }));
            }
        Interlocked.Increment(ref _spawning);
        try
        {
            var held = await FanOut("__hold", JsonSerializer.SerializeToElement(true), ct);
            if (held.Where((r, i) => r.IsError && _workers[i].Alive).Any()) return Result(JsonSerializer.SerializeToElement(new { failures = held.Where(r => r.IsError).Select(r => r.Data), workers = WorkerStatus() }), true);
            var replies = await FanOut("spawn_scenario", args, ct);
            // Refresh inventories to catch disconnects during the other children's
            // slower spawns, and record bots for crash reporting between tool calls.
            var states = await Task.WhenAll(_workers.Select(async w =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(2000);
                try { return await SafeCall(w, "__state", EmptyArgs, timeout.Token); }
                catch (OperationCanceledException) { return new WorkerReply(true, JsonSerializer.SerializeToElement(new { error = "worker state timed out" })); }
            }));
            for (int i = 0; i < states.Length; i++)
            {
                if (!states[i].IsError) lock (_gate) Remember(_workers[i].Index, states[i].Data.Deserialize<WorkerState>()!.Bots);
                else if (replies[i].Data.TryGetProperty("grants", out var grants)) lock (_gate)
                {
                    var known = grants.EnumerateArray().Select(g => { var row = JsonNode.Parse(g.GetRawText())!.AsObject(); row["status"] = "active"; return JsonSerializer.SerializeToElement(row); });
                    _knownBots[_workers[i].Index] = (_knownBots.GetValueOrDefault(_workers[i].Index) ?? []).Concat(known).ToArray();
                }
            }
            var merged = SupervisorResults.MergeScenario(plans, replies, states, WorkerStatus());
            return Result(merged, merged.GetProperty("sideA").GetProperty("spawned").GetInt32()
                + merged.GetProperty("sideB").GetProperty("spawned").GetInt32() == 0);
        }
        finally
        {
            Interlocked.Decrement(ref _spawning);
            using var timeout = new CancellationTokenSource(2000);
            try { await FanOut("__hold", JsonSerializer.SerializeToElement(false), timeout.Token); }
            catch (OperationCanceledException) { }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var worker in _workers) worker.Dispose();
        try { _coordination.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
