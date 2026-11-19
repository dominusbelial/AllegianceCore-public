using System.Buffers.Binary;
using Allegiance.Core;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Row-bounded version of MdlTextureDecoder's RGB565 mesh path.
/// Native engine/modeler.cpp:723-733 preserves file rows/pitch and
/// engine/device3d.cpp:2245-2246 preserves mesh UVs. Pixel results are tested
/// against that shared decoder, including keyed sky/planet pixels.</summary>
public sealed class IncrementalSurface
{
    private readonly byte[] _data;
    private readonly int _pixels, _pitch;
    private readonly uint _red, _green, _blue;
    public int Width { get; }
    public int Height { get; }
    public int Row { get; private set; }
    public bool Complete => Row == Height;
    public IncrementalSurface(byte[] data)
    {
        _data = data;
        int cursor = 0;
        uint Read()
        {
            if (cursor < 0 || cursor > data.Length - 4) throw new InvalidDataException("Truncated visual surface.");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)); cursor += 4; return value;
        }
        void SkipString()
        {
            int end = Array.IndexOf(data, (byte)0, cursor);
            if (end < 0) throw new InvalidDataException("Unterminated surface name.");
            cursor = (end + 4) & ~3;
        }
        if (Read() != MdlTextureDecoder.MdlMagic) throw new InvalidDataException("Unsupported visual surface.");
        _ = Read(); uint namespaces = Read(), imports = Read(), exports = Read(); _ = Read();
        if (namespaces > 1024 || imports > 1024 || exports > 1024) throw new InvalidDataException("Unbounded visual header.");
        for (int i = 0; i < namespaces; i++) SkipString();
        for (int i = 0; i < imports; i++) { _ = Read(); SkipString(); }
        for (int i = 0; i < exports; i++) SkipString();
        for (int i = 0; i < 4; i++) _ = Read();
        Width = (int)Read(); Height = (int)Read(); _pitch = (int)Read(); uint bits = Read();
        _red = Read(); _green = Read(); _blue = Read(); _ = Read(); _ = Read(); _pixels = cursor;
        if (Width is <= 0 or > 2048 || Height is <= 0 or > 2048 || bits != 16 ||
            _pitch < Width * 2 || cursor + (long)_pitch * Height > data.Length ||
            _red != 0xf800 || _green != 0x7e0 || _blue != 0x1f)
            throw new InvalidDataException("Invalid RGB565 visual surface.");
    }
    public int DecodeRows(byte[] rgba, int maximumRows, bool colorKey)
    {
        int rows = Math.Min(maximumRows, Height - Row);
        if (maximumRows <= 0 || rgba.Length < rows * Width * 4) throw new ArgumentException("Insufficient row buffer.");
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < Width; x++)
            {
                int p = _pixels + (Row + y) * _pitch + x * 2;
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(p, 2));
                byte r = (byte)MathF.Round(((v & _red) >> 11) / 31f * 255f);
                byte g = (byte)MathF.Round(((v & _green) >> 5) / 63f * 255f);
                byte b = (byte)MathF.Round((v & _blue) / 31f * 255f);
                int d = (y * Width + x) * 4;
                bool transparent = colorKey && (r * 299 + g * 587 + b * 114) / 1000 <= 10;
                rgba[d] = transparent ? (byte)0 : r; rgba[d + 1] = transparent ? (byte)0 : g;
                rgba[d + 2] = transparent ? (byte)0 : b; rgba[d + 3] = transparent ? (byte)0 : (byte)255;
            }
        Row += rows; return rows;
    }
}
