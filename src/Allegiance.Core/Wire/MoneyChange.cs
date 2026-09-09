namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MONEY_CHANGE</c> (id 60), the server's "the side's money changed"
/// message (<c>src/Inc/Messages.h:441-445</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, MONEY_CHANGE, 60)
///     Money     dMoney;
///     ShipID    sidTo;
///     ShipID    sidFrom;    //== NA     means a server initiated change (cheat, probably)
///   END_FEDMSG              //== sidTo  means a player initiated change (purchase, etc.)
///                           //otherwise means a transfer from -> to
/// </code>
/// Layout (default MSVC packing — MONEY_CHANGE sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions at :636-642/:917-925; <c>Money</c> =
/// <c>AGCMoney</c> = 32-bit <c>long</c> (AGCIDL.idl:246), <c>ShipID</c> =
/// <c>ObjectID</c> = 16-bit <c>short</c> (igc.h:477,480); base
/// <c>FEDMESSAGE</c> = two 16-bit words cbmsg+fmid, messagecore.h:43,59,76-87):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 60
///   offset  4: int32  dMoney   (Money, 4-align at 4)
///   offset  8: short  sidTo    (ShipID)
///   offset 10: short  sidFrom  (ShipID)
///   = 12 B fixed, no var items → cbmsg = 12 = sizeof(FMD_S_MONEY_CHANGE)
/// </code>
/// <para>The three <c>Money</c> fields' <c>ShipID</c>s are 16-bit, so there is
/// no interior pad and no trailing pad (the struct rounds to 12, its own
/// natural width) — unlike <see cref="SetMoney"/>, whose trailing <c>ShipID</c>
/// leaves two pad bytes.</para>
/// <para><b>Native call sites / wire semantics</b> (P118, read directly from
/// <c>FedSrv.CPP</c>): the reference server fires this when a side's money is
/// mutated, almost always broadcasting to the <em>whole side's peer group</em>
/// (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>) rather than to the affected
/// player alone — e.g. the board-refund (<c>FedSrv.CPP:2435-2441</c>, with
/// <c>sidFrom = NA</c>) and the teleport refund (<c>FedSrv.CPP:7202-7206</c>,
/// <c>sidFrom = NA</c>). <c>sidFrom = NA</c> marks a server-initiated change
/// (this port's pooled-treasury refunds), <c>sidFrom == sidTo</c> a
/// player-initiated purchase, and otherwise a player-to-player transfer.</para>
/// </summary>
public sealed class MoneyChange : IFedMessage
{
    public const ushort Fmid = 60;

    /// <summary>Wire width of the whole message: header(4) + Money(4) +
    /// two ShipIDs(2 each) = 12. No alignment gaps.</summary>
    public const int FixedSize = 12;

    /// <summary>The signed money delta (<c>Money</c>, native 32-bit). Positive
    /// for a refund/credit, negative for a purchase deduction.</summary>
    public int DMoney;

    /// <summary>The recipient ship of the change (the refunded/purchasing ship).</summary>
    public short SidTo;

    /// <summary><c>NA</c> = -1 marks a server-initiated change; == <c>SidTo</c>
    /// a player-initiated purchase; otherwise a transfer from → to.</summary>
    public short SidFrom;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(DMoney);         // offset 4
        w.WriteI16(SidTo);          // offset 8
        w.WriteI16(SidFrom);        // offset 10
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MONEY_CHANGE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MoneyChange Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MONEY_CHANGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MONEY_CHANGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new MoneyChange { DMoney = r.ReadI32() };
        msg.SidTo = r.ReadI16();
        msg.SidFrom = r.ReadI16();
        return msg;
    }
}
