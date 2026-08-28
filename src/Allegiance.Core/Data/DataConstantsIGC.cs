using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>
/// <c>Constants</c> payload for <c>OT_constants</c> (34). Fixed 1760 bytes.
/// Source: <c>src/Igc/missionigc.h:21</c>, C++ default alignment. All fields
/// are floats, so there is no intra-struct padding and offsets are trivial.
/// One <c>OT_constants</c> record per static core (99 in the corpus).
/// </summary>
/// <remarks>
/// Layout is <c>float floatConstants[c_fcidMax]</c> then
/// <c>float damageConstants[c_dmgidMax][c_defidMax]</c> with both dimensions
/// equal to 20 (<c>src/Igc/igc.h:565,568</c>). The damage matrix is row-major
/// [dmgid][defid]; column 0 (asteroid defense) is conventionally zeroed per
/// row in shipped cores, but that is data policy, not layout.
/// </remarks>
public readonly record struct DataConstantsIGC(
    float[] FloatConstants,
    float[][] DamageConstants)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(Constants)</c> = 1760.</summary>
    public const int Size = 1760;
    /// <summary><c>c_fcidMax</c> = 40 (<c>src/Igc/igc.h:107</c>).</summary>
    public const int FcidMax = 40;
    /// <summary><c>c_dmgidMax</c> = 20 (<c>src/Igc/igc.h:565</c>).</summary>
    public const int DmgidMax = 20;
    /// <summary><c>c_defidMax</c> = 20 (<c>src/Igc/igc.h:568</c>).</summary>
    public const int DefidMax = 20;
    /// <summary>Base offset of the damage matrix, after the 40 float constants.</summary>
    public const int DamageOffset = FcidMax * 4;

    /// <summary>
    /// Decode an <c>OT_constants</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataConstantsIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataConstantsIGC expects {Size} bytes, got {b.Length}");

        var fc = new float[FcidMax];
        for (var i = 0; i < FcidMax; i++)
            fc[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));

        var dc = new float[DmgidMax][];
        for (var i = 0; i < DmgidMax; i++)
        {
            dc[i] = new float[DefidMax];
            for (var j = 0; j < DefidMax; j++)
                dc[i][j] = BinaryPrimitives.ReadSingleLittleEndian(
                    b.Slice(DamageOffset + 4 * (i * DefidMax + j)));
        }

        return new DataConstantsIGC(FloatConstants: fc, DamageConstants: dc);
    }
}
