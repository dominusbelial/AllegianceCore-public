namespace Allegiance.Tools.McpSpawner;

/// <summary>Counts final live slots separately from failed attempts, including refill.</summary>
internal sealed class SpawnBatch
{
    internal sealed record Slot(int Index, short Side, string Hull, Func<BotManager.SpawnResult> Spawn);
    internal sealed record Failure(int index, short side, string hull, int attempt, string error);
    internal sealed record SideCounts(short side, int requested, int spawned, int failed, int refilled);
    private readonly IReadOnlyList<Slot> _slots;
    private readonly BotManager.SpawnResult?[] _results;
    private readonly bool[] _refilled;
    public List<Failure> Failures { get; } = new();
    public IReadOnlyList<BotManager.SpawnResult> Results => _results.Where(r => r.HasValue).Select(r => r!.Value).ToArray();

    public SpawnBatch(IReadOnlyList<Slot> slots)
    {
        _slots = slots;
        _results = new BotManager.SpawnResult?[slots.Count];
        _refilled = new bool[slots.Count];
    }

    public void Run(bool refill, Func<BotManager.SpawnResult, bool> alive)
    {
        void Attempt(int i, int attempt)
        {
            try
            {
                var result = _slots[i].Spawn();
                if (!alive(result)) throw new InvalidOperationException("bot disconnected or was disposed before spawn completed");
                _results[i] = result;
                _refilled[i] = attempt == 2;
            }
            catch (Exception ex)
            {
                var slot = _slots[i];
                Failures.Add(new(slot.Index, slot.Side, slot.Hull, attempt, ex.Message));
                SpawnLaunch.LogFailure(slot.Side, slot.Hull, $"slot {slot.Index} attempt {attempt}: {ex.Message}");
            }
        }
        void Refresh()
        {
            for (int i = 0; i < _slots.Count; i++)
                if (_results[i] is { } r && !alive(r))
                {
                    var slot = _slots[i];
                    const string reason = "bot disconnected or was disposed during scenario spawning";
                    Failures.Add(new(slot.Index, slot.Side, slot.Hull, _refilled[i] ? 2 : 1, reason));
                    SpawnLaunch.LogFailure(slot.Side, slot.Hull, $"slot {slot.Index}: {reason}");
                    _results[i] = null;
                }
        }
        for (int i = 0; i < _slots.Count; i++) Attempt(i, 1);
        Refresh();
        if (refill)
            for (int i = 0; i < _slots.Count; i++)
                if (_results[i] is null) Attempt(i, 2);
        Refresh();
    }

    public SideCounts Counts(short side)
    {
        int requested = 0, spawned = 0, refilled = 0;
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Side == side)
            {
                requested++;
                if (_results[i].HasValue) { spawned++; if (_refilled[i]) refilled++; }
            }
        return new(side, requested, spawned, requested - spawned, refilled);
    }
}
