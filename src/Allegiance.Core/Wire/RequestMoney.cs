namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_REQUEST_MONEY</c> (id 182), the client's \"please send me money\"
/// request (<c>src/Inc/Messages.h:934-938</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(CS, REQUEST_MONEY, 182)
///     Money               amount;
///     ShipID              shipidRequest;
///     HullID              hidFor;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — REQUEST_MONEY sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions; <c>Money</c> = <c>AGCMoney</c> = 32-bit
/// <c>long</c> (AGCIDL.idl:246), <c>ShipID</c>/<c>HullID</c> = <c>ObjectID</c>
/// = 16-bit <c>short</c> (igc.h:477,480,483); base <c>FEDMESSAGE</c> = two
/// 16-bit words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 182
///   offset  4: int32  amount         (Money, 4-align at 4)
///   offset  8: short  shipidRequest  (ShipID)
///   offset 10: short  hidFor         (HullID)
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_CS_REQUEST_MONEY)
/// </code>
/// Unlike <see cref="AutoDonate"/> (ShipID then Money ⇒ pad between them),
/// here <c>amount</c> comes FIRST, so <c>shipidRequest</c> and <c>hidFor</c>
/// follow contiguously at offsets 8/10 with no interior pad, and 12 is
/// already 4-aligned — no trailing pad either.
/// </summary>
/// <remarks>
/// <b>Direction / wire semantics</b> (P199; native dispatch at
/// <c>FedSrv.CPP:5361-5406</c>): this is a <c>CS</c> message. The client sends
/// <c>amount</c> (what it needs) and <c>hidFor</c> (optionally which hull the
/// money is for — decode-only UI context for the recipient; the server does
/// not validate it). The server fills in <c>shipidRequest</c> = the <em>sender's</em>
/// ship id and forwards the request unmodified otherwise to exactly one
/// resolved recipient connection: the sender's current
/// <see cref="Allegiance.Server.PlayerWallet.AutoDonateTarget"/> when that
/// player's wallet holds at least <c>amount</c>, otherwise the richest other
/// teammate on the sender's side (ties → first-to-maximum via native's
/// <c>&gt;</c> comparison). The handler <b>never transfers money</b> — it only
/// forwards the request; the recipient is expected to respond separately (e.g.
/// with an <c>AUTODONATE(112)</c> gift).
/// </remarks>
public sealed class RequestMoney : IFedMessage
{
    public const ushort Fmid = 182;

    /// <summary>Wire width of the whole message: header(4) + Money(4) +
    /// ShipID(2) + HullID(2) = 12 (native sizeof with default MSVC packing).</summary>
    public const int FixedSize = 12;

    /// <summary>The money being requested (<c>Money</c>, native 32-bit).</summary>
    public int Amount;

    /// <summary>Filled in by the server with the <em>requester's</em> ship id
    /// before forwarding (<c>ShipID</c> = short). Sent as-is (ignored) by the
    /// client.</summary>
    public short ShipIdRequest;

    /// <summary>Optionally which hull the money is for (<c>HullID</c> = short).
    /// Decode-only payload for the recipient's UI; the server neither validates
    /// nor modifies it.</summary>
    public short HidFor;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(Amount);         // offset 4
        w.WriteI16(ShipIdRequest);  // offset 8
        w.WriteI16(HidFor);         // offset 10
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"REQUEST_MONEY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static RequestMoney Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"REQUEST_MONEY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected REQUEST_MONEY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new RequestMoney { Amount = r.ReadI32() };
        msg.ShipIdRequest = r.ReadI16();
        msg.HidFor = r.ReadI16();
        return msg;
    }
}
