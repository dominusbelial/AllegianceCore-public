namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_PLAYER_DONATE</c> (id 67), the client's manual, direct
/// player-to-player gift (<c>src/Inc/Messages.h:527-530</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(C, PLAYER_DONATE, 67)
///     Money     moneyGiven;
///     ShipID    shipID;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — PLAYER_DONATE sits at Messages.h:527, well
/// before the first <c>#pragma pack(1)</c> region at :637; <c>Money</c> =
/// <c>AGCMoney</c> = 32-bit <c>long</c> (AGCIDL.idl:246), <c>ShipID</c> =
/// <c>ObjectID</c> = 16-bit <c>short</c> (igc.h:477,480); base
/// <c>FEDMESSAGE</c> = two 16-bit words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 67
///   offset  4: int32  moneyGiven  (Money, 4-align at 4)
///   offset  8: short  shipID      (ShipID)
///   offset 10: 2 pad bytes (0) — struct trailing alignment
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_C_PLAYER_DONATE)
/// </code>
/// Exactly the <see cref="SetMoney"/> shape (Money then a single trailing
/// ShipID): the struct rounds up to the 4-byte alignment of the <c>Money</c>
/// member, so two zero pad bytes follow <c>shipID</c> and the wire is 12 B.
/// </summary>
/// <remarks>
/// <b>Wire semantics</b> (P203; native dispatch at
/// <c>FedSrv.CPP:7213-7262</c>): the sender gives <c>moneyGiven</c> directly
/// to the ship named by <c>shipID</c>, provided that ship is on the same side
/// or an allied side and the sender's own wallet (P197
/// <see cref="Allegiance.Server.PlayerWallet"/>) covers it. This is a manual,
/// one-shot gift — deliberately independent of <c>AUTODONATE(112)</c>'s
/// standing relationship. On a real transfer the server debits the sender,
/// credits the recipient, and broadcasts <c>MONEY_CHANGE(60)</c> to the
/// sender's side group always, plus the recipient's side group when the gift
/// crossed sides; a bounced donation echoes a no-op <c>MONEY_CHANGE</c>
/// (<c>sidFrom = NA</c>) to the sender alone with no wallet movement.
/// </remarks>
public sealed class PlayerDonate : IFedMessage
{
    public const ushort Fmid = 67;

    /// <summary>Wire width of the whole message: header(4) + Money(4) +
    /// ShipID(2) + 2 trailing pad bytes = 12 (native sizeof with default
    /// MSVC packing).</summary>
    public const int FixedSize = 12;

    /// <summary>The amount the sender is giving (<c>Money</c>, native 32-bit).</summary>
    public int MoneyGiven;

    /// <summary>The recipient ship's id (<c>ShipID</c> = short).</summary>
    public short ShipID;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(MoneyGiven);     // offset 4
        w.WriteI16(ShipID);         // offset 8
        w.WriteZeros(2);            // offset 10: trailing pad (native sizeof = 12)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PLAYER_DONATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PlayerDonate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PLAYER_DONATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PLAYER_DONATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new PlayerDonate { MoneyGiven = r.ReadI32() };
        msg.ShipID = r.ReadI16();
        r.Advance(2);                               // trailing pad
        return msg;
    }
}
