namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SET_MISSION_OWNER</c> (id 123, <c>Messages.h:781-785</c>): the
/// server notifies the whole mission that a ship is now the mission owner
/// (always also its side's team leader — <c>assert(IMPLIES(fMissionOwner,
/// fTeamLeader))</c>, <c>fsmission.cpp:616</c>). A mission owner can also
/// transfer ownership to a teammate (the <c>CS</c> direction); P183 only
/// builds the server-generated direction (assignment on join / migration on
/// disconnect), not the client-initiated transfer.
/// <code>
/// DEFINE_FEDMSG(CS, SET_MISSION_OWNER, 123)
///   SideID  sideID;
///   ShipID  shipID;
/// END_FEDMSG
/// </code>
/// Layout (natural alignment, like <c>SetTeamLeader</c>/<c>TeamReady</c>):
/// header(4) + <c>SideID sideID</c>(short, 2, offset 4) + <c>ShipID
/// shipID</c>(short, 2, offset 6) = <b>8 bytes fixed</b>.</summary>
public sealed class SetMissionOwner : IFedMessage
{
    public const ushort Fmid = 123;
    public const int FixedSize = 8;

    /// <summary>The side that owns the mission (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>The ship (that side's leader) now owning the mission (<c>ShipID</c> = short).</summary>
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
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_MISSION_OWNER internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetMissionOwner Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_MISSION_OWNER needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_MISSION_OWNER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        short shipId = r.ReadI16();      // offset 6
        return new SetMissionOwner { SideId = sideId, ShipId = shipId };
    }
}
