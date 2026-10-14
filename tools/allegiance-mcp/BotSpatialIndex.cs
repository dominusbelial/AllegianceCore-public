using System;
using System.Collections.Generic;
using Allegiance.Core.Collision;   // P633 — Vector3

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P633 — a tiny uniform-grid spatial index over a set of ship positions for
/// the bot decision loop (item 3). Built once per sector snapshot and queried
/// repeatedly for friendly-in-line checks — so a bot's O(cluster) scans become O(cells-overlapping +
/// occupants), instead of walking every ship in the world on every query.
///
/// Design notes (matching the task's honesty rules):
/// <list type="bullet">
///   <item><b>Per-sector:</b> an index instance covers ships of ONE cluster; the
///   caller passes the cluster id at build time and <see cref="QueryCluster"/>
///   returns only that cluster's occupants, so ships in other sectors are never
///   examined.</item>
///   <item><b>Shared read-only by same side where views are identical:</b> the
///   index is IMMUTABLE after construction and never mutated, so the bot harness
///   may share one build per side among same-side bots whose ClientWorld views
///   are identical. It is never shared across sides (visibility stays honest —
///   each side's index is built only from what that side can see).</item>
///   <item><b>Deterministic:</b> cells are iterated in ascending index order and
///   occupants in insertion order, so neighbour results are reproducible for a
///   fixed input — the property the unit tests pin down.</item>
/// </list>
/// </summary>
public sealed class BotSpatialIndex
{
    private readonly float _cell;
    private readonly short _cluster;
    // Flat occupancy: for each cell id, the contiguous run [Start_i, Start_i+Count_i)
    // into _occupants. -1 = empty cell.
    private readonly int[] _cellStart;
    private readonly int[] _cellCount;
    private readonly (short Id, Vector3 Pos)[] _occupants;
    private readonly float _originX, _originZ;
    private readonly int _cw, _ch;

    /// <summary>A single occupant as fed to the index at build time.</summary>
    public readonly record struct ShipPoint(short Id, Vector3 Position);

    /// <summary>
    /// Build a grid of <paramref name="cellSize"/> over the given ships'
    /// positions, storing each under <paramref name="cluster"/>. Ships whose
    /// position is not finite are skipped. Cell size balances occupancy against
    /// query locality (defensive-advice default ~400 m for a typical sector).
    /// </summary>
    public static BotSpatialIndex Build(
        short cluster, IReadOnlyList<ShipPoint> ships, float cellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0) cellSize = 400f;
        // Bounding box of the cluster's ships (empty → degenerate 1-cell grid).
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        int n = 0;
        foreach (var s in ships)
        {
            if (!float.IsFinite(s.Position.X) || !float.IsFinite(s.Position.Z)) continue;
            n++;
            if (s.Position.X < minX) minX = s.Position.X;
            if (s.Position.X > maxX) maxX = s.Position.X;
            if (s.Position.Z < minZ) minZ = s.Position.Z;
            if (s.Position.Z > maxZ) maxZ = s.Position.Z;
        }
        if (n == 0) { minX = minZ = 0; maxX = maxZ = 0; }

        // Use relative coordinates: very large finite world offsets must not
        // overflow integer cell coordinates. Enlarge cells until the whole grid fits.
        int cw, ch;
        while (true)
        {
            double width = Math.Floor(((double)maxX - minX) / cellSize) + 1;
            double height = Math.Floor(((double)maxZ - minZ) / cellSize) + 1;
            if (width * height <= 1 << 20)
            {
                cw = (int)width;
                ch = (int)height;
                break;
            }
            cellSize *= 2;
        }
        int ncells = cw * ch;

