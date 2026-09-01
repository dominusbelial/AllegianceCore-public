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

    /// <summary>
    /// Encode an <c>OT_station</c> payload — the inverse of
    /// <see cref="Decode"/> — producing exactly <see cref="Size"/> bytes.
    /// Field layout mirrors <c>Decode</c> verbatim (offsets 0-86 + the trailing
    /// pad byte at 87 rounding to 88). Following the reference engine's
    /// <c>CstationIGC::Export</c> (<c>src/Igc/stationIGC.cpp:89-113</c>),
    /// which copies stationTypeID/stationID, position/up/forward/rotation,
    /// clusterID, sideID and the two BP fractions straight through with no
    /// normalization, there is <b>no always-false-on-export field</b> for a
    /// station (contrast <c>DataClusterIGC.activeF</c>): every field is raw
    /// passthrough.
    /// <list type="number">
    /// <item><b>Numeric fields pass through raw.</b> the four geometry groups
    /// (three triples + a quad), the four 2-byte ids and the two BP fraction
    /// bytes are written exactly as held.</item>
    /// <item><b>The string field (<c>Name</c>, 25) is zero-padded, not
    /// NUL-forced</b> — a fixed <c>char[25]</c>, exactly as
    /// <see cref="DataClusterIGC.Encode"/> writes its name fields.
    /// <see cref="Decode"/> trims via <c>CutAtNull</c>, so round-trips hold
    /// for names shorter than their buffer.</item>
    /// </list>
    /// The resulting bytes satisfy <c>Decode(Encode(this)) == this</c> for
    /// names shorter than their buffer (the sole asymmetry: a full-length name
    /// cannot round-trip, matching <see cref="Decode"/>'s trim semantics).
    /// </summary>
    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0 + 4 * i),
                Position is { Length: 3 } p ? p[i] : 0f);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12 + 4 * i),
                Up is { Length: 3 } u ? u[i] : 0f);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24 + 4 * i),
                Forward is { Length: 3 } f ? f[i] : 0f);
        }
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36 + 4 * i),
                Rotation is { Length: 4 } r ? r[i] : 0f);

        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(52), ClusterId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(54), SideId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(56), StationId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(58), StationTypeId);
        b[60] = BpHull;
        b[61] = BpShield;

        int n = Math.Min(Name?.Length ?? 0, 25);
        if (n > 0) Encoding.ASCII.GetBytes(Name![..n]).CopyTo(b, 62);
        // b[62+n..87] remain zero (zero-pad, no forced NUL); b[87] pad to 88
        return b;
    }
}
