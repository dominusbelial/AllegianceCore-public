using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

/// <summary>One endpoint per treasure, so each query returns each original index at most once.</summary>
internal sealed class TreasurePickupGrid
{
    private const int CellSize = 128;
    private readonly Dictionary<(int X, int Y, int Z), int> _heads = [];
    private readonly List<int> _next = [];
    private float _maxDrift;
    internal readonly List<Treasure> Treasures = [];
    internal readonly HashSet<Treasure> Done = [];
    internal readonly List<int> Candidates = [];

    // Bound the float error of the relative segment (and avoid squared-length overflow).
    // At 2^20, relative endpoints are <= 2^21 and relative travel <= 2^22.
    private static bool InDomain(Vector3 p) =>
        MathF.Abs(p.X) <= 1048576 && MathF.Abs(p.Y) <= 1048576 && MathF.Abs(p.Z) <= 1048576;

    private static int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellSize);

    internal bool Build()
    {
        _heads.Clear();
        _next.Clear();
        _maxDrift = 0;
        if (Treasures.Count < 16) return false;
        for (int i = 0; i < Treasures.Count; i++)
        {
            var end = Treasures[i].Position;
            var start = Treasures[i].PreviousPosition;
            if (!InDomain(start) || !InDomain(end)) return false;
            var delta = end - start;
            _maxDrift = MathF.Max(_maxDrift,
                MathF.Max(MathF.Abs(delta.X), MathF.Max(MathF.Abs(delta.Y), MathF.Abs(delta.Z))));
            var key = (Cell(end.X), Cell(end.Y), Cell(end.Z));
            _next.Add(_heads.GetValueOrDefault(key, -1));
            _heads[key] = i;
        }
        return _heads.Count > 1;
    }

    internal bool Query(Vector3 start, Vector3 end, float radius)
    {
        if (!InDomain(start) || !InDomain(end) || !(MathF.Abs(radius) <= 1048576)) return false;
        // The scalar test squares radius. Include both sweeps even for negative hull
        // lengths. Four world units cover rounding in relative endpoints, travel,
        // interpolation and the outward cell bounds throughout the indexed domain.
        float pad = MathF.Abs(radius) + _maxDrift + 4;
        int x0 = Cell(MathF.Min(start.X, end.X) - pad), x1 = Cell(MathF.Max(start.X, end.X) + pad);
        int y0 = Cell(MathF.Min(start.Y, end.Y) - pad), y1 = Cell(MathF.Max(start.Y, end.Y) + pad);
        int z0 = Cell(MathF.Min(start.Z, end.Z) - pad), z1 = Cell(MathF.Max(start.Z, end.Z) + pad);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 512) return false;

        Candidates.Clear();
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_heads.TryGetValue((x, y, z), out int head)) continue;
            for (int i = head; i >= 0; i = _next[i]) Candidates.Add(i);
        }
        Candidates.Sort();
        return true;
    }
}
