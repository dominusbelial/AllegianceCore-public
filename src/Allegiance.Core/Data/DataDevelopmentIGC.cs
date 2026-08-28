using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataDevelopmentIGC</c> payload for <c>OT_development</c> (32).
/// Fixed 468 bytes. Source: <c>src/Igc/igc.h:2691</c>, C++ default alignment.
/// DataBuyableIGC base (362 B) + <c>gas</c> (GlobalAttributeSet, 25 floats)
/// + <c>developmentID</c> + <c>completionSound</c>. The float array aligns
/// naturally at 364 (no extra pad). See wiki page
/// <c>allegiance-igc-file-format</c> §OT_development.
/// </summary>
public readonly record struct DataDevelopmentIGC(
    int Price,
    uint TimeToBuild,
    string ModelName,
    string IconName,
    string Name,
    string Description,
    byte GroupId,
    byte[] TtbmRequired,
    byte[] TtbmEffects,
    float[] Gas,
    short DevelopmentId,
    short CompletionSound)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataDevelopmentIGC)</c>.</summary>
    public const int Size = 468;
    /// <summary>The GlobalAttributeSet has <c>c_gaMax = 25</c> floats.</summary>
    public const int GaCount = 25;

    /// <summary>
    /// Decode an <c>OT_development</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataDevelopmentIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataDevelopmentIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var gas = new float[GaCount];
        for (var i = 0; i < GaCount; i++)
            gas[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(364 + 4 * i));

        return new DataDevelopmentIGC(
            Price: BinaryPrimitives.ReadInt32LittleEndian(b[0..]),
            TimeToBuild: BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            ModelName: Fix(b, 8, 14),
            IconName: Fix(b, 22, 13),
            Name: Fix(b, 35, 25),
            Description: Fix(b, 60, 201),
            GroupId: b[261],
            TtbmRequired: b.Slice(262, 50).ToArray(),
            TtbmEffects: b.Slice(312, 50).ToArray(),
            Gas: gas,
            DevelopmentId: BinaryPrimitives.ReadInt16LittleEndian(b[464..]),
            CompletionSound: BinaryPrimitives.ReadInt16LittleEndian(b[466..]));
    }
}
