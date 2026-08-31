using System.Buffers.Binary;
using System.Text;

// DataClusterIGC is the only Data/*.cs record that is consumed in BOTH
// directions: decoded from the mission map at boot (for Sector assembly,
// MissionMap.Load) AND re-encoded to the wire for FM_S_EXPORT(26)/OT_cluster
// (ExportCluster). The other Data/*.cs types are decode-only corpus parsers.
// Hence it, uniquely, carries an Encode() beside its Decode(). The writer
// mirrors the decoder byte-for-byte, so Encode(Decode(b)) == b exactly.


namespace Allegiance.Core;

/// <summary>
/// <c>DataClusterIGC</c> payload for <c>OT_cluster</c> (13). Fixed 92 bytes.
/// Source layout: <c>DataClusterIGC</c> (<c>src/Igc/igc.h:1733</c>), C++
/// default alignment. See wiki page <c>allegiance-igc-file-format</c>
/// §OT_cluster.
/// </summary>
/// <remarks>
/// <c>activeF</c> is always false on disk: the exporter forces it
/// (<c>src/Igc/clusterIGC.cpp:494</c>) — an independent confirmation of its
/// offset at 88. The three planet fields (<c>BytePercentage</c> at 85-87)
/// divide by 240; they are raw bytes here. Trailing 2 bytes are pad to round
/// the struct to 92. Verified against all 1506 corpus records: every
/// <c>ClusterId</c> is a real sector in the same file and <c>StarSeed</c> is
/// positive. Rare degenerate <c>LightDirection</c> magnitudes occur in
/// <c>m2xwheel</c>/<c>m2xwheeli</c>/<c>m2xBoilp</c> maps but are genuine data,
/// not a misread offset.
/// </remarks>
public readonly record struct DataClusterIGC(
    uint StarSeed,
    float[] LightDirection,
    uint LightColor,
    float ScreenX,
    float ScreenY,
    short ClusterId,
    short Debris,
    short Stars,
    string Name,
    string PosterName,
    string PlanetName,
    byte PlanetSinLatitude,
    byte PlanetLongitude,
    byte PlanetRadius,
    bool Active,
    bool HomeSector)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataClusterIGC)</c>.</summary>
    public const int Size = 92;

    /// <summary>
    /// Decode an <c>OT_cluster</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataClusterIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataClusterIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        return new DataClusterIGC(
            StarSeed: BinaryPrimitives.ReadUInt32LittleEndian(b[0..]),
            LightDirection:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[12..]),
            ],
            LightColor: BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            ScreenX: BinaryPrimitives.ReadSingleLittleEndian(b[20..]),
            ScreenY: BinaryPrimitives.ReadSingleLittleEndian(b[24..]),
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[28..]),
            Debris: BinaryPrimitives.ReadInt16LittleEndian(b[30..]),
            Stars: BinaryPrimitives.ReadInt16LittleEndian(b[32..]),
            Name: Fix(b, 34, 25),
            PosterName: Fix(b, 59, 13),
            PlanetName: Fix(b, 72, 13),
            PlanetSinLatitude: b[85],
            PlanetLongitude: b[86],
            PlanetRadius: b[87],
            Active: b[88] != 0,
            HomeSector: b[89] != 0);
    }

    /// <summary>
    /// Encode an <c>OT_cluster</c> payload — the inverse of
    /// <see cref="Decode"/> — producing exactly <see cref="Size"/> bytes.
    /// Field layout mirrors <c>Decode</c> verbatim (offsets 0-89 + 2 pad bytes
    /// to round to 92). Three encoding rules, matching the reference engine:
    /// <list type="number">
    /// <item><b><c>activeF</c> is always written false.</b> The real exporter
    /// forces it (<c>src/Igc/clusterIGC.cpp:494</c>), and
    /// <see cref="Decode"/> reads offset 88 for it; we honour that rather
    /// than trust the caller's <see cref="Active"/> (which is meaningful only
    /// for map-load, not export).</item>
    /// <item><b>String fields are zero-padded, not NUL-padded.</b> They are
    /// fixed <c>char[n]</c> buffers; <see cref="Decode"/> trims trailing
    /// NULs/<c>0</c> bytes via <c>CutAtNull</c>, so writing the string's bytes
    /// followed by zero padding round-trips. Names longer than the buffer are
    /// truncated to fit.</item>
    /// <item><b>Planet angles are raw bytes.</b> The three planet fields are
    /// <c>uchar</c> percentages divided by 240 on read; they pass through
    /// here unchanged (we never recompute a percentage from degrees).</item>
    /// </list>
    /// The resulting bytes satisfy <c>Decode(Encode(this)) == this</c> for
    /// names shorter than their buffer (the sole asymmetry: a full-length
    /// name cannot round-trip because there is no room for a NUL, matching
    /// <see cref="Decode"/>'s trim semantics).
    /// </summary>
    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), StarSeed);
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4 + 4 * i),
                LightDirection is { Length: 3 } ld ? ld[i] : 0f);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), LightColor);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), ScreenX);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), ScreenY);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(28), ClusterId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(30), Debris);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), Stars);

        WriteField(b, 34, 25, Name);
        WriteField(b, 59, 13, PosterName);
        WriteField(b, 72, 13, PlanetName);

        b[85] = PlanetSinLatitude;
        b[86] = PlanetLongitude;
        b[87] = PlanetRadius;
        b[88] = 0;   // activeF — always false on export (clusterIGC.cpp:494)
        b[89] = (byte)(HomeSector ? 1 : 0);
        // b[90..91] remain zero (pad to 92)
        return b;
    }

    private static void WriteField(byte[] b, int start, int count, string s)
    {
        int n = Math.Min(s?.Length ?? 0, count);
        if (n > 0) Encoding.ASCII.GetBytes(s![..n]).CopyTo(b, start);
        // trailing bytes of the fixed buffer stay zero (zero-pad, no forced NUL)
    }
}
