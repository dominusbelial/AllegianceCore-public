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
    private readonly Dictionary<short, Task<string?>> _pending = new();
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
            await Task.WhenAll(_pending.Values);
        }
    }

    internal void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        var roster = _manager.BackfillRoster(_patterns);
        foreach (short side in _pending.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
        {
            _ = _pending[side].GetAwaiter().GetResult();
            _pending.Remove(side);
            _settle[side] = now + 1; // wait for the native roster echo after admission/removal
        }
        if (roster.Generation != _generation)
        {
            _generation = roster.Generation;
            _generationAt = now;
            _policy.NewMatch();
            _protectedLogged.Clear();
            Console.WriteLine($"[backfill] generation={_generation} roster settling");
        }
        if (!roster.Ready || now - _generationAt < 2) return;
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
            Log(decision, decision.Action == "quit" ? $"quit {decision.Name}" : "join combat bot");
            // A slow purchase on one side must not delay a human's eviction on
            // another side. At most one outstanding operation per side.
            _pending[side] = Task.Run(() =>
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
            });
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
