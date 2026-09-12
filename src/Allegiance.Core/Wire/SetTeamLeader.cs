namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SET_TEAM_LEADER</c> (id 121, <c>Messages.h:767-772</c>): the
/// server notifies the whole mission that a ship is now its side's team
/// leader, or a team leader transfers leadership to a teammate. Both are <c>CS</c>;
/// P183 only builds the server-generated direction (the automatic
/// assignment/succession on join and disconnect); the client-initiated manual
/// transfer is a separate, not-yet-scoped task and no live client entry point
/// is added.
/// <code>
/// DEFINE_FEDMSG(CS, SET_TEAM_LEADER, 121)
///   SideID  sideID;
///   ShipID  shipID;
/// END_FEDMSG
/// </code>
/// Layout (same natural-alignment reasoning as the sibling <c>TeamReady</c>
/// codec — no pragma pack encloses this message in <c>Messages.h</c>):
/// header(4) + <c>SideID sideID</c>(short, 2, offset 4) + <c>ShipID
/// shipID</c>(short, 2, offset 6) = <b>8 bytes fixed</b>.</summary>
public sealed class SetTeamLeader : IFedMessage
{
    public const ushort Fmid = 121;
    public const int FixedSize = 8;

    /// <summary>The side whose leadership this announces (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>The ship now leading that side (<c>ShipID</c> = short).</summary>
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
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_TEAM_LEADER internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetTeamLeader Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_TEAM_LEADER needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_TEAM_LEADER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        short shipId = r.ReadI16();      // offset 6
        return new SetTeamLeader { SideId = sideId, ShipId = shipId };
    }
}
