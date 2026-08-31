namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_JOINED_MISSION</c> (id 95, <c>Messages.h:660-665</c>) — the server's
/// "you are in the game" message, sent to a client when it actually enters a
/// mission. Its C++ handler (<c>appmsg.cpp:2150-2215</c>) is the moment the
/// client loads its static core: it calls
/// <c>ResetStaticData(m_pMissionInfo->GetIGCStaticFile(), &amp;m_pCoreIGC, now,
/// GetIsZoneClub())</c> (<c>appmsg.cpp:2162-2163</c>) when the file differs
/// from the currently-loaded one, which in turn calls
/// <c>LoadIGCStaticCore</c> (<c>clintlib.cpp:4212-4221</c>) and — critically —
/// <b>imports</b> the local <c>.igc</c> file so each object type's
/// <c>Initialize</c> (e.g. <c>ChullTypeIGC::Initialize</c>,
/// <c>hullTypeIGC.cpp:230</c>: <c>AddHullType(this)</c>) populates the
/// mission's <c>m_hullTypes</c>/etc registries. That is exactly the static-data
/// hydration T14-T16 proved was missing (the client's mission existed but its
/// hull-type table was empty, so
/// <c>GetMyMission()-&gt;GetHullType(1)</c> faulted).
///
/// The handler's own assert has an explicit escape
/// (<c>m_dwCookieToJoin == pfmJoinedMission->dwCookie || !m_fmLobby.IsConnected()</c>,
/// <c>appmsg.cpp:2157</c>): a direct-connect bot never connects to a lobby, so
/// the cookie check is trivially satisfied — this message is legitimately
/// reachable outside the lobby flow by design.
///
/// Only four fixed fields (<c>Messages.h:660-665</c>): <c>ShipID shipID</c>;
/// <c>float dtime</c>; <c>Cookie cookie</c> (a single byte); <c>DWORD
/// dwCookie</c>. Empirically confirmed layout (P29 MSVC probe):
/// <c>sizeof(FMD_S_JOINED_MISSION) = 20</c> with
/// <c>offsetof(shipID)=4</c>, <c>offsetof(dtime)=8</c>,
/// <c>offsetof(cookie)=12</c>, <c>offsetof(dwCookie)=16</c> (MSVC aligns the
/// float to 4 → two pad bytes after the 2-byte shipID, and the trailing DWORD
/// to 4 → three pad bytes after the 1-byte cookie). Wire layout:
/// <code>
///   offset 0: ushort cbmsg = 20
///   offset 2: ushort fmid  = 95
///   offset 4: short  shipID   (2 bytes)
///   offset 6: 2 pad bytes      (align the float)
///   offset 8: float dtime      (4 bytes)
///   offset12: byte  cookie     (1 byte)
///   offset13: 3 pad bytes      (align the trailing DWORD)
///   offset16: DWORD dwCookie   (4 bytes)
///   = 20 bytes total
/// </code>
/// Of the four, the join handler actually consumes <c>shipID</c> (used to tag
/// the player's info, <c>appmsg.cpp:2198/2207</c>) and <c>dwCookie</c> (the
/// assert, escaped for a direct-connect bot). <c>dtime</c> is a client clock
/// adjustment and <c>cookie</c> an in-game session token neither is read in
/// this flow, so both are zero here (documented, not assumed — grep in
/// <c>appmsg.cpp:2150-2215</c> confirms no other consumption).
/// </summary>
public sealed class JoinedMission : IFedMessage
{
    public const ushort Fmid = 95;

    /// <summary><c>sizeof(FMD_S_JOINED_MISSION)</c> — compiler-confirmed 20 bytes.</summary>
    public const int FixedSize = 20;

    // Named absolute wire offsets, from the P29 MSVC probe.
    private const int ShipIdOffset = 4;
    private const int DTimeOffset = 8;
    private const int CookieOffset = 12;
    private const int DwCookieOffset = 16;

    /// <summary><c>shipID</c> — the joining player's ship id; must be the assigned id so the client's player info is tagged with it.</summary>
    public short ShipId;

    /// <summary><c>dtime</c> — client clock delta; not read by this flow, zero.</summary>
    public float DTime;

    /// <summary><c>cookie</c> — the in-game cookie; not read by this flow, zero.</summary>
    public byte Cookie;

    /// <summary><c>dwCookie</c> — the lobby cookie; assert-guarded (escaped for a direct-connect bot).</summary>
    public uint DwCookie;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);
        w.Advance(DTimeOffset - w.Position);   // 2 pad bytes to the float
        w.WriteI32(BitConverter.SingleToInt32Bits(DTime));
        w.Advance(CookieOffset - w.Position);  // 3 pad bytes to the cookie
        w.WriteByte(Cookie);
        w.Advance(DwCookieOffset - w.Position); // 3 pad bytes to the DWORD
        w.WriteU32(DwCookie);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"JOINED_MISSION internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    public static JoinedMission Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length != FixedSize)
            throw new ArgumentException($"JOINED_MISSION needs exactly {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected JOINED_MISSION fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != FixedSize) throw new ArgumentException($"cbmsg={hdr.CbMsg} != fixed {FixedSize}");

        short shipId = BitConverter.ToInt16(buf.Slice(ShipIdOffset));
        float dtime = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(DTimeOffset)));
        byte cookie = buf[CookieOffset];
        uint dwCookie = BitConverter.ToUInt32(buf.Slice(DwCookieOffset));

        return new JoinedMission
        {
            ShipId = shipId,
            DTime = dtime,
            Cookie = cookie,
            DwCookie = dwCookie,
        };
    }
}
