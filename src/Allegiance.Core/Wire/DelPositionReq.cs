namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_DELPOSITIONREQ</c> (id 42, <c>Messages.h:366-370</c>) — cancel (self)
/// or reject (leader) a pending side-join request. Client-sendable both ways:
/// the requester cancels their own request (<c>reason == Canceled</c>), or the
/// target side's leader rejects someone's request (<c>reason == Rejected</c>).
/// The server also generates it (whole-mission) when it drops a pending request,
/// e.g. on reject or on the requester's disconnect. Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, DELPOSITIONREQ, 42) // cancel/reject a pending request
///     ShipID                shipID;
///     SideID                iSide;
///     DelPositionReqReason  reason;
///   END_FEDMSG
/// </code>
/// Two <c>short</c>s plus a C++ <c>int</c> enum: the requesting ship, the side,
/// and the reason (<c>Messages.h:338-364</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 42
///   offset 4: short  shipID        (ShipID)
///   offset 6: short  iSide         (SideID)
///   offset 8: int    reason        (DelPositionReqReason — 4-align OK at 8)
///   = 12 B fixed
/// </code>
/// </summary>
public sealed class DelPositionReq : IFedMessage
{
    public const ushort Fmid = 42;
    public const int FixedSize = 12;   // header + shipID@4 + iSide@6 + reason@8

    /// <summary>The id of the ship whose pending request this affects (<c>ShipID</c> = short).</summary>
    public short ShipId;

    /// <summary>The side of the request (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>Why the request is withdrawn/rejected (<c>DelPositionReqReason</c> = native int).</summary>
    public DelPositionReqReason Reason;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);                  // offset 4
        w.WriteI16(SideId);                  // offset 6
        w.WriteI32((int)Reason);             // offset 8
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"DELPOSITIONREQ internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static DelPositionReq Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DELPOSITIONREQ needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DELPOSITIONREQ fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short shipId = r.ReadI16();      // offset 4
        short sideId = r.ReadI16();      // offset 6
        int reason = r.ReadI32();        // offset 8
        return new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = (DelPositionReqReason)reason };
    }
}
