namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_POSITIONACK</c> (id 41, <c>Messages.h:331-336</c>) — the side's
/// team leader accepts or rejects a pending side-join request (<c>POSITIONREQ(40)</c>).
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(C, POSITIONACK, 41) // mission/team leader accepts or rejects person
///     ShipID        shipID;
///     SideID        iSide;
///     bool          fAccepted;
///   END_FEDMSG
/// </code>
/// Two <c>short</c>s plus a <c>bool</c>: the requesting ship, the side, and
/// whether the request is accepted. On accept the server seats the requester;
/// on reject it drops the pending request and broadcasts <c>DELPOSITIONREQ(
/// DPR_Rejected)</c> (<c>FedSrv.CPP:6709-6747</c>). The struct's own stale
/// header comment ("everyone gets either a <c>PLAYER_SIDE_CHANGE</c> or a
/// <c>DELPOSITIONREQ</c>") names an fmid that does not exist — the real
/// accept-outcome broadcast is <c>JOIN_SIDE(125)</c> (P183).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 41
///   offset 4: short  shipID        (ShipID)
///   offset 6: short  iSide         (SideID)
///   offset 8: bool   fAccepted     (1 byte)
///   offset 9: 1 pad byte           (struct aligned to 2)
///   = 10 B fixed
/// </code>
/// </summary>
public sealed class PositionAck : IFedMessage
{
    public const ushort Fmid = 41;
    public const int FixedSize = 10;   // header + shipID@4 + iSide@6 + bool@8 + 1 pad@9

    /// <summary>The id of the ship whose request is being answered (<c>ShipID</c> = short).</summary>
    public short ShipId;

    /// <summary>The side of the request (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>Whether the leader accepts (<c>true</c>) or rejects (<c>false</c>) the request.</summary>
    public bool FAccepted;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);                  // offset 4
        w.WriteI16(SideId);                  // offset 6
        w.WriteByte((byte)(FAccepted ? 1 : 0)); // offset 8
        w.WriteByte(0);                      // offset 9: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"POSITIONACK internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PositionAck Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"POSITIONACK needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected POSITIONACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short shipId = r.ReadI16();      // offset 4
        short sideId = r.ReadI16();      // offset 6
        bool fAccepted = r.ReadByte() != 0; // offset 8
        r.Advance(1);                    // offset 9: pad
        return new PositionAck { ShipId = shipId, SideId = sideId, FAccepted = fAccepted };
    }
}
