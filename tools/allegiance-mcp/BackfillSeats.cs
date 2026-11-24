namespace Allegiance.Tools.McpSpawner;

internal sealed record BackfillPilot(string Name, short Side, bool Bot, bool Protected, string Role, bool Owned, bool Ready = true);
internal sealed record BackfillDecision(short Side, int Humans, int Bots, string Action, string? Name = null);

/// <summary>Port orchestration, not a native rule. Monotonic wall time keeps the
/// reconnect grace independent of simulation acceleration and intermissions.</summary>
internal sealed class BackfillSeats(int seats, double graceSeconds = 30)
{
    private readonly Dictionary<short, double> _deficitSince = new();
    private readonly HashSet<short> _seen = new();
    private readonly Dictionary<short, int> _humans = new();

    internal void NewMatch() { _deficitSince.Clear(); _seen.Clear(); }
    internal BackfillDecision? Decide(short side, IReadOnlyList<BackfillPilot> roster, double now)
    {
        var pilots = roster.Where(p => p.Side == side).ToArray();
        int humans = pilots.Count(p => !p.Bot && p.Ready), bots = pilots.Count(p => p.Bot);
        bool first = _seen.Add(side);
        bool humanLeft = humans < _humans.GetValueOrDefault(side, humans);
        _humans[side] = humans;
        int total = humans + bots;
        if (total >= seats)
        {
            _deficitSince.Remove(side);
            if (total == seats) return null;
            var drop = DropOrder(pilots).FirstOrDefault();
            return drop is null ? new(side, humans, bots, "protected") : new(side, humans, bots, "quit", drop.Name);
        }
        // Human counts intentionally survive NewMatch: a departure during the
        // two-second roster settling window is not a cold-start vacant seat.
        if (humanLeft) _deficitSince[side] = now;
        if (!_deficitSince.TryGetValue(side, out double since))
            _deficitSince[side] = since = first && !humanLeft ? now - graceSeconds : now;
        return now - since >= graceSeconds ? new(side, humans, bots, "join") : null;
    }

    internal static IEnumerable<BackfillPilot> DropOrder(IEnumerable<BackfillPilot> pilots) => pilots
        .Where(p => p.Bot && p.Owned && !p.Protected)
        .OrderBy(p => p.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
}
