using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataProjectileTypeIGC</c> payload for <c>OT_projectileType</c> (22).
/// Fixed 88 bytes. Source layout: <c>DataObjectIGC</c>
/// (<c>src/Igc/igc.h:1904</c>) + extras (<c>DataProjectileTypeIGC</c>,
/// <c>src/Igc/igc.h:1924</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_projectileType.
/// </summary>
public readonly record struct DataProjectileTypeIGC(
    float[] Color,
    float Radius,
    float Rotation,
    string ModelName,
    string TextureName,
    float Power,
    float BlastPower,
    float BlastRadius,
    float Speed,
    float Lifespan,
    short ProjectileTypeId,
    byte DamageType,
    bool AbsoluteF,
    bool Directional,
    float Width,
    short AmbientSound)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataProjectileTypeIGC)</c>.</summary>
    public const int Size = 88;

    /// <summary>
    /// Decode an <c>OT_projectileType</c> payload.
    /// </summary>
    /// <remarks>
    /// Corpus-measured correction to the wiki table: <c>width</c> sits at
    /// offset <b>80</b>, not 78. After <c>bDirectional</c> at 76 the following
    /// float needs 4-byte alignment, so there are three pad bytes (77-79) then
    /// <c>width</c>@80. Verified across all 9369 records: <c>width</c>@80 is
    /// always a clean float, <c>ambientSound</c> is <c>-1</c> in every record,
    /// and <c>damageType</c> is in 0..19.
    /// </remarks>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataProjectileTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataProjectileTypeIGC expects {Size} bytes, got {b.Length}");

        static string FixChar(ReadOnlySpan<byte> s, int start, int count) =>
            CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        return new DataProjectileTypeIGC(
            Color: [
                BinaryPrimitives.ReadSingleLittleEndian(b[0..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[12..]),
            ],
            Radius: BinaryPrimitives.ReadSingleLittleEndian(b[16..]),
            Rotation: BinaryPrimitives.ReadSingleLittleEndian(b[20..]),
            ModelName: FixChar(b, 24, 13),
            TextureName: FixChar(b, 37, 13),
            Power: BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            BlastPower: BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            BlastRadius: BinaryPrimitives.ReadSingleLittleEndian(b[60..]),
            Speed: BinaryPrimitives.ReadSingleLittleEndian(b[64..]),
            Lifespan: BinaryPrimitives.ReadSingleLittleEndian(b[68..]),
            ProjectileTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[72..]),
            DamageType: b[74],
            AbsoluteF: b[75] != 0,
            Directional: b[76] != 0,
            Width: BinaryPrimitives.ReadSingleLittleEndian(b[80..]),
            AmbientSound: BinaryPrimitives.ReadInt16LittleEndian(b[84..]));
    }

    internal static string CutAtNull(string s)
    {
        var i = s.IndexOf('\0');
        return i < 0 ? s : s[..i];
    }
}
