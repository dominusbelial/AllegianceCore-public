using System.Buffers.Binary;
using System.Text;

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
}
