using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataWarpIGC</c> payload for <c>OT_warp</c> (7). Fixed 108 bytes.
/// Source layout: <c>WarpDef</c> (<c>src/Igc/igc.h:1722</c>) + <c>DataWarpIGC</c>
/// (<c>src/Igc/igc.h:2506</c>), C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_warp.
/// </summary>
/// <remarks>
/// Alignment padding occurs twice. The embedded <c>WarpDef</c> is 32 bytes
/// (five shorts and two 13-char names), then the 25-char <c>Name</c> runs
/// 32-56; three pad bytes (57-59) align the following <c>Vector</c> position
/// to 4. Everything after (<c>forward</c>, <c>rotation</c>, <c>signature</c>,
/// <c>clusterID</c>) lands naturally aligned; two trailing pads round the
/// struct to 108. Verified across all 5212 corpus records — every
/// <c>destinationID</c> resolves to a warp or sector in the same file, and
/// every <c>ClusterId</c> to a real sector.
/// </remarks>
public readonly record struct DataWarpIGC(
    short WarpId,
    short DestinationId,
    short Radius,
    string TextureName,
    string IconName,
    string Name,
    float[] Position,
    float[] Forward,
    float[] Rotation,
    float Signature,
    short ClusterId)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataWarpIGC)</c>.</summary>
    public const int Size = 108;

    /// <summary>
    /// Decode an <c>OT_warp</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataWarpIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataWarpIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));
        static float[] Vec(ReadOnlySpan<byte> s, int off) =>
        [
            BinaryPrimitives.ReadSingleLittleEndian(s[off..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[(off + 8)..]),
        ];

        return new DataWarpIGC(
            WarpId: BinaryPrimitives.ReadInt16LittleEndian(b[0..]),
            DestinationId: BinaryPrimitives.ReadInt16LittleEndian(b[2..]),
            Radius: BinaryPrimitives.ReadInt16LittleEndian(b[4..]),
            TextureName: Fix(b, 6, 13),
            IconName: Fix(b, 19, 13),
            Name: Fix(b, 32, 25),
            Position: Vec(b, 60),
            Forward: Vec(b, 72),
            Rotation:
            [
                BinaryPrimitives.ReadSingleLittleEndian(b[84..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[88..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[92..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[96..]),
            ],
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[100..]),
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[104..]));
    }
}
