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
    /// <summary>Andon aleph mass limit, parsed from the map definition by the
    /// native runtime (<c>warpIGC.cpp:67-99</c>; <c>igc.h:4177</c>). It is NOT
    /// part of the fixed 108-byte <c>DataWarpIGC</c> payload, so it defaults to
    /// <c>-1</c> (unlimited) and must be supplied by the map loader/test harness
    /// when a limit is desired.</summary>
    public float MassLimit { get; init; } = -1f;

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

    /// <summary>
    /// Encode an <c>OT_warp</c> payload — the inverse of
    /// <see cref="Decode"/> — producing exactly <see cref="Size"/> bytes.
    /// Field layout mirrors <c>Decode</c> verbatim (offsets 0-105 + 2 pad bytes
    /// to round to 108). Following the reference engine's
    /// <c>CwarpIGC::Export</c> (<c>src/Igc/warpIGC.cpp:112-130</c>), which
    /// copies the warp-definition and the runtime position/orientation/
    /// signature/cluster straight through with no normalization, there is
    /// <b>no always-false-on-export field</b> for a warp (contrast
    /// <c>DataClusterIGC.activeF</c>): every field is raw passthrough.
    /// <list type="number">
    /// <item><b>Numeric fields pass through raw.</b> ids, radius, the four
    /// <c>Vector</c>-sized position/forward/rotation groups and the scalar
    /// signature are written exactly as held.</item>
    /// <item><b>String fields are zero-padded, not NUL-forced</b> — fixed
    /// <c>char[n]</c> buffers (<c>TextureName</c>/<c>IconName</c> 13,
    /// <c>Name</c> 25) exactly as <see cref="DataClusterIGC.Encode"/>
    /// writes them. <see cref="Decode"/> trims via <c>CutAtNull</c>, so
    /// round-trips hold for names shorter than their buffer.</item>
    /// </list>
    /// The resulting bytes satisfy <c>Decode(Encode(this)) == this</c> for
    /// names shorter than their buffer (the sole asymmetry: a full-length name
    /// cannot round-trip, matching <see cref="Decode"/>'s trim semantics).
    /// </summary>
    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0), WarpId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), DestinationId);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), Radius);

        WriteField(b, 6, 13, TextureName);
        WriteField(b, 19, 13, IconName);
        WriteField(b, 32, 25, Name);
        // b[57..60] remain zero (pad aligning the following Vector to 4)

        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(60 + 4 * i),
                Position is { Length: 3 } p ? p[i] : 0f);
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(72 + 4 * i),
                Forward is { Length: 3 } f ? f[i] : 0f);
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(84 + 4 * i),
                Rotation is { Length: 4 } r ? r[i] : 0f);

        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(100), Signature);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(104), ClusterId);
        // b[106..107] remain zero (pad to 108)
        return b;
    }

    private static void WriteField(byte[] b, int start, int count, string s)
    {
        int n = Math.Min(s?.Length ?? 0, count);
        if (n > 0) Encoding.ASCII.GetBytes(s![..n]).CopyTo(b, start);
        // trailing bytes of the fixed buffer stay zero (zero-pad, no forced NUL)
    }
}
