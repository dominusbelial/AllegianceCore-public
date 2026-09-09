namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SET_MONEY</c> (id 115), the server's "here is a ship's money" sync
/// message (<c>src/Inc/Messages.h:751-754</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, SET_MONEY, 115)
///     Money   money;
///     ShipID  shipID;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — SET_MONEY sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions; <c>Money</c> = <c>AGCMoney</c> = 32-bit
/// <c>long</c> (AGCIDL.idl:246), <c>ShipID</c> = 16-bit <c>short</c>;
/// base <c>FEDMESSAGE</c> = two 16-bit words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 115
///   offset  4: int32  money   (Money, 4-align at 4)
///   offset  8: short  shipID  (ShipID)
///   offset 10: 2 pad bytes (0) — struct trailing alignment
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_S_SET_MONEY)
/// </code>
/// <para>Unlike <see cref="MoneyChange"/> (whose two <c>ShipID</c>s fill the
/// 12-byte struct), the single trailing <c>ShipID</c> leaves the struct at 10
/// field bytes, and the compiler rounds <c>sizeof</c> up to the 4-byte
/// alignment of the <c>Money long</c> member — so <b>12</b> bytes go on the
/// wire, with two zero pad bytes after <c>shipID</c>. The native FM framework
/// transmits <c>cbmsg = sizeof(FMD_S_SET_MONEY)</c> verbatim
/// (<c>messagecore.h:714,724</c>), so the wire is genuinely 12 bytes; the pad
/// is included here to stay byte-faithful to the reference protocol.</para>
/// <para><b>Native call site / wire semantics</b> (P118): the reference server
/// emits <c>SET_MONEY</c> from <c>CFSMission::SendMissionInfo</c>
/// (<c>FedSrv/fsmission.cpp:4270-4299</c>), invoked from
/// <c>CFSMission::AddPlayerToSide</c> (<c>fsmission.cpp:539,736</c>) when a
/// joining/syncing player is told about every other person on the side — one
/// <c>SET_MONEY</c> per teammate, with <c>shipID</c> = that teammate and
/// <c>money</c> = that teammate's per-player wallet
/// (<c>CFSPlayer::GetMoney()</c>). The client handler (<c>appmsg.cpp:1367-1379</c>)
/// resolves the ship by id and sets that player's money.</para>
/// <para><b>Port mapping</b> (standing pooled-<c>Side.Money</c> design, P118):
/// this port tracks one pooled number per side (<c>Model/Side.cs:52</c>), not
/// per-player wallets, so the native per-teammate loop collapses to a single
/// message telling the joining player the side's one shared money value, with
/// <c>shipID</c> = the joining player's own ship (the honest single-number
/// address in this model).</para>
/// </summary>
public sealed class SetMoney : IFedMessage
{
    public const ushort Fmid = 115;

    /// <summary>Wire width of the whole message: header(4) + Money(4) +
    /// ShipID(2) + 2 trailing pad bytes = 12 (native sizeof with default
    /// MSVC packing).</summary>
    public const int FixedSize = 12;

    /// <summary>The money value to sync (<c>Money</c>, native 32-bit). In this
    /// pooled-money port, the side's one shared treasury value.</summary>
    public int Money;

    /// <summary>The ship whose money this is (<c>ShipID</c> = short). In this
    /// port's pooled mapping, the joining player's own ship id.</summary>
    public short ShipId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(Money);          // offset 4
        w.WriteI16(ShipId);         // offset 8
        w.WriteZeros(2);            // offset 10: trailing pad (native sizeof = 12)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_MONEY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetMoney Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_MONEY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_MONEY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new SetMoney { Money = r.ReadI32() };
        msg.ShipId = r.ReadI16();
        r.Advance(2);                               // trailing pad
        return msg;
    }
}
