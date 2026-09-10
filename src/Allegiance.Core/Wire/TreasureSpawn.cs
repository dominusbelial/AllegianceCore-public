using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_TREASURE</c> (id 28), the server's \"a treasure is here\" message
/// (<c>src/Inc/Messages.h:253-255</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, TREASURE, 28)
///     DataTreasureIGC   dataTreasure;
///   END_FEDMSG
/// </code>
/// The message embeds the <b>entire</b> <see cref="DataTreasureIGC"/> export
/// struct (igc.h:2517-2529) verbatim — the same struct
/// <c>CtreasureIGC::Export</c> hands out (<c>treasureIGC.cpp:268-278</c>),
/// sized <c>sizeof(DataTreasureIGC)</c>. It is the per-instance spawn/export
/// notice: when a treasure is created the server serializes its full
/// <c>DataTreasureIGC</c> into this frame so a client can reconstruct the
/// object (<c>CtreasureIGC::Initialize</c> takes that same struct, igc.h
/// treasureIGC.cpp:22-28). Carries no id list beyond the embedded struct's own
/// <c>objectID</c>/<c>clusterID</c>/<c>treasureID</c>/<c>amount</c>/<c>treasureCode</c>.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (default MSVC packing — TREASURE at :253 sits outside
/// Messages.h's two <c>#pragma pack(1)</c> regions at :636-642 and :917-925;
/// the struct aligns to its largest member, the 4-aligned
/// <see cref="DataTreasureIGC"/>):</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 28
///   offset 4: DataTreasureIGC dataTreasure  (44 bytes, 4-aligned → starts at 4)
///   = 48 bytes total (cbmsg = 48)
/// </code>
/// <para><b>Pickup relationship (this slice)</b>: P123 wires the cash-pickup
/// mechanic end-to-end but, per the task's explicit scope, uses a single
/// <em>explicitly-placed</em> test treasure (mirroring how mine/probe tests
/// are already set up) rather than the real randomized spawn-table algorithm
/// (a separate later task). The <c>TREASURE</c>(28) spawn broadcast itself is
/// therefore <b>built but not yet emitted</b> by this port — its send side is
/// exactly the deferred spawn system (see the P123 report's spawn-trigger
/// finding). The codec is nevertheless complete and byte-exact so the future
/// spawn task (and the pickup tests) can rely on it.</para>
/// <para><b>Name note</b>: the class is <c>TreasureSpawn</c> (not <c>Treasure</c>)
/// because the port's sim object for a live treasure is already
/// <see cref="Allegiance.Core.Sim.Treasure"/> (mirroring <c>Mine</c>/<c>Probe</c>);
/// keeping both named <c>Treasure</c> would be ambiguous wherever the host
/// references both namespaces.</para>
/// </remarks>
public sealed class TreasureSpawn : IFedMessage
{
    public const ushort Fmid = 28;

    /// <summary>Wire width: header(4) + <see cref="DataTreasureIGC.Size"/>(44).</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + DataTreasureIGC.Size;

    /// <summary>The embedded treasure instance (the whole export struct).</summary>
    public DataTreasureIGC Data;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        var d = Data;
        foreach (var f in d.P0) w.WriteI32(BitConverter.SingleToInt32Bits(f));   // offset 4-15
        foreach (var f in d.V0) w.WriteI32(BitConverter.SingleToInt32Bits(f));   // 16-27
        w.WriteI32(BitConverter.SingleToInt32Bits(d.Lifespan));                  // 28-31
        w.WriteU32(d.Time0);                                                     // 32-35
        w.WriteI16(d.ObjectId);                                                  // 36-37
        w.WriteI16(d.TreasureId);                                                // 38-39
        w.WriteI16(d.ClusterId);                                                 // 40-41
        w.WriteI16(d.Amount);                                                    // 42-43
        w.WriteByte((byte)d.TreasureCode);                                       // 44
        w.WriteByte(d.CreateNow ? (byte)1 : (byte)0);                            // 45
        w.WriteByte(0);                                                          // 46-47: struct trailing pad to 44
        w.WriteByte(0);

        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"TREASURE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static TreasureSpawn Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TREASURE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TREASURE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var d = buf.Slice(4, DataTreasureIGC.Size);
        static float[] Vec(ReadOnlySpan<byte> s, int off)
        {
            var v = new float[3];
            for (int i = 0; i < 3; i++)
                v[i] = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(s.Slice(off + 4 * i)));
            return v;
        }

        return new TreasureSpawn
        {
            Data = new DataTreasureIGC(
                P0: Vec(d, 0),
                V0: Vec(d, 12),
                Lifespan: BitConverter.Int32BitsToSingle(BitConverter.ToInt32(d.Slice(24))),
                Time0: BinaryPrimitives.ReadUInt32LittleEndian(d[28..]),
                ObjectId: BinaryPrimitives.ReadInt16LittleEndian(d[32..]),
                TreasureId: BinaryPrimitives.ReadInt16LittleEndian(d[34..]),
                ClusterId: BinaryPrimitives.ReadInt16LittleEndian(d[36..]),
                Amount: BinaryPrimitives.ReadInt16LittleEndian(d[38..]),
                TreasureCode: (TreasureCode)d[40],
                CreateNow: d[41] != 0),
        };
    }
}
