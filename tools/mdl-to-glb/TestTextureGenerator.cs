namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P824 — the built-in proof texture for the new-art pipeline: a two-grey
/// checker with a saturated colour marker in each corner of the GPU-order
/// image (top-left origin), so the UV mapping can be asserted numerically at
/// any of the four corners — the marker must land exactly where the UVs say,
/// through the writer → <c>.mdl</c> file → decoder → renderer chain.
/// </summary>
/// <remarks>
/// Corner palette (GPU space, so u=0/v=0 is the top-left texel (0,0) — the
/// same convention glTF and the port's renderer use):
/// <list type="bullet">
/// <item>TL (u≈0, v≈0): magenta <see cref="TopLeftMarker"/></item>
/// <item>TR (u≈1, v≈0): yellow <see cref="TopRightMarker"/></item>
/// <item>BL (u≈0, v≈1): cyan <see cref="BottomLeftMarker"/></item>
/// <item>BR (u≈1, v≈1): green <see cref="BottomRightMarker"/></item>
/// </list>
/// Each marker is a solid <see cref="MarkerSize"/>-square block; the rest of
/// the image is an 8×8-tile checker of <see cref="CheckerA"/>/<see cref="CheckerB"/>.
/// </remarks>
public static class TestTextureGenerator
{
    public const int DefaultSize = 128;
    public const int MarkerSize = 16;
    public const int CheckerTile = 8;

    public static readonly (byte r, byte g, byte b) TopLeftMarker = (255, 0, 255);     // magenta
    public static readonly (byte r, byte g, byte b) TopRightMarker = (255, 255, 0);    // yellow
    public static readonly (byte r, byte g, byte b) BottomLeftMarker = (0, 255, 255);  // cyan
    public static readonly (byte r, byte g, byte b) BottomRightMarker = (0, 255, 0);   // green
    public static readonly (byte r, byte g, byte b) CheckerA = (96, 96, 96);
    public static readonly (byte r, byte g, byte b) CheckerB = (160, 160, 160);

    /// <summary>Build the proof texture as top-left-origin RGBA
    /// (width×height×4 bytes, alpha 255).</summary>
    public static byte[] BuildRgba(int size = DefaultSize)
    {
        if (size <= 0)
            throw new InvalidDataException($"texture size {size} must be positive");
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var c = MarkerAt(x, y, size) ?? CheckerAt(x, y);
                int i = (y * size + x) * 4;
                rgba[i] = c.r; rgba[i + 1] = c.g; rgba[i + 2] = c.b; rgba[i + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>The corner marker covering GPU pixel (x, y), or null for checker.</summary>
    public static (byte r, byte g, byte b)? MarkerAt(int x, int y, int size) =>
        (x < MarkerSize && y < MarkerSize) ? TopLeftMarker
        : (x >= size - MarkerSize && y < MarkerSize) ? TopRightMarker
        : (x < MarkerSize && y >= size - MarkerSize) ? BottomLeftMarker
        : (x >= size - MarkerSize && y >= size - MarkerSize) ? BottomRightMarker
        : null;

    private static (byte r, byte g, byte b) CheckerAt(int x, int y) =>
        (x / CheckerTile + y / CheckerTile) % 2 == 0 ? CheckerA : CheckerB;
}
