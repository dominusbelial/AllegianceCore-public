using System.Buffers.Binary;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Strict, bounded DDS DX10 2D BC reader. Only selected mip bytes are read;
/// neither this reader nor the upload path contains a software BC decoder.</summary>
public sealed record HdDds(int Width, int Height, int Levels, int Bc)
{
    public int BlockBytes => Bc == 4 ? 8 : 16;
    public int LevelBytes(int level) => checked(((Math.Max(1, Width >> level) + 3) / 4) *
        ((Math.Max(1, Height >> level) + 3) / 4) * BlockBytes);
    public long BytesFrom(int first) => Enumerable.Range(first, Levels - first).Sum(i => (long)LevelBytes(i));
    public static HdDds Read(string path, string map)
    {
        using var stream = File.OpenRead(path);
        Span<byte> h = stackalloc byte[148]; stream.ReadExactly(h);
        // Span cannot be captured; use an array for the bounded header helpers.
        return Parse(h.ToArray(), stream.Length, map);
    }
    private static HdDds Parse(byte[] h, long length, string map)
    {
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offset));
        if (!h.AsSpan(0, 4).SequenceEqual("DDS "u8) || U(4) != 124 || U(76) != 32 || U(80) != 4 ||
            U(84) != 0x30315844 || U(132) != 3 || U(136) != 0 || U(140) != 1 || U(24) != 0 || U(112) != 0)
            throw new InvalidDataException("unsupported DDS container");
        int w = checked((int)U(16)), height = checked((int)U(12)), levels = checked((int)U(28));
        if (w is < 1 or > 4096 || height is < 1 or > 4096 || (w & (w - 1)) != 0 || (height & (height - 1)) != 0 ||
            levels != 1 + (int)Math.Log2(Math.Max(w, height))) throw new InvalidDataException("DDS dimensions/mips");
        int bc = U(128) switch { 99 => 7, 83 => 5, 80 => 4, 78 => 3, _ => throw new InvalidDataException("DDS DXGI format") };
        if ((map is "basecolor" or "emission") ? bc is not (7 or 3) : bc != (map == "normal" ? 5 : 4))
            throw new InvalidDataException("DDS map format/colour space");
        var result = new HdDds(w, height, levels, bc);
        if (length != 148 + result.BytesFrom(0)) throw new InvalidDataException("DDS payload length");
        return result;
    }
    public static int SelectFirstMip(int width, int height, float projectedPixels)
    {
        if (!float.IsFinite(projectedPixels) || projectedPixels <= 0) return 0;
        // Two texels per screen pixel and a 64px floor avoid starving small hulls.
        int desired = Math.Max(64, (int)Math.Min(8192, projectedPixels * 2));
        int level = 0;
        while (Math.Max(width, height) > desired && Math.Max(width, height) > 4)
        { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); level++; }
        return level;
    }
}
