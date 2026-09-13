namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_CANCEL_BALLOT</c> (id 18, <c>Messages.h:179-182</c>): tells the
/// clients a pending vote is cancelled. Server-broadcast only.
/// <code>
/// DEFINE_FEDMSG(S, CANCEL_BALLOT, 18)
///   BallotID  ballotID;
/// END_FEDMSG
/// </code>
/// Layout (natural alignment, no enclosing pragma pack — <c>Messages.h:179</c>):
/// header(4) + <c>BallotID ballotID</c> (short, 2, offset 4) = <b>6 bytes fixed</b>
/// (struct align 2; 6 is already even).
/// <para><b>Emission note (P187).</b> The native FedSrv never actually emits
/// this message on ballot resolution — <c>Ballot::Update</c>
/// (<c>fsmission.cpp:5817-5834</c>) just calls <c>OnPassed</c>/<c>OnFailed</c>
/// and returns, and <c>Ballot::Cancel</c> (<c>fsmission.cpp:5836-5845</c>)
/// leaves an unanswered "REVIEW: should we send a cancelation message here?"
/// The only consumer anywhere in the tree is the client
/// (<c>clintlib/appmsg.cpp:377-386</c>) reacting to a message the server never
/// sends. This port therefore builds the codec for wire completeness (the
/// message type exists and round-trips) but does not emit it on resolution —
/// faithful to the live native server. See docs/BALLOT_SYSTEM_P187.md.</para>
/// </summary>
public sealed class CancelBallot : IFedMessage
{
    public const ushort Fmid = 18;
    public const int FixedSize = 6;

    /// <summary>The ballot being cancelled (<c>BallotID</c> = <c>ObjectID</c> =
    /// short).</summary>
    public short BallotId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(BallotId);       // offset 4
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"CANCEL_BALLOT internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)w.Position);
        return buf;
    }

    public static CancelBallot Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CANCEL_BALLOT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CANCEL_BALLOT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short ballotId = r.ReadI16();    // offset 4
        return new CancelBallot { BallotId = ballotId };
    }
}
