using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>
/// <c>DataTreasureIGC</c> — the <b>individual treasure's</b> wire-export
/// struct (<c>src/Igc/igc.h:2517-2529</c>), the payload of the
/// <c>TREASURE</c>(28) message and of <c>CtreasureIGC::Export</c>
/// (<c>treasureIGC.cpp:268-278</c>). It is <b>distinct</b> from
/// <see cref="DataTreasureSetIGC"/>: that struct is the <c>OT_treasureSet</c>
/// <em>catalog</em> record decoded from the static-core corpus, whereas this
/// struct is one live treasure instance carried on the wire when a treasure is
/// spawned / exported. Because treasures are <b>runtime</b> objects (created
/// and destroyed during play, <c>treasureIGC.cpp:22-213</c>) they never appear
/// in the static core — so unlike every other <c>Data*IGC</c> in this folder
/// this decode is measured against the native struct layout
/// (<c>igc.h:2517-2529</c>), not against <c>.igc</c> corpus records. The
/// layout is nevertheless pinned byte-for-byte, with the <c>char</c>
/// <c>treasureCode</c> (not a 4-byte enum) and the <c>bool createNow</c> trap
/// called out below — the same reverse-engineering discipline, just sourced
/// from the reference header rather than corpus measurements.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (<c>igc.h:2517-2529</c>, C++ default alignment — the
/// member widths from igc.h: <c>Vector</c> = 3 floats, <c>Time</c> = uint32
/// ms, <c>ObjectID</c>/<c>TreasureID</c>/<c>SectorID</c> = short, and
/// critically <c>TreasureCode = char</c> at <c>igc.h:2086</c> — a single
/// byte, <b>not</b> a 4-byte C++ enum):</para>
/// <code>
///   offset  0: float[3]  p0            (Vector — world position)
///   offset 12: float[3]  v0            (Vector — initial velocity)
///   offset 24: float     lifespan      (seconds before the treasure expires)
///   offset 28: uint      time0         (Time — creation/export clock)
///   offset 32: short     objectID      (ObjectID)
///   offset 34: short     treasureID    (TreasureID — per-code payload)
///   offset 36: short     clusterID     (SectorID — owning sector)
///   offset 38: short     amount        (cash amount / part count)
///   offset 40: byte      treasureCode  (TreasureCode — a char, igc.h:2086)
///   offset 41: bool      createNow     (1 byte)
///   padding 42-43                       (rounds to the 4-byte largest-member alignment)
///   = 44 bytes
/// </code>
/// <para><b>The two traps.</b> (1) <c>treasureCode</c> is a <c>char</c> — one
/// byte — despite the surrounding shorts making the struct look evenly 4-padded;
/// the adjacent <c>createNow</c> bool then consumes byte 41 and the struct
/// rounds up to 44, not 48. (2) <c>createNow</c> is a genuine trailing bool;
/// when true, <c>CtreasureIGC::Initialize</c> stamps <c>time0 = now</c>
/// (<c>treasureIGC.cpp:35-38</c>). A decode that assumed a 4-byte enum or an
/// even multiple of 4 would mis-slice every field from byte 40 on.</para>
/// <para><b>Cash semantics</b> (<c>treasureIGC.cpp:91-92</c>): for
/// <c>c_tcCash</c>, <c>amount = treasureID</c> — the dollar value is carried
/// in the <c>treasureID</c> slot (500…10000 per the corpus validation of
/// <see cref="DataTreasureSetIGC"/>), and <c>amount</c> is likewise set to it
/// during Initialize. This slice reads <c>Amount</c> as the credit value for
/// the pickup, and the port's tests construct a cash treasure from the real
/// corpus catalog (a genuine <c>Cash</c> <c>treasureID</c>) so the credited
/// figure is a measured amount, not a fabricated one.</para>
/// </remarks>
public readonly record struct DataTreasureIGC(
    float[] P0,
    float[] V0,
    float Lifespan,
    uint Time0,
    short ObjectId,
    short TreasureId,
    short ClusterId,
    short Amount,
    TreasureCode TreasureCode,
    bool CreateNow)
{
    /// <summary>Exact wire/export size in bytes: <c>sizeof(DataTreasureIGC)</c>.</summary>
    public const int Size = 44;

    /// <summary>
    /// Decode a <c>DataTreasureIGC</c> payload. <paramref name="b"/> must be
    /// exactly <see cref="Size"/> bytes.
    /// </summary>
    public static DataTreasureIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataTreasureIGC expects {Size} bytes, got {b.Length}");

        static float[] Vec(ReadOnlySpan<byte> s, int off)
        {
            var v = new float[3];
            for (int i = 0; i < 3; i++)
                v[i] = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(off + 4 * i));
            return v;
        }

        return new DataTreasureIGC(
            P0: Vec(b, 0),
            V0: Vec(b, 12),
            Lifespan: BinaryPrimitives.ReadSingleLittleEndian(b[24..]),
            Time0: BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
            ObjectId: BinaryPrimitives.ReadInt16LittleEndian(b[32..]),
            TreasureId: BinaryPrimitives.ReadInt16LittleEndian(b[34..]),
            ClusterId: BinaryPrimitives.ReadInt16LittleEndian(b[36..]),
            Amount: BinaryPrimitives.ReadInt16LittleEndian(b[38..]),
            TreasureCode: (TreasureCode)b[40],
            CreateNow: b[41] != 0);
    }
}
