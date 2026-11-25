using System.Diagnostics;
using Allegiance.Core.Identity;

namespace Allegiance.Tools.McpSpawner;

/// <summary>One external driver owns its combat peers. Server roster snapshots
/// alone decide seat counts. No server admin interface or extra game rule.</summary>
internal sealed class BackfillDriver
{
    private readonly BotManager _manager;
    private readonly BackfillSeats _policy;
    private readonly string[] _patterns;
    private readonly Dictionary<short, (bool Joining, Task<string?> Work)> _pending = new();
    private readonly Dictionary<short, BackfillRetry> _admissionRetries = new();
    private readonly BackfillLiveness _liveness = new();
    private Task? _maintenance;
    private bool _probeRecoveryPending;
    private object? _rosterProbe;
    private readonly Dictionary<short, double> _settle = new();
    private readonly HashSet<short> _protectedLogged = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _finaleArmed;
    private readonly int _seats;
    private long _generation = -1;
    private double _generationAt;

    internal BackfillDriver(BotManager manager, int seats, string[]? patterns = null)
    {
        if (seats is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(seats));
        _manager = manager;
        _seats = seats;
        _policy = new(seats);
        _patterns = patterns ?? BotNameRules.Defaults();
        if (!BotNameRules.IsBot(_patterns, "mcp-probe-w1-test") || !BotNameRules.IsBot(_patterns, "mcp-b1-test")
            || !BotNameRules.IsBot(_patterns, "mcp-cmd1-test"))
            throw new ArgumentException("Backfill bot-name rules must include the driver's mcp-* names");
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        _manager.PrepareBackfill(_seats);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Step();
                await Task.Delay(250, ct);
            }
        }
        finally
        {
            // Manager owns all connections and is disposed by the caller after
            // outstanding admission attempts finish. Never abandon a spawn task.
            await Task.WhenAll(_pending.Values.Select(p => p.Work));
            if (_maintenance is not null) await _maintenance;
        }
    }

    internal void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        var roster = _manager.BackfillRoster(_patterns);
        foreach (short side in _pending.Where(p => p.Value.Work.IsCompleted).Select(p => p.Key).ToArray())
        {
            var pending = _pending[side];
            string? joined = pending.Work.GetAwaiter().GetResult();
            if (pending.Joining)
            {
                var retry = _admissionRetries[side];
                if (joined is null) retry.Failed(now, Random.Shared.NextDouble());
                else retry.Succeeded();
            }
            _pending.Remove(side);
            _settle[side] = now + 1; // wait for the native roster echo after admission/removal
        }
        if (_liveness.Observe(now, roster.NotReadyReason) is { } stalled) Console.WriteLine(stalled);
        if (_maintenance?.IsCompleted == true)
        {
            _maintenance.GetAwaiter().GetResult();
            _maintenance = null;
        }
        if (_maintenance is null)
        {
            // A ready snapshot may go stale before the worker starts. Never
            // authorize replacement on a tick that can also admit a combat peer.
            bool recoverProbe = !roster.Ready && _pending.Count == 0;
            _probeRecoveryPending = recoverProbe;
            _maintenance = Task.Run(() => _manager.MaintainBackfillSessions(recoverProbe));
        }
        if (roster.Generation != _generation || !ReferenceEquals(_rosterProbe, _manager.Probe))
        {
            _rosterProbe = _manager.Probe;
            _generation = roster.Generation;
            _generationAt = now;
            _policy.NewMatch();
            _protectedLogged.Clear();
            Console.WriteLine($"[backfill] generation={_generation} roster settling");
        }
        if (!roster.Ready || now - _generationAt < 2 || _probeRecoveryPending) return;
        foreach (short side in roster.Sides)
        {
            if (_pending.ContainsKey(side) || _settle.GetValueOrDefault(side) > now) continue;
            var decision = _policy.Decide(side, roster.Pilots, now);
            if (decision is null) { _protectedLogged.Remove(side); continue; }
            if (decision.Action == "protected")
            {
                if (_protectedLogged.Add(side)) Log(decision, "keep protected economy/commander seats");
                continue;
            }
            _protectedLogged.Remove(side);
            if (!_admissionRetries.TryGetValue(side, out var admissionRetry))
                _admissionRetries[side] = admissionRetry = new();
            if (decision.Action == "join" && !admissionRetry.Due(now)) continue;
            Log(decision, decision.Action == "quit" ? $"quit {decision.Name}" : "join combat bot");
            // A slow purchase on one side must not delay a human's eviction on
            // another side. At most one outstanding operation per side.
            _pending[side] = (decision.Action == "join", Task.Run(() =>
            {
                try
                {
                    if (decision.Action == "quit")
                    {
                        if (!_manager.QuitBackfillCombat(decision.Name!))
                            Log(decision, $"retained {decision.Name} (role/leadership changed)");
                        return null;
                    }
                    return _manager.SpawnBackfillCombat(side);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[backfill] side {side} {decision.Action} failed: {ex.Message}");
                    return null;
                }
            }));
        }
        if (!_finaleArmed && _pending.Count == 0 && roster.Sides.Length >= 2 && roster.Sides.All(side =>
            roster.Pilots.Count(p => p.Side == side) >= _seats
            && roster.Pilots.Any(p => p.Side == side && p.Owned && !p.Protected)))
        {
            _manager.ArmBackfillFinale();
            _finaleArmed = true;
        }
    }

    private static void Log(BackfillDecision d, string action) =>
        Console.WriteLine($"[backfill] side {d.Side} humans {d.Humans} bots {d.Bots} → {action}");
}
