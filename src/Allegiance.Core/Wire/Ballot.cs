namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_BALLOT</c> (id 17, <c>Messages.h:170-176</c>): the server
/// announces a pending vote to the eligible voters, carrying the proposal
/// text, the initiator, the ballot id and its expiration. Server-broadcast
/// only (P187) — there is no client→server BALLOT; the <c>VOTE</c>(19)
/// message is how a client responds.
/// <code>
/// DEFINE_FEDMSG(S, BALLOT, 17)
///   FM_VAR_ITEM(BallotText);
///   ObjectType  otInitiator;
///   ObjectID    oidInitiator;
///   BallotID    ballotID;
///   Time        timeExpiration;
///   bool        bHideToLeader; // KGJV #110
/// END_FEDMSG
/// </code>
/// Layout (natural alignment — no pragma pack encloses this message in
/// <c>Messages.h</c>; the only pack regions are <c>:636-642</c> and
/// <c>:917-925</c>):
/// <code>
///   offset 0:  ushort cbmsg
///   offset 2:  ushort fmid = 17
///   offset 4:  ushort ibBallotText   (var item — the FM_VAR_ITEM pair)
///   offset 6:  ushort cbBallotText
///   offset 8:  short  otInitiator    (ObjectType = short, igc.h:115)
///   offset 10: short  oidInitiator   (ObjectID = short, igc.h:477)
///   offset 12: short  ballotID       (BallotID = ObjectID = short)
///   offset 14: 2 pad bytes           (align the uint32 Time to 4)
///   offset 16: uint32 timeExpiration (Time = ms accumulator, ztime.h:20-21)
///   offset 20: bool   bHideToLeader
///   offset 21-23: pad (struct align 4 → size 24)
///   = 24 bytes fixed
/// blob: BallotText (CB_ZTS) at offset 24 → cbmsg = 24 + blobLen
/// </code>
/// <c>timeExpiration</c> is the raw native millisecond <c>Time</c> value on
/// the wire (kept as <see cref="uint"/> here for fidelity); the server
/// converts its double-seconds clock to milliseconds when broadcasting.
/// <c>HideToLeader</c> is a decode-only UI hint (whether the leader sees who
/// proposed the vote) — no behavior in this port.
/// </summary>
public sealed class Ballot : IFedMessage
{
    public const ushort Fmid = 17;
    public const int FixedSize = 24;

    /// <summary>The proposal text, encoded as a CB_ZTS string exactly as the
    /// native <c>FM_VAR_PARM(BallotText, CB_ZTS)</c> (<c>fsmission.cpp:5968-5971</c>).</summary>
    public string BallotText = "";

    /// <summary>Who proposed: <c>OT_ship</c> for a player-initiated ballot,
    /// <c>OT_side</c> for a mission-wide one (<c>ObjectType</c> = short).</summary>
    public short OtInitiator;

    /// <summary>The initiator's object id — the proposing ship for a
    /// player-initiated ballot, the offering side for a mission-wide one.</summary>
    public short OidInitiator;

    /// <summary>The unique ballot id the server assigned (<c>BallotID</c> =
    /// <c>ObjectID</c> = short).</summary>
    public short BallotId;

    /// <summary>The ballot's expiration as a raw native millisecond
    /// <c>Time</c> value (uint32 on the wire).</summary>
    public uint TimeExpiration;

    /// <summary>KGJV #110 — whether the side leader is shielded from seeing
    /// who proposed the vote. Decode-only in this port (no client).</summary>
    public bool HideToLeader;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = ZtsBlob.BlobSize(BallotText);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibBallotText @4
        int slotCb = w.Reserve(2);  // cbBallotText @6
        w.WriteI16(OtInitiator);    // @8
        w.WriteI16(OidInitiator);   // @10
        w.WriteI16(BallotId);       // @12
        w.WriteZeros(2);            // @14-15: pad to align the uint32 Time
        w.WriteU32(TimeExpiration); // @16
        w.WriteByte(HideToLeader ? (byte)1 : (byte)0);  // @20
        w.WriteZeros(3);            // @21-23: tail pad to struct align 4
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"BALLOT internal layout drift: {w.Position} != {FixedSize}");

        var (ib, cb) = ZtsBlob.Write(ref w, BallotText);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static Ballot Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BALLOT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BALLOT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair

        WireReader r = new(buf);
        r.Advance(4);                    // header
        r.Advance(4);                    // VarItem (re-read via ZtsBlob)
        short ot = r.ReadI16();          // @8
        short oid = r.ReadI16();         // @10
        short ballotId = r.ReadI16();    // @12
        r.Advance(2);                    // pad @14-15
        uint expires = r.ReadU32();      // @16
        bool hide = r.ReadByte() != 0;   // @20

        return new Ballot
        {
            BallotText = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            OtInitiator = ot,
            OidInitiator = oid,
            BallotId = ballotId,
            TimeExpiration = expires,
            HideToLeader = hide,
        };
    }
}
