namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_CHANGE_TEAM_CIV</c> (id 51, <c>Messages.h:407-411</c>) — the side's
/// team leader changes the civilization backing their side. Client-sendable
/// only by the target side's leader while the mission is still
/// <c>STAGE_NOTSTARTED</c>; the server forwards it whole-mission and swaps the
/// side's civilization (<c>FedSrv.CPP:7344-7367</c>,
/// <c>fsmission.cpp:5268-5284 SetSideCiv</c>). Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, CHANGE_TEAM_CIV, 51) // sent by team leader; forwarded to everybody
///     SideID  iSide;
///     CivID   civID;
///     bool    random;   // random-faction option
///   END_FEDMSG
/// </code>
/// <c>SideID</c> and <c>CivID</c> are both <c>short</c>s (igc.h typedefs);
/// the trailing <c>bool random</c> is native's random-faction option
/// (Xynth #170): the native client picks a random legal civ itself and sends
/// that civID with <c>random=true</c> (<c>WinTrek/teamscreen.cpp:1062-1075</c>),
/// and the server applies the civ plus the side's random-faction flag. This
/// port honours the flag on the SERVER instead: <c>random=true</c> makes the
/// server resolve the pick from the legal lobby pool and relay the resolved
/// civID (<c>Server.TeamCiv.cs</c>), so every peer and the authoritative side
/// agree without the native owner re-roll loop (<c>teamscreen.cpp:2662-2695</c>). Natural alignment:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 51
///   offset 4: short  iSide   (SideID)
///   offset 6: short  civID   (CivID)
///   offset 8: bool   random  (1 byte)
///   offset 9: 1 pad byte     (struct aligned to 2)
///   = 10 B fixed
/// </code>
/// </summary>
public sealed class ChangeTeamCiv : IFedMessage
{
    public const ushort Fmid = 51;
    public const int FixedSize = 10;  // header + iSide@4 + civID@6 + random@8 + 1 pad@9

    /// <summary>The side whose civilization is changing (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>The target civilization's id (<c>CivID</c> = short).</summary>
    public short CivId;

    /// <summary>Random-faction option (<c>bool</c>). When true the server
    /// resolves the civ from the legal lobby pool and the relayed message carries
    /// that concrete civ id with the flag still set.</summary>
    public bool Random;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4
        w.WriteI16(CivId);                   // offset 6
        w.WriteByte((byte)(Random ? 1 : 0)); // offset 8
        w.WriteByte(0);                      // offset 9: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CHANGE_TEAM_CIV internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ChangeTeamCiv Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CHANGE_TEAM_CIV needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CHANGE_TEAM_CIV fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        short civId = r.ReadI16();       // offset 6
        bool random = r.ReadByte() != 0; // offset 8
        r.Advance(1);                    // offset 9: pad
        return new ChangeTeamCiv { SideId = sideId, CivId = civId, Random = random };
    }
}
