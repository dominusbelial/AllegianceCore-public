using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class WorkerSupervisor
{
    private sealed class DesiredScenario(long id, JsonElement arguments, bool recover)
    {
        internal long Id = id;
        internal JsonElement Arguments = arguments;
        internal bool Recover = recover;
        internal long OperatorRevision;
        internal Dictionary<int, Dictionary<string, FleetSlot>> Slots = new();
    }
    private readonly List<DesiredScenario> _desired = new();
    private Func<int, IWorker>? _workerFactory;
    private bool _recoveryEnabled;
    private TimeSpan _recoveryDeadline = TimeSpan.FromMinutes(15);
    private CancellationTokenSource? _recoveryCancel;
    private long _fleetEpoch, _scenarioSequence;
    private long _operatorRevision;
    private string _fleetState = "idle";
    private string? _fleetFailure;
    private volatile bool _recovering;
    private long _probeLossAt;
    private int _recoveryAttempt;
    private readonly Dictionary<(long Scenario, int Worker, string Key), long> _lostSlots = new();

    private DesiredScenario PrepareDesired(JsonElement args)
    {
        bool recover = _recoveryEnabled && (args.TryGetProperty("recover", out var explicitRecovery)
            ? explicitRecovery.GetBoolean() : args.TryGetProperty("preset", out var preset) && preset.GetString() == "showcase");
        var clean = JsonNode.Parse(args.GetRawText())!.AsObject();
        clean.Remove("__fleet_id"); clean.Remove("__fleet_slots");
        lock (_gate) return new(Interlocked.Increment(ref _scenarioSequence), JsonSerializer.SerializeToElement(clean), recover) { OperatorRevision = _operatorRevision };
    }
    private JsonElement ScenarioArguments(DesiredScenario scenario, bool replay, int? worker = null)
    {
        var args = JsonNode.Parse(scenario.Arguments.GetRawText())!.AsObject();
        args["__fleet_id"] = scenario.Id;
        if (worker is { } index)
        {
            lock (_gate) args["__fleet_slots"] = JsonSerializer.SerializeToNode(scenario.Slots.GetValueOrDefault(index)?.Keys.ToArray() ?? []);
        }
        if (replay)
        {
            // Explicit map overrides and minefield coordinates belong to the old
            // map. Re-derive objectives from the fresh side's observations.
            args.Remove("target_sector");
            if (args["minefields"] is JsonObject fields)
            {
                fields["at"] = "rendezvous";
                fields["__fresh_map"] = true;
            }
        }
        return JsonSerializer.SerializeToElement(args);
    }
    private bool RegisterDesired(DesiredScenario scenario, WorkerReply[] plans)
    {
        lock (_gate)
        {
            if (scenario.OperatorRevision != _operatorRevision) return false;
            for (int i = 0; i < plans.Length; i++)
                if (!plans[i].IsError && plans[i].Data.TryGetProperty("slots", out var slots))
                    scenario.Slots[_workers[i].Index] = slots.Deserialize<FleetSlot[]>()!
                        .Where(s => s.Scenario == scenario.Id).ToDictionary(s => s.Key);
                else if (!plans[i].IsError)
                {
                    var missing = new Dictionary<string, FleetSlot>();
                    foreach (var (sideKey, prefix) in new[] { ("sideA", "a"), ("sideB", "b") })
                    {
                        var side = plans[i].Data.GetProperty(sideKey);
                        for (int n = 0; n < side.GetProperty("requested").GetInt32(); n++)
                            missing[$"{prefix}:{n}"] = new(scenario.Id, $"{prefix}:{n}", side.GetProperty("side").GetInt16(), null, "failed");
                    }
                    scenario.Slots[_workers[i].Index] = missing;
                }
            _desired.Add(scenario);
            Interlocked.Increment(ref _spawning);
            SetFleetState("starting");
            return true;
        }
    }
    private void SetFleetState(string state, string? failure = null)
    {
        // Caller holds _gate. Exactly one diagnostic per state transition.
        if (_fleetState == state && _fleetFailure == failure) return;
        _fleetState = state;
        _fleetFailure = failure;
        Console.Error.WriteLine($"[fleet] epoch={_fleetEpoch} state={state}" + (failure is null ? "" : $" reason={failure}"));
    }
    internal object FleetStatus()
    {
        lock (_gate)
        {
            var slots = _desired.SelectMany(s => s.Slots).SelectMany(w => w.Value.Values.Select(s =>
                s with { Status = _workers.Any(worker => worker.Index == w.Key && worker.Alive) ? s.Status : "failed" })).ToArray();
            int active = _recovering ? 0 : slots.Count(s => s.Status == "active");
            int recovering = _recovering ? slots.Length : 0;
            return new { state = _fleetState, epoch = _fleetEpoch, requested = slots.Length, active, recovering,
                failed = slots.Length - active - recovering, ok = Healthy && !_recovering && slots.Length == active && _fleetFailure is null,
                processHealthy = Healthy, failure = _fleetFailure,
                retryAttempt = _recoveryAttempt, deadlineSeconds = _recoveryDeadline.TotalSeconds,
                sides = slots.GroupBy(s => s.Side).Select(g => new { side = g.Key, requested = g.Count(),
                    active = _recovering ? 0 : g.Count(s => s.Status == "active") }).ToArray() };
        }
    }
    private object WorkerFleetStatus(int index)
    {
        lock (_gate)
        {
            var slots = _desired.SelectMany(s => s.Slots.GetValueOrDefault(index)?.Values.ToArray() ?? []).ToArray();
            int active = _recovering || !_workers.Any(w => w.Index == index && w.Alive) ? 0 : slots.Count(s => s.Status == "active");
            int recovering = _recovering ? slots.Length : 0;
            return new { requested = slots.Length, active, recovering, failed = slots.Length - active - recovering };
        }
    }
    private async Task<(int Worker, FleetSlot Slot)[]> ResolveRecoveryNameAsync(JsonElement args, CancellationToken ct)
    {
        if (!_recovering || !args.TryGetProperty("name", out var value)) return [];
        string? name = value.GetString();
        long epoch = Interlocked.Read(ref _fleetEpoch);
        var workers = _workers.ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1500);
        try
        {
            var states = await Task.WhenAll(workers.Select(w => SafeCall(w, "__state", EmptyArgs, timeout.Token)));
            lock (_gate)
            {
                if (_fleetEpoch != epoch || !_recovering) return [];
                var matches = new List<(int Worker, FleetSlot Slot)>();
                for (int i = 0; i < states.Length; i++)
                    if (!states[i].IsError)
                        foreach (var slot in states[i].Data.Deserialize<WorkerState>()!.Fleet ?? [])
                            if (slot.Name == name) matches.Add((workers[i].Index, slot));
                return matches.ToArray();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
    }
    private int CancelDesired(string method, JsonElement args, (int Worker, FleetSlot Slot)[] observed)
    {
        lock (_gate)
        {
            string? name = method == "despawn_bot" && args.TryGetProperty("name", out var value) ? value.GetString() : null;
            if (method == "despawn_bot" && name is null) return 0;
            int canceled = 0;
            foreach (var scenario in _desired)
                foreach (var (worker, slots) in scenario.Slots)
                    foreach (var key in slots.Where(s => name is null || (observed.Length == 0 ? s.Value.Name == name :
                        observed.Any(row => row.Worker == worker && row.Slot.Scenario == scenario.Id && row.Slot.Key == s.Key)))
                        .Select(s => s.Key).ToArray())
                    { slots.Remove(key); canceled++; }
            _desired.RemoveAll(s => s.Slots.Values.All(slots => slots.Count == 0));
            // Kill in-flight processes before waiting for the mutation lock.
            // This also fences a spawn that has not returned its name yet.
            if (_recovering && (canceled > 0 || method is "clear_bots" or "stop_bots")) _recoveryCancel?.Cancel();
            if (method is "clear_bots" or "stop_bots")
            {
                _operatorRevision++;
                _desired.Clear();
                SetFleetState("stopped");
                if (Volatile.Read(ref _spawning) > 0 && !_recovering)
                {
                    _fleetEpoch++;
                    foreach (var worker in _workers) worker.Dispose();
                }
            }
            return canceled;
        }
    }
    private async Task<bool> ObserveFleetAsync(WorkerState?[] states)
    {
        bool loss;
        lock (_gate)
        {
            if (_fleetState == "failed") return false;
            // A probe transport loss is the authority. One fleet bot disappearing
            // with live probes follows the existing remove path and is canceled.
            // A half-second lets independently delivered TCP loss callbacks settle.
            bool probeLost = states.Any(s => s?.ProbeLost == true);
            if (probeLost && _probeLossAt == 0) _probeLossAt = Environment.TickCount64;
            if (!probeLost) _probeLossAt = 0;
            bool fleetLoss = states.Select((state, i) => (state, i)).Any(item =>
                item.state?.Fleet?.Any(row => row.Status == "connection lost" &&
                    _desired.FirstOrDefault(s => s.Id == row.Scenario)?.Slots.GetValueOrDefault(_workers[item.i].Index)?.ContainsKey(row.Key) == true) == true);
            bool allProbesLost = states.Length > 1 && states.All(s => s?.ProbeLost == true);
            loss = probeLost && Environment.TickCount64 - _probeLossAt >= 500 && (allProbesLost || fleetLoss);
            for (int i = 0; i < states.Length; i++)
                if (states[i]?.Fleet is { } fleet)
                    foreach (var row in fleet)
                    {
                        var slots = _desired.FirstOrDefault(s => s.Id == row.Scenario)?.Slots.GetValueOrDefault(_workers[i].Index);
                        if (slots is null || !slots.ContainsKey(row.Key)) continue;
                        // Keep failure counts honest for a lone connection drop;
                        // cancel its desired slot, so future restarts don't refill it.
                        if (row.Status is not ("active" or "failed" or "connection lost")) slots.Remove(row.Key);
                        else if (row.Status == "connection lost" && !probeLost && Volatile.Read(ref _spawning) == 0)
                        {
                            var key = (row.Scenario, _workers[i].Index, row.Key);
                            long now = Environment.TickCount64;
                            if (!_lostSlots.TryGetValue(key, out long lostAt)) _lostSlots[key] = lostAt = now;
                            // A large fleet's independent receive callbacks need
                            // not report the probe loss in the same 200ms sample.
                            if (now - lostAt >= 2000) { slots.Remove(row.Key); _lostSlots.Remove(key); }
                            else slots[row.Key] = row;
                        }
                        else slots[row.Key] = row;
                    }
            _desired.RemoveAll(s => s.Slots.Values.All(slots => slots.Count == 0));
            if (_desired.Count == 0 && _fleetState is not ("stopped" or "failed")) SetFleetState("idle");
            if (!loss || !_desired.Any(s => s.Recover && s.Slots.Values.Any(v => v.Count > 0)))
            {
                if (Volatile.Read(ref _spawning) == 0 && _desired.Count > 0)
                {
                    bool full = _desired.SelectMany(s => s.Slots).All(w =>
                        _workers.Any(worker => worker.Index == w.Key && worker.Alive) && w.Value.Values.All(s => s.Status == "active"));
                    SetFleetState(full ? "active" : "degraded", full ? null : _fleetFailure ?? "desired slots not active");
                }
                return false;
            }
            _recovering = true;
            _lostSlots.Clear();
            _recoveryAttempt = 0;
            // Slots for non-opted-in scenarios are deliberately not replayed.
            _desired.RemoveAll(s => !s.Recover);
            _fleetEpoch++;
            SetFleetState("recovering");
        }
        await RecoverFleetAsync();
        return true;
    }

    private async Task RecoverFleetAsync()
    {
        var elapsed = Stopwatch.StartNew();
        int attempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            lock (_gate) _recoveryCancel = cancel;
            var remaining = _recoveryDeadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            cancel.CancelAfter(remaining);
            bool locked = false;
            try
            {
                // Close old pipes first: an in-flight 390-slot spawn must not
                // hold the mutation semaphore for its remaining failed slots.
                foreach (var worker in _workers) worker.Dispose();
                await _mutation.WaitAsync(cancel.Token);
                locked = true;
                lock (_gate)
                {
                    _knownBots.Clear();
                    _coordinator = new(BotManager.DefenseSettings.Read(Environment.GetEnvironmentVariable),
                        string.Equals(Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SYNC_WINGS"), "on", StringComparison.OrdinalIgnoreCase));
                    if (!_desired.Any(s => s.Slots.Values.Any(v => v.Count > 0)))
                    {
                        _recovering = false;
                        SetFleetState("stopped");
                        return;
                    }
                }
                // One paced retry for the whole fleet, not one retry per bot.
                double delay = Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5)));
                lock (_gate) _recoveryAttempt = attempt;
                await Task.Delay(TimeSpan.FromSeconds(delay * Random.Shared.NextDouble() * .4 + delay * .8), cancel.Token);
                for (int i = 0; i < _workers.Length; i++) _workers[i] = _workerFactory!(i);
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                startup.CancelAfter(TimeSpan.FromSeconds(35));
                var ready = await FanOut("__ping", EmptyArgs, startup.Token);
                if (ready.Any(r => r.IsError)) continue;
                if (_paused) await FanOut("__pause", JsonSerializer.SerializeToElement(true), cancel.Token);
                DesiredScenario[] scenarios;
                lock (_gate) scenarios = _desired.ToArray();
                SetRecoveryState("replaying");
                await FanOut("__hold", JsonSerializer.SerializeToElement(true), cancel.Token);
                foreach (var scenario in scenarios)
                {
                    var replies = await Task.WhenAll(_workers.Select(w => SafeCall(w, "spawn_scenario", ScenarioArguments(scenario, true, w.Index), cancel.Token)));
                    // A refused/partial slot is reported failed, never a silent
                    // whole-preset replay. Only another server loss replays again.
                    if (replies.All(r => r.IsError)) _fleetFailure = "preset replay failed";
                }
                await FanOut("__hold", JsonSerializer.SerializeToElement(false), cancel.Token);
                var states = await FanOut("__state", EmptyArgs, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    foreach (var scenario in _desired)
                        foreach (var slots in scenario.Slots.Values)
                            foreach (var key in slots.Keys.ToArray()) slots[key] = slots[key] with { Name = null, Status = "failed" };
                    for (int i = 0; i < states.Length; i++)
                        if (!states[i].IsError)
                        {
                            var state = states[i].Data.Deserialize<WorkerState>()!;
                            Remember(_workers[i].Index, state.Bots);
                            foreach (var row in state.Fleet ?? [])
                            {
                                var slots = _desired.FirstOrDefault(s => s.Id == row.Scenario)?.Slots.GetValueOrDefault(_workers[i].Index);
                                if (slots?.ContainsKey(row.Key) == true) slots[row.Key] = row;
                            }
                        }
                    bool full = _desired.SelectMany(s => s.Slots.Values).SelectMany(s => s.Values).All(s => s.Status == "active");
                    _recovering = false;
                    SetFleetState(_desired.Count == 0 ? "stopped" : full ? "active" : "degraded", full ? null : "preset replay incomplete");
                    _probeLossAt = 0;
                }
                return;
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested && elapsed.Elapsed < _recoveryDeadline)
            {
                // Operator edits invalidate this epoch. Dispose everything before
                // replaying only the remaining desired slots.
                lock (_gate) _fleetEpoch++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Connection/startup failures share the same bounded retry loop.
                lock (_gate) _fleetFailure = ex.Message;
            }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_recoveryCancel, cancel)) _recoveryCancel = null; }
                if (locked) _mutation.Release();
                if (cancel.IsCancellationRequested) foreach (var worker in _workers) worker.Dispose();
            }
        }
        foreach (var worker in _workers) worker.Dispose();
        lock (_gate)
        {
            _recovering = false;
            foreach (var scenario in _desired)
                foreach (var slots in scenario.Slots.Values)
                    foreach (var key in slots.Keys.ToArray()) slots[key] = slots[key] with { Status = "failed" };
            if (_stop.IsCancellationRequested) SetFleetState("stopped");
            else SetFleetState("failed", "server recovery deadline exhausted");
        }
    }
    private void SetRecoveryState(string state) { lock (_gate) SetFleetState(state); }
}
