using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataCivilizationIGC</c> payload for <c>OT_civilization</c> (27).
/// Fixed 268 bytes. Source: <c>src/Igc/igc.h:2071</c>, C++ default alignment.
/// See wiki page <c>allegiance-igc-file-format</c> §OT_civilization.
/// </summary>
public readonly record struct DataCivilizationIGC(
    float IncomeMoney,
    float BonusMoney,
    string Name,
    string IconName,
    string HudName,
    byte[] TtbmBaseTechs,
    byte[] TtbmNoDevTechs,
    float[] GasBaseAttributes,
    short Lifepod,
    short CivilizationId,
    short InitialStationTypeId)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataCivilizationIGC)</c>.</summary>
    public const int Size = 268;
    /// <summary>The GlobalAttributeSet has <c>c_gaMax = 25</c> floats.</summary>
    public const int GaCount = 25;

    /// <summary>
    /// Decode an <c>OT_civilization</c> payload.
    /// </summary>
    /// <remarks>
    /// Corpus-measured correction to the wiki table: the <c>gasBaseAttributes</c>
    /// float array is 4-byte aligned, so there is a <b>pad byte at 159</b>
    /// (after the two 50-byte tech bitmasks end at 158) and the array runs
    /// <b>160..259</b>. The three trailing IDs therefore sit at
    /// <b>260/262/264</b>, not 259/261/263 as the wiki's draft table suggested.
    /// Verified: every <c>lifepod</c> and <c>initialStationTypeId</c> decodes to
    /// a hull/station-type ID that exists in the same core.
    /// </remarks>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataCivilizationIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataCivilizationIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var gas = new float[GaCount];
        for (var i = 0; i < GaCount; i++)
            gas[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(160 + 4 * i));

        return new DataCivilizationIGC(
            IncomeMoney: BinaryPrimitives.ReadSingleLittleEndian(b[0..]),
            BonusMoney: BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
            Name: Fix(b, 8, 25),
            IconName: Fix(b, 33, 13),
            HudName: Fix(b, 46, 13),
            TtbmBaseTechs: b.Slice(59, 50).ToArray(),
            TtbmNoDevTechs: b.Slice(109, 50).ToArray(),
            GasBaseAttributes: gas,
            Lifepod: BinaryPrimitives.ReadInt16LittleEndian(b[260..]),
            CivilizationId: BinaryPrimitives.ReadInt16LittleEndian(b[262..]),
            InitialStationTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[264..]));
    }
}
