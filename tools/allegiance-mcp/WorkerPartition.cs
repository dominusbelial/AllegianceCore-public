namespace Allegiance.Tools.McpSpawner;

/// <summary>Partition the complete, identical scenario plan, independently per side.</summary>
internal sealed record WorkerPartition(int Index, int Count)
{
    internal static int[] Split(int total, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        return Enumerable.Range(0, workers).Select(i => total / workers + (i < total % workers ? 1 : 0)).ToArray();
    }

    internal IReadOnlyList<SpawnBatch.Slot> Select(IReadOnlyList<SpawnBatch.Slot> slots)
    {
        var offsets = new Dictionary<short, int>();
        return slots.Where(s =>
        {
            int offset = offsets.GetValueOrDefault(s.Side);
            offsets[s.Side] = offset + 1;
            return offset % Count == Index;
        }).ToArray();
    }
}
