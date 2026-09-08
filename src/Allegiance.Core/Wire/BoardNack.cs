namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_BOARD_NACK</c> (id 178, <c>Messages.h:913-915</c>:
/// <c>DEFINE_FEDMSG(S, BOARD_NACK, 178) ShipID sidRequestedParent; END_FEDMSG</c>) —
/// the server's refusal of a <see cref="BoardShip"/> request. Sent <b>only</b>
/// to the requesting player's own connection (<c>pfsPlayer-&gt;GetConnection()</c>,
/// <c>FedSrv.CPP:6412-6416</c>) and only on failure — when the named parent
/// ship is missing/a ghost, or when the board/leave action itself failed.
/// Carries <c>sidRequestedParent</c> = the <c>sidParent</c> from the request
/// (the native leaves this field unset — the <c>pfmBoardNack-&gt;sidRequestedParent;</c>
/// statement at <c>FedSrv.CPP:6414</c> is a no-op — but populating it with the
/// semantically-correct requested id is strictly more useful than the garbage
/// that would otherwise ride on the wire, following <see cref="EnterLifepod"/>'s
/// same rationale).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 178
///   offset 4: short  sidRequestedParent  (ShipID = short)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class BoardNack : IFedMessage
{
    public const ushort Fmid = 178;
    public const int FixedSize = 6;   // header + sidRequestedParent short — no padding (short is 2-aligned)

    /// <summary>The <c>sidParent</c> from the refused <see cref="BoardShip"/> request.</summary>
    public short SidRequestedParent;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidRequestedParent);   // sidRequestedParent (ShipID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BOARD_NACK internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BoardNack Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BOARD_NACK needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BOARD_NACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new BoardNack { SidRequestedParent = r.ReadI16() };
    }
}
