namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_JOIN_SIDE</c> (id 125, <c>Messages.h:820-823</c>), a
/// <b>server-only</b> (<c>S</c>) broadcast announcing that a ship joined /
/// switched to a side (native <c>AddPlayerToSide</c>, <c>fsmission.cpp:621-624</c>):
/// <code>
/// DEFINE_FEDMSG(S, JOIN_SIDE, 125)
///   SideID  sideID;
///   ShipID  shipID;
/// END_FEDMSG
/// </code>
/// Sent to the whole mission whenever a player is seated on a side — always,
/// regardless of whether they became a leader/owner (those get the separate
/// <c>SET_TEAM_LEADER</c>(121)/<c>SET_MISSION_OWNER</c>(123) broadcasts).
/// Layout (natural alignment, like the sibling <c>TeamReady</c> codec):
/// header(4) + <c>SideID sideID</c>(short, 2, offset 4) + <c>ShipID
/// shipID</c>(short, 2, offset 6) = <b>8 bytes fixed</b>.</summary>
public sealed class JoinSide : IFedMessage
{
    public const ushort Fmid = 125;
    public const int FixedSize = 8;

    /// <summary>The side joined (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>The ship that joined it (<c>ShipID</c> = short).</summary>
    public short ShipId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4
        w.WriteI16(ShipId);                  // offset 6
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"JOIN_SIDE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static JoinSide Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"JOIN_SIDE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected JOIN_SIDE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        short shipId = r.ReadI16();      // offset 6
        return new JoinSide { SideId = sideId, ShipId = shipId };
    }
}
