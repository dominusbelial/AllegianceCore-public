namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_TEAM_READY</c> (id 46, <c>Messages.h:386-390</c>), a server-
/// generated broadcast reporting that a whole side's aggregate ready state
/// has changed:
/// <code>
/// DEFINE_FEDMSG(S, TEAM_READY, 46) // team is ready or not
///   // Server-generated based on whether everyone on team is ready, or leader has forced ready
///   SideID    iSide;
///   bool      fReady;
/// END_FEDMSG
/// </code>
/// The server emits this only when a side's ready flag actually <em>changes</em>
/// (<c>fsmission.cpp:5449-5471</c> <c>SetReady</c>: <c>if (fReady !=
/// GetReady(iSide))</c>), to the whole mission group. P180 ports the
/// aggregate-recompute half; <c>FORCE_TEAM_READY(47)</c> is intentionally out
/// of scope (it needs a team-leader concept this port doesn't have).
/// <para>Layout (same natural-alignment reasoning as the sibling
/// <c>PlayerReady</c> codec — no pragma pack encloses this message in
/// <c>Messages.h</c>): header(4) + <c>SideID iSide</c>(short, 2, offset 4) +
/// <c>bool fReady</c>(1, offset 6) + 1 byte tail alignment padding (offset 7,
/// struct aligned to 2) = <b>8 bytes fixed</b>.</para>
/// </summary>
public sealed class TeamReady : IFedMessage
{
    public const ushort Fmid = 46;
    public const int FixedSize = 8;

    /// <summary>The side whose ready state this reports (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>Whether that side is now aggregate-ready.</summary>
    public bool FReady;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4
        w.WriteByte((byte)(FReady ? 1 : 0)); // offset 6
        w.WriteByte(0);                      // offset 7: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"TEAM_READY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static TeamReady Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TEAM_READY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TEAM_READY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        bool fReady = r.ReadByte() != 0; // offset 6
        r.Advance(1);                    // offset 7: tail alignment padding
        return new TeamReady { SideId = sideId, FReady = fReady };
    }
}
