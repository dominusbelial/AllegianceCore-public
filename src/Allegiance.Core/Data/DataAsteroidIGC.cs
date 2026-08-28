using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataAsteroidIGC</c> payload for <c>OT_asteroid</c> (5). Fixed 144 bytes.
/// Source layout: <c>DataAsteroidIGC</c> (<c>src/Igc/igc.h:1891</c>) embedding
/// <c>AsteroidDef</c> (<c>src/Igc/igc.h:1709</c>), C++ default alignment. See
/// wiki page <c>allegiance-igc-file-format</c> §OT_asteroid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Corpus-measured correction:</b> the shipped files place <c>clusterID</c>
/// at <b>116</b>, <c>name</c> at <b>118</b> and <c>fraction</c> at <b>143</b>,
/// with <b>three uninterpreted bytes at 113-115</b> sitting between
/// <c>iconName</c> (ending at 112) and <c>clusterID</c>. The current
/// <c>igc.h</c> struct would put clusterID at 114 (1 pad at 113) and name at
/// 115; the on-disk layout is two bytes later, so the shipped maps were
/// written by an older revision whose struct carried three extra bytes here.
/// Verified across all 973 records: every <c>clusterID</c> at 116 names a real
/// sector in the same file, so the offset is certain despite the gap. The
/// 113-115 bytes are overwhelmingly uninitialized (<c>0xCC</c> debug fill or
/// zero) and are deliberately <b>not</b> exposed as a field.
/// </para>
/// <para>The fixed-size 144 offsets are otherwise padding-free: <c>ore</c>,
/// <c>oreMax</c>, <c>hitPoints</c> are 4-aligned, the three 13-char names are
/// byte-aligned, and the 25-char name + fraction tail is byte-aligned.</para>
/// </remarks>
public readonly record struct DataAsteroidIGC(
    float Signature,
    float[] Position,
    float[] Up,
    float[] Forward,
    float[] Rotation,
    float Ore,
    float OreMax,
    short AabmCapabilities,
    short AsteroidId,
    float HitPoints,
    short Radius,
    string ModelName,
    string TextureName,
    string IconName,
    short ClusterId,
    string Name,
    byte Fraction)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataAsteroidIGC)</c>.</summary>
    public const int Size = 144;

    /// <summary>
    /// Decode an <c>OT_asteroid</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataAsteroidIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataAsteroidIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));
        static float[] Vec(ReadOnlySpan<byte> s, int off) =>
        [
            BinaryPrimitives.ReadSingleLittleEndian(s[off..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 8)..]),
        ];

        return new DataAsteroidIGC(
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[0..]),
            Position: Vec(b, 4),
            Up: Vec(b, 16),
            Forward: Vec(b, 28),
            Rotation:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[44..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            ],
            Ore: BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            OreMax: BinaryPrimitives.ReadSingleLittleEndian(b[60..]),
            AabmCapabilities: BinaryPrimitives.ReadInt16LittleEndian(b[64..]),
            AsteroidId: BinaryPrimitives.ReadInt16LittleEndian(b[66..]),
            HitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[68..]),
            Radius: BinaryPrimitives.ReadInt16LittleEndian(b[72..]),
            ModelName: Fix(b, 74, 13),
            TextureName: Fix(b, 87, 13),
            IconName: Fix(b, 100, 13),
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[116..]),
            Name: Fix(b, 118, 25),
            Fraction: b[143]);
    }
}