        var cellCount = new int[ncells];
        var occ = new (short, Vector3)[n];
        int idx = 0;
        foreach (var s in ships)
        {
            if (!float.IsFinite(s.Position.X) || !float.IsFinite(s.Position.Z)) continue;
            int cx = CellCoordinate(s.Position.X, minX, cellSize, cw);
            int cz = CellCoordinate(s.Position.Z, minZ, cellSize, ch);
            int cell = cz * cw + cx;
            cellCount[cell]++;
            occ[idx++] = (s.Id, s.Position);
        }

        // Prefix sums → contiguous per-cell runs, in ascending cell order.
        var start = new int[ncells];
        int acc = 0;
        for (int c = 0; c < ncells; c++) { start[c] = acc; acc += cellCount[c]; }
        var sorted = new (short, Vector3)[n];
        var cursor = (int[])start.Clone();
        for (int i = 0; i < n; i++)
        {
            var s = occ[i];
            int cx = CellCoordinate(s.Item2.X, minX, cellSize, cw);
            int cz = CellCoordinate(s.Item2.Z, minZ, cellSize, ch);
            int cell = cz * cw + cx;
            sorted[cursor[cell]++] = s;
        }

        return new BotSpatialIndex(cluster, cellSize, minX, minZ, cw, ch, start, cellCount, sorted);
    }

    private BotSpatialIndex(short cluster, float cell, float originX, float originZ, int cw, int ch,
        int[] cellStart, int[] cellCount, (short, Vector3)[] occupants)
    {
        _cluster = cluster;
        _cell = cell;
        _originX = originX; _originZ = originZ; _cw = cw; _ch = ch;
        _cellStart = cellStart; _cellCount = cellCount;
        _occupants = occupants;
    }

    /// <summary>The cluster this index was built for.</summary>
    public short Cluster => _cluster;

    /// <summary>All ships whose cell equals <paramref name="cluster"/> (== this
    /// index's cluster — ships in other sectors are simply not present).</summary>
    public IReadOnlyList<(short Id, Vector3 Pos)> QueryCluster(short cluster)
    {
        if (cluster != _cluster) return Array.Empty<(short, Vector3)>();
        return _occupants;
    }

    /// <summary>
    /// P633 — return every ship within <paramref name="radius"/> of
    /// <paramref name="center"/> (horizontal X/Z), examining only the grid cells
    /// whose rectangle intersects the query disc (O(cells ∩ disc + occupants)),
    /// not the whole world. Exact distance filter is applied per occupant so the
    /// result is correct; iteration order is deterministic (ascending cell, then
    /// insertion order).
    /// </summary>
    public List<(short Id, Vector3 Pos)> QueryRadius(Vector3 center, float radius)
    {
        var result = new List<(short, Vector3)>();
        if (!float.IsFinite(radius) || radius < 0 || !float.IsFinite(center.X) || !float.IsFinite(center.Z)) return result;
        double r2 = (double)radius * radius;
        int cxm = CellCoordinate((double)center.X - radius, _originX, _cell, _cw);
        int cxp = CellCoordinate((double)center.X + radius, _originX, _cell, _cw);
        int czm = CellCoordinate((double)center.Z - radius, _originZ, _cell, _ch);
        int czp = CellCoordinate((double)center.Z + radius, _originZ, _cell, _ch);
        for (int cz = Math.Max(0, czm); cz <= Math.Min(_ch - 1, czp); cz++)
            for (int cx = Math.Max(0, cxm); cx <= Math.Min(_cw - 1, cxp); cx++)
            {
                int cell = cz * _cw + cx;
                int st = _cellStart[cell], cnt = _cellCount[cell];
                if (cnt <= 0) continue;
                for (int i = st; i < st + cnt; i++)
                {
                    var (id, pos) = _occupants[i];
                    double dx = (double)pos.X - center.X, dz = (double)pos.Z - center.Z;
                    if (dx * dx + dz * dz <= r2) result.Add((id, pos));
                }
            }
        return result;
    }
    private static int CellCoordinate(double value, float origin, float cell, int count) =>
        (int)Math.Clamp(Math.Floor((value - origin) / cell), 0, count - 1);

}
