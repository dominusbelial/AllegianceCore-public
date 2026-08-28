using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataStationIGC</c> payload for <c>OT_station</c> (1). Fixed 88 bytes.
/// Source layout: <c>DataStationIGC</c> (<c>src/Igc/igc.h:2634</c>), C++
/// default alignment. See wiki page <c>allegiance-igc-file-format</c>
/// §OT_station.
/// </summary>
/// <remarks>
/// All geometry (three <c>Vector</c>s and a <c>Rotation</c>) is 4-byte
/// aligned and the tail is five 2-byte IDs followed by two single bytes and a
/// 25-char name, so there are no narrow-to-wide transitions to pad: the only
/// wasted byte is the trailing pad at 87 rounding the struct to 88. Offsets
/// verified across all 289 corpus records — every <c>clusterID</c> names a
/// sector present in the same map and every <c>stationTypeID</c> resolves to
/// an <c>OT_stationType</c> in the static core.
/// </remarks>
public readonly record struct DataStationIGC(
    float[] Position,
    float[] Up,
    float[] Forward,
    float[] Rotation,
    short ClusterId,
    short SideId,
    short StationId,
    short StationTypeId,
    byte BpHull,
    byte BpShield,
    string Name)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataStationIGC)</c>.</summary>
    public const int Size = 88;

    /// <summary>
    /// Decode an <c>OT_station</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataStationIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataStationIGC expects {Size} bytes, got {b.Length}");

        static float[] Vec(ReadOnlySpan<byte> s, int off) =>
        [
            BinaryPrimitives.ReadSingleLittleEndian(s[off..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 8)..]),
        ];

        return new DataStationIGC(
            Position: Vec(b, 0),
            Up: Vec(b, 12),
            Forward: Vec(b, 24),
            Rotation:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[36..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[44..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
            ],
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[52..]),
            SideId: BinaryPrimitives.ReadInt16LittleEndian(b[54..]),
            StationId: BinaryPrimitives.ReadInt16LittleEndian(b[56..]),
            StationTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[58..]),
            BpHull: b[60],
            BpShield: b[61],
            Name: DataProjectileTypeIGC.CutAtNull(
                Encoding.ASCII.GetString(b.Slice(62, 25))));
    }
}
