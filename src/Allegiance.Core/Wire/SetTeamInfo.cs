using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SET_TEAM_INFO</c> (id 15, <c>Messages.h:157-161</c>): the server
/// relays a side's squad id + name to the whole mission on every squad/name
/// change (native <c>SetSideName</c> / <c>SetSideSquad</c>,
/// fsmission.cpp:1580-1590), and a client leader can propose a change back
/// (native only honours that C→S direction in a squad game,
/// <c>FedSrv/FedSrv.CPP:7430-7463</c>).
/// <code>
/// DEFINE_FEDMSG(CS, SET_TEAM_INFO, 15)
///   SquadID   squadID;   // int  (Igc/igc.h:502)
///   SideID    sideID;    // short
///   char      SideName[c_cbName];   // 25 bytes (c_cbName = c_cbNameDB+1, Utility.h:60,64)
/// END_FEDMSG
/// </code>
/// Native wire size = <c>sizeof(FMD_CS_SET_TEAM_INFO)</c>: header(4) + int(4) +
/// short(2) + char[25] = 35, rounded to the struct's 4-byte alignment = <b>36
/// bytes</b> (1 trailing pad byte, the same natural-alignment convention as the
/// sibling <c>AutoAccept</c> codec, Messages.h has no <c>#pragma pack</c>
/// enclosing this message — the two pack regions are at lines 636-642 and
/// 917-925, far from line 157):
/// <code>
///   offset 0:  ushort cbmsg
///   offset 2:  ushort fmid = 15
///   offset 4:  int    squadID   (SquadID; NA(-1) = side has no squad)
///   offset 8:  short  sideID
///   offset 10: char[25] SideName
///   offset 35: 1 pad byte       (struct aligned to 4)
/// </code>
/// <para><b>Port semantics (P820c):</b> this port's squad id is a string guid,
/// not native's integer, so the server maps it to an opaque stable int for the
/// <c>squadID</c> field and — following native's non-lock squad-game rule
/// (<c>SetSideSquad</c> → <c>SetSideName(sid, szSquadName)</c>,
/// fsmission.cpp:1666-1667) — carries the <b>squad name</b> in <c>SideName</c>
/// when the side has a squad, or the real side name when it has none (the
/// client clears the side's squad when <c>squadID</c> is NA). Unlike native's
/// client (appmsg.cpp:2541-2550, which renames the IGC side from
/// <c>SideName</c>), this port keeps the real side name from SIDE_ROSTER and
/// stores <c>SideName</c> as the side's <b>squad name</b> for the team pane —
/// a deliberate divergence so the port can show "SIDE n [Squad]" separately.</para>
/// </summary>
public sealed class SetTeamInfo : IFedMessage
{
    public const ushort Fmid = 15;
    public const int FixedSize = 36;   // header + int + short + char[25] + 1 pad
    public const int SideNameCapacity = 25;

    /// <summary>The side's squad id (native <c>SquadID</c> int; NA(-1) when the
    /// side has no squad). In this port it is an opaque stable int derived from
    /// the string squad guid — the client only tests it against NA.</summary>
    public int SquadId = -1;

    /// <summary>The side this team-info describes (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>Up to 25 Latin-1 characters: the squad name when
    /// <see cref="SquadId"/> != NA, else the real side name (native non-lock
    /// squad-game semantics). Truncated on encode to fit <c>char[25]</c>.</summary>
    public string SideName = "";

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);               // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(SquadId);         // offset 4
        w.WriteI16(SideId);          // offset 8
        var name = new byte[SideNameCapacity];
        int n = Math.Min(SideName.Length, SideNameCapacity);
        if (n > 0) Encoding.Latin1.GetBytes(SideName.Substring(0, n), name.AsSpan(0, n));
        w.WriteBytes(name);          // offset 10
        w.WriteByte(0);              // offset 35: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_TEAM_INFO internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetTeamInfo Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_TEAM_INFO needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_TEAM_INFO fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        int squadId = r.ReadI32();   // offset 4
        short sideId = r.ReadI16();  // offset 8
        var nameBytes = buf.Slice(r.Position, SideNameCapacity);
        r.Advance(SideNameCapacity); // offset 10..34
        r.Advance(1);                // offset 35: pad
        int len = SideNameCapacity;
        while (len > 0 && nameBytes[len - 1] == 0) len--;
        return new SetTeamInfo { SquadId = squadId, SideId = sideId, SideName = Encoding.Latin1.GetString(nameBytes[..len]) };
    }
}
