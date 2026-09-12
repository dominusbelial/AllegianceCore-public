namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_QUIT_MISSION</c> (id 124, <c>Messages.h:812-818</c>): a player has
/// left the mission entirely — broadcast by the server on disconnect
/// (P183's real, previously-missing gap) and, in the <c>CS</c> direction,
/// usable by a player to quit or a leader to kick. P183 only builds the
/// server-generated disconnect direction; the client-initiated quit/kick is a
/// separate, not-yet-scoped task.
/// <code>
/// DEFINE_FEDMSG(CS, QUIT_MISSION, 124)
///   FM_VAR_ITEM(szMessageParam);
///   ShipID            shipID;
///   QuitSideReason    reason;
/// END_FEDMSG
/// </code>
/// Layout (MSVC default packing; same rule as the sibling <c>ChatMessage</c>
/// codec — each member aligned to its own size): header(4) + the
/// <c>szMessageParam</c> VarItem ib/cb pair (short, short, offset 4-7) +
/// <c>ShipID shipID</c>(short, 2, offset 8) + 2 pad bytes (offset 10-11,
/// align the C++ <c>int</c> enum to 4) + <c>QuitSideReason reason</c>(int, 4,
/// offset 12) = <b>16 bytes fixed</b>; the <c>szMessageParam</c> CB_ZTS blob
/// (the same variable-length trailing-string shape <see cref="ChatMessage"/>
/// uses) follows at offset 16. <see cref="Message"/> is written/read through
/// the shared <see cref="ZtsBlob"/> helper exactly like the chat family.</summary>
public sealed class QuitMission : IFedMessage
{
    public const ushort Fmid = 124;
    public const int FixedSize = 16;

    /// <summary>The optional trailing message parameter, encoded as a CB_ZTS
    /// string exactly as the native <c>FM_VAR_PARM(szMessageParam, CB_ZTS)</c>
    /// (<c>fsmission.cpp:482-484</c>). Empty by default — the disconnect path
    /// carries no reason text.</summary>
    public string Message = "";

    /// <summary>The ship that quit (<c>ShipID</c> = short).</summary>
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
            throw new InvalidOperationException($"QUIT_MISSION internal layout drift: {w.Position} != {FixedSize}");

        // blob: CB_ZTS at the fixed-region end (ib = offset from byte 0)
        var (ib, cb) = ZtsBlob.Write(ref w, Message);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static QuitMission Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"QUIT_MISSION needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected QUIT_MISSION fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        WireReader r = new(buf);
        r.Advance(4);
        r.Advance(4);                    // VarItem (re-read via ZtsBlob)
        short shipId = r.ReadI16();      // offset 8
        r.Advance(2);                    // pad 10-11
        int reason = r.ReadI32();        // offset 12

        return new QuitMission
        {
            Message = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            ShipId = shipId,
            Reason = (QuitSideReason)reason,
        };
    }
}
