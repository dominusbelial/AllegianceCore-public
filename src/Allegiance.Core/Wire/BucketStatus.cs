namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_BUCKET_STATUS</c> (id 65), the server's \"here is a team
/// construction bucket's funding/completion state\" message
/// (<c>src/Inc/Messages.h:493-498</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, BUCKET_STATUS, 65)
///     DWORD         timeTotal;
///     SideID        sideID;
///     Money         moneyTotal;
///     BucketID      iBucket;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — BUCKET_STATUS sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions; <c>DWORD</c> = 32-bit unsigned long,
/// <c>SideID</c> = <c>ObjectID</c> = 16-bit short (igc.h:491,477), <c>Money</c>
/// = 32-bit <c>AGCMoney</c>, <c>BucketID</c> = short):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 65
///   offset  4: uint32 timeTotal  (DWORD, 4-align at 4)
///   offset  8: short  sideID     (SideID)
///   offset 10: 2 pad bytes (0)   (sideID then the 4-align Money)
///   offset 12: int32  moneyTotal (Money)
///   offset 16: short  iBucket    (BucketID)
///   offset 18: 2 pad bytes (0)   (struct trailing alignment)
///   = 20 B fixed → cbmsg = 20 = sizeof(FMD_S_BUCKET_STATUS)
/// </code>
/// The interleaving of 2-byte <c>SideID</c>/<c>BucketID</c> shorts with 4-byte
/// <c>DWORD</c>/<c>Money</c> longs forces two interior/trailing alignment pads,
/// so the struct is 20 bytes — the same default-packing discipline P118 applied
/// to <see cref="SetMoney"/>.</para>
/// <para><b>Native call sites</b> (P119, read directly): the reference server
/// broadcasts one <c>BUCKET_STATUS</c> to the whole side's peer group after a
/// donation (<c>FedSrv.CPP:7183-7196</c>) and sends one per bucket during join
/// sync (<c>fsmission.cpp:4260-4273</c>). <c>timeTotal</c> is the bucket's
/// invested build time in milliseconds, <c>moneyTotal</c> its invested money
/// (bucketigc.h:264-272).</para>
/// </summary>
public sealed class BucketStatus : IFedMessage
{
    public const ushort Fmid = 65;

    /// <summary>Wire width of the whole message: header(4) + timeTotal(4) +
    /// sideID(2) + pad(2) + moneyTotal(4) + iBucket(2) + pad(2) = 20.</summary>
    public const int FixedSize = 20;

    /// <summary>The bucket's invested build time in milliseconds
    /// (<c>DWORD</c> = uint32; bucketigc.h:264-267 GetTime()).</summary>
    public uint TimeTotal;

    /// <summary>The side that owns the bucket (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>The bucket's invested money (<c>Money</c>, native 32-bit;
    /// bucketigc.h:269-272 GetMoney()).</summary>
    public int MoneyTotal;

    /// <summary>The team bucket being reported (<c>BucketID</c> = short).</summary>
    public short IBucket;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeTotal);      // offset 4
        w.WriteI16(SideId);         // offset 8
        w.WriteZeros(2);            // offset 10: interior pad (Money 4-align)
        w.WriteI32(MoneyTotal);     // offset 12
        w.WriteI16(IBucket);        // offset 16
        w.WriteZeros(2);            // offset 18: trailing pad
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BUCKET_STATUS internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BucketStatus Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BUCKET_STATUS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BUCKET_STATUS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new BucketStatus { TimeTotal = r.ReadU32() };
        msg.SideId = r.ReadI16();
        r.Advance(2);                               // interior pad
        msg.MoneyTotal = r.ReadI32();
        msg.IBucket = r.ReadI16();
        r.Advance(2);                               // trailing pad
        return msg;
    }
}
