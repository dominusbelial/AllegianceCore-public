namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_BUCKET_DONATE</c> (id 64), the client's \"donate money into a team
/// construction bucket\" message (<c>src/Inc/Messages.h:488-491</c>), quoted in
/// full:
/// <code>
///   DEFINE_FEDMSG(C, BUCKET_DONATE, 64)
///     Money         moneyGiven;
///     BucketID      iBucket;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — BUCKET_DONATE sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions at :636-642/:917-925; <c>Money</c> =
/// <c>AGCMoney</c> = 32-bit <c>long</c> (AGCIDL.idl:246), <c>BucketID</c> = a
/// <c>typedef short</c> (Messages.h:487); base <c>FEDMESSAGE</c> = two 16-bit
/// words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 64
///   offset  4: int32  moneyGiven (Money, 4-align at 4)
///   offset  8: short  iBucket    (BucketID)
///   offset 10: 2 pad bytes (0) — struct trailing alignment
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_C_BUCKET_DONATE)
/// </code>
/// The single trailing <c>BucketID</c> leaves the struct at 10 field bytes,
/// and the compiler rounds <c>sizeof</c> up to the 4-byte alignment of the
/// <c>Money long</c> member — exactly the <see cref="SetMoney"/> trailing-pad
/// rule (P118) — so <b>12</b> bytes go on the wire with two zero pad bytes
/// after <c>iBucket</c>. The native FM framework transmits
/// <c>cbmsg = sizeof(...)</c> verbatim (messagecore.h:714,724).</para>
/// <para><b>Native call site / wire semantics</b> (P119, read directly from
/// <c>FedSrv.CPP:7137-7211</c> <c>case FM_C_BUCKET_DONATE</c>): the handler
/// gates on <c>STAGE_STARTED</c>, resolves <c>pside-&gt;GetBucket(iBucket)</c>,
/// traps hack boots, then — if the side can still build the bucket — calls
/// <c>pbucket-&gt;AddMoney(moneyGiven)</c> (which caps the investment at the
/// price still owed and returns the actually-spent amount, bucketigc.h:281-293),
/// deducts only that spent amount from the donor, refunds the excess, and
/// broadcasts a <c>BUCKET_STATUS(65)</c> to the whole side's peer group.</para>
/// </summary>
public sealed class BucketDonate : IFedMessage
{
    public const ushort Fmid = 64;

    /// <summary>Wire width of the whole message: header(4) + Money(4) +
    /// BucketID(2) + 2 trailing pad bytes = 12 (native sizeof with default
    /// MSVC packing).</summary>
    public const int FixedSize = 12;

    /// <summary>The amount of money the donor offers into the bucket
    /// (<c>Money</c>, native 32-bit). The server only takes what the bucket
    /// still needs and refunds the rest (FedSrv.CPP:7151-7160).</summary>
    public int MoneyGiven;

    /// <summary>The team bucket to fund (<c>BucketID</c> = short, a team-scoped
    /// id — not personal).</summary>
    public short IBucket;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(MoneyGiven);     // offset 4
        w.WriteI16(IBucket);        // offset 8
        w.WriteZeros(2);            // offset 10: trailing pad (native sizeof = 12)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BUCKET_DONATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BucketDonate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BUCKET_DONATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BUCKET_DONATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new BucketDonate { MoneyGiven = r.ReadI32() };
        msg.IBucket = r.ReadI16();
        r.Advance(2);                               // trailing pad
        return msg;
    }
}
