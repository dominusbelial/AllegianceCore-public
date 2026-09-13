namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_AUTODONATE</c> (id 116), the server's broadcast of a player's
/// auto-donate relationship (<c>src/Inc/Messages.h:756-759</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, AUTODONATE, 116)
///     ShipID      sidDonateBy;
///     ShipID      sidDonateTo;
///     Money       amount;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — AUTODONATE sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions; two 16-bit <c>ShipID</c>s then a 32-bit
/// <c>Money</c>; base <c>FEDMESSAGE</c> = two 16-bit words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 116
///   offset  4: short  sidDonateBy  (ShipID)
///   offset  6: short  sidDonateTo  (ShipID)
///   offset  8: int32  amount       (Money)
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_S_AUTODONATE)
/// </code>
/// Two <c>ShipID</c>s fill the header-to-4 gap exactly, so the single 4-byte
/// <c>Money</c> lands naturally at offset 8 with <b>no pad</b> — the same
/// no-interior-gap consequence the port already documented for
/// <see cref="MoneyChange"/> (whose two <c>ShipID</c>s likewise round to 12).</para>
/// <para><b>Wire semantics</b> (P197; emitted by
/// <c>CFSPlayer::SetAutoDonate</c>, <c>fsship.cpp:1343-1352</c>): tells the
/// side's peers that <c>sidDonateBy</c> now auto-donates to
/// <c>sidDonateTo</c> (<c>NA</c> = -1 means "donates to no one"). <c>amount</c>
/// is the one-time gift just transferred (0 when no gift). The broadcast is
/// scoped to the donor's <em>side group</em> (<c>CFSSide::FromIGC(
/// GetSide())->GetGroup()</c>), not the whole mission.</para>
/// </summary>
public sealed class AutoDonateUpdate : IFedMessage
{
    public const ushort Fmid = 116;

    /// <summary>Wire width of the whole message: header(4) + two ShipIDs(2
    /// each) + Money(4) = 12. No alignment gaps.</summary>
    public const int FixedSize = 12;

    /// <summary>The ship whose relationship this is (<c>ShipID</c> = short,
    /// the donor).</summary>
    public short SidDonateBy;

    /// <summary>The ship being donated to (<c>ShipID</c> = short), or
    /// <c>NA</c> (-1) for "donates to no one".</summary>
    public short SidDonateTo;

    /// <summary>One-time gift just transferred (<c>Money</c>, native 32-bit),
    /// 0 when none.</summary>
    public int Amount;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidDonateBy);    // offset 4
        w.WriteI16(SidDonateTo);    // offset 6
        w.WriteI32(Amount);         // offset 8
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"AUTODONATE(S) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AutoDonateUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"AUTODONATE(S) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected AUTODONATE(S) fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new AutoDonateUpdate { SidDonateBy = r.ReadI16() };
        msg.SidDonateTo = r.ReadI16();
        msg.Amount = r.ReadI32();
        return msg;
    }
}
