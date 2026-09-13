namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_VOTE</c> (id 19, <c>Messages.h:185-188</c>): a client records its
/// vote on a pending ballot. The only client-sendable message in the
/// ballot cluster (P187) — the server relays neither BALLOT(17) nor
/// CANCEL_BALLOT(18) up.
/// <code>
/// DEFINE_FEDMSG(C, VOTE, 19)
///   BallotID  ballotID;
///   bool      bAgree;
/// END_FEDMSG
/// </code>
/// Layout (natural alignment, no enclosing pragma pack — <c>Messages.h:185</c>):
/// header(4) + <c>BallotID ballotID</c> (short, 2, offset 4) +
/// <c>bool bAgree</c> (1, offset 6) + 1 pad byte (offset 7, struct align 2)
/// = <b>8 bytes fixed</b>.
/// </summary>
public sealed class Vote : IFedMessage
{
    public const ushort Fmid = 19;
    public const int FixedSize = 8;

    /// <summary>The ballot being voted on (<c>BallotID</c> = short).</summary>
    public short BallotId;

    /// <summary><c>true</c> = in favor, <c>false</c> = opposed.</summary>
    public bool Agree;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(BallotId);       // offset 4
        w.WriteByte(Agree ? (byte)1 : (byte)0);   // offset 6
        w.WriteZeros(1);            // offset 7: tail pad to align 2
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"VOTE internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)w.Position);
        return buf;
    }

    public static Vote Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"VOTE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected VOTE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short ballotId = r.ReadI16();    // offset 4
        bool agree = r.ReadByte() != 0;  // offset 6
        return new Vote { BallotId = ballotId, Agree = agree };
    }
}
