namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_QUIT_SIDE</c> (id 126, <c>Messages.h:825-829</c>): a player has
/// left a side <em>while staying in the mission</em> — distinct from
/// <c>QUIT_MISSION</c>(124), which is the gone-entirely signal. The server
/// emits it on the quit-half of a same-mission side switch (reason
/// <see cref="QuitSideReason.SwitchingSides"/>); in the <c>CS</c> direction a
/// player can quit a side or a leader can kick someone, neither of which P183
/// wires to a live entry point.
/// <code>
/// DEFINE_FEDMSG(CS, QUIT_SIDE, 126)
///   FM_VAR_ITEM(szMessageParam);
///   ShipID            shipID;
///   QuitSideReason    reason;
/// END_FEDMSG
/// </code>
/// Layout is byte-for-byte identical to <see cref="QuitMission"/> (header(4) +
/// VarItem ib/cb @4-7 + <c>ShipID shipID</c> short @8 + 2 pad @10-11 +
/// <c>QuitSideReason reason</c> int @12 = <b>16 bytes fixed</b>), plus the
/// trailing CB_ZTS <see cref="Message"/> blob — the same variable-length
/// trailing-string shape the chat family uses.</summary>
public sealed class QuitSide : IFedMessage
{
    public const ushort Fmid = 126;
    public const int FixedSize = 16;

    /// <summary>The optional trailing message parameter, encoded as a CB_ZTS
    /// string (native <c>FM_VAR_PARM(szMessageParam, CB_ZTS)</c>,
    /// <c>fsmission.cpp:1131-1133</c>). Empty by default.</summary>
    public string Message = "";

    /// <summary>The ship that left the side (<c>ShipID</c> = short).</summary>
    public short ShipId;

    /// <summary>Why (<c>QuitSideReason</c> = native <c>int</c> enum).</summary>
    public QuitSideReason Reason;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = ZtsBlob.BlobSize(Message);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibMessage @4
        int slotCb = w.Reserve(2);  // cbMessage @6
        w.WriteI16(ShipId);         // offset 8
        w.WriteZeros(2);            // offset 10-11: align reason int to 4
        w.WriteI32((int)Reason);    // offset 12
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"QUIT_SIDE internal layout drift: {w.Position} != {FixedSize}");

        var (ib, cb) = ZtsBlob.Write(ref w, Message);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static QuitSide Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"QUIT_SIDE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected QUIT_SIDE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        WireReader r = new(buf);
        r.Advance(4);
        r.Advance(4);                    // VarItem (re-read via ZtsBlob)
        short shipId = r.ReadI16();      // offset 8
        r.Advance(2);                    // pad 10-11
        int reason = r.ReadI32();        // offset 12

        return new QuitSide
        {
            Message = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            ShipId = shipId,
            Reason = (QuitSideReason)reason,
        };
    }
}
