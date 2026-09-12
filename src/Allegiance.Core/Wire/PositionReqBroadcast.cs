namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_POSITIONREQ</c> (id 40, <c>Messages.h:325-329</c>) — the server's
/// whole-mission broadcast announcing a pending side-join request that needs
/// leader approval (the server half of the <c>POSITIONREQ</c> pair; the
/// client request is the distinct <see cref="PositionReq"/> shape at id 39).
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(S, POSITIONREQ, 40) // server forwards request to appropriate person if permission required
///   // This goes to everyone so that they know the player in question is in the lobby
///     ShipID    shipID;
///     SideID    iSide;
///   END_FEDMSG
/// </code>
/// Two <c>short</c>s: the requesting ship and the side it wants. Sent by the
/// server to the whole mission when the target side already has a leader, so
/// the leader (and everyone) sees the pending request (<c>FedSrv.CPP:6658-6707</c>
/// → <c>RequestPosition</c>, <c>fsmission.cpp:4765-4771</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 40
///   offset 4: short  shipID       (ShipID)
///   offset 6: short  iSide        (SideID)
///   = 8 B fixed (the same two-back-to-back-shorts shape as JoinSide)
/// </code>
/// </summary>
public sealed class PositionReqBroadcast : IFedMessage
{
    public const ushort Fmid = 40;
    public const int FixedSize = 8;   // header + shipID @4 + iSide @6

    /// <summary>The id of the ship making the pending request (<c>ShipID</c> = short).</summary>
    public short ShipId;

    /// <summary>The side the request is for (<c>SideID</c> = short).</summary>
    public short SideId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);                  // offset 4
        w.WriteI16(SideId);                  // offset 6
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"POSITIONREQ(40) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PositionReqBroadcast Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"POSITIONREQ(40) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected POSITIONREQ fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short shipId = r.ReadI16();      // offset 4
        short sideId = r.ReadI16();      // offset 6
        return new PositionReqBroadcast { ShipId = shipId, SideId = sideId };
    }
}
