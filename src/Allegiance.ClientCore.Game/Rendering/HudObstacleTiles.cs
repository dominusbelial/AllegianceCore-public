using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Broad phase for the shared text placer. Tile membership only
/// narrows the search. Rectangle.Intersects decides every collision.</summary>
internal sealed class HudObstacleTiles
{
    private readonly record struct Obstacle(Rectangle Bounds, bool BlocksInstruments);
    private List<Obstacle>?[] _tiles = new List<Obstacle>?[1];
    private int _columns = 1, _rows = 1;

    public void Reset(int width, int height)
    {
        foreach (var tile in _tiles) tile?.Clear();
        // 64-pixel tiles. Saturate at the outer tiles for offscreen geometry
        // and unusually large viewports, bounding the size of the directory.
        _columns = Math.Clamp((width >> 6) + 1, 1, 64);
        _rows = Math.Clamp((height >> 6) + 1, 1, 64);
        int count = _columns * _rows;
        if (_tiles.Length < count) Array.Resize(ref _tiles, count);
    }

    private (int Left, int Right, int Top, int Bottom) TilesFor(Rectangle bounds)
    {
        int left = Math.Clamp(bounds.Left >> 6, 0, _columns - 1);
        int top = Math.Clamp(bounds.Top >> 6, 0, _rows - 1);
        // Include the end tile. Even a zero-width rectangle can satisfy XNA's
        // strict inequalities inside a wider rectangle. For reversed endpoints
        // any intersecting interval must span the start point.
        return (left, Math.Clamp(bounds.Right >> 6, left, _columns - 1),
            top, Math.Clamp(bounds.Bottom >> 6, top, _rows - 1));
    }

    public void Add(Rectangle bounds, bool blocksInstruments)
    {
        var tiles = TilesFor(bounds);
        var obstacle = new Obstacle(bounds, blocksInstruments);
        for (int y = tiles.Top; y <= tiles.Bottom; y++)
        for (int x = tiles.Left; x <= tiles.Right; x++)
        {
            var tile = _tiles[y * _columns + x] ??= new();
            tile.Add(obstacle);
        }
    }

    // Labels only need the first intersection. A panel sweep needs the
    // greatest bottom minus its part offset, exactly as in the linear scan.
    public int? BlockingBottom(Rectangle bounds, bool instrument, int? partOffset = null)
    {
        var tiles = TilesFor(bounds);
        int? next = null;
        for (int y = tiles.Top; y <= tiles.Bottom; y++)
        for (int x = tiles.Left; x <= tiles.Right; x++)
        {
            if (_tiles[y * _columns + x] is not { } tile) continue;
            foreach (var obstacle in tile)
            {
                if (instrument && !obstacle.BlocksInstruments) continue;
                if (!obstacle.Bounds.Intersects(bounds)) continue;
                if (partOffset is not int offset) return obstacle.Bounds.Bottom;
                int bottom = obstacle.Bounds.Bottom - offset;
                next = next.HasValue ? Math.Max(next.Value, bottom) : bottom;
            }
        }
        return next;
    }
}
