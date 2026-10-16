using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MISSIONDEF</c> (id 38, <c>Messages.h:294-318</c>) — the server's
/// mission-definition message, sent when a mission is created and when it
/// changes. On the C++ client its handler (<c>appmsg.cpp:2408-2428</c>) is the
/// moment the client's mission-info object exists: when <c>m_pMissionInfo</c>
/// is NULL it allocates it (<c>appmsg.cpp:2416</c>:
/// <c>new MissionInfo(pfmMissionDef->dwCookie)</c>) and
/// <c>Update</c>s it, storing the whole <c>MissionParams</c> in
/// <c>m_pfmMissionDef</c> (<c>clintlib.cpp:670-673</c> memcpy). This is the
/// structural predecessor that <c>FM_S_JOINED_MISSION(95)</c> requires
/// (<c>assert(m_pMissionInfo != NULL)</c>, <c>appmsg.cpp:2154</c>), so the
/// client <b>must</b> receive this message before the join completes.
///
/// This is the message whose embedded <c>MissionParams.szIGCStaticFile</c> and
/// <c>verIGCcore</c> fields are the whole point of P29: the
/// <c>JOINED_MISSION</c> handler reads them
/// (GetIGCStaticFile/GetIGCStaticVer, <c>clintlib.h:608-611</c>) to decide
/// whether to <c>ResetStaticData</c> and reload the local static core
/// (<c>clintlib.cpp:4193-4224</c>) — the step that populates the mission's
/// hull/part/station-type registries the T14-T16 chain proved were the missing
/// piece for real WOPR movement. <b>Empirically corrected in P29</b> (vs. the
/// task brief): <c>szIGCStaticFile</c> must be the <b>extensionless base
/// name</b> ("PCore001"), NOT "PCore001.igc", because the client's
/// <c>LoadIGCStaticCore</c> calls <c>UTL::getFile(name, ".igc", ...)</c> which
/// appends ".igc" (<c>missionigc.cpp:2410</c>, <c>_Utility/Utility.cpp:105-123</c>)
/// — sending the extension makes it look for "<c>PCore001.igc.igc</c>"
/// (observed on the wire probe). And <c>verIGCcore</c> must equal the version
/// word stored at offset 0 of that file, or <c>ResetStaticData</c> bails
/// (<c>clintlib.cpp:4212-4218</c>) and the client disconnects.
///
/// Empirically confirmed layout (compiler-verified on the Windows VM against
/// the checked-in <c>igc.h</c>/<c>Messages.h</c>; P29 diagnostic probe):
/// <c>sizeof(MissionParams) = 252</c>, <c>sizeof(FMD_S_MISSIONDEF) = 864</c>,
/// <c>offsetof(FMD_S_MISSIONDEF, misparms) = 4</c>,
/// <c>offsetof(MissionParams, szIGCStaticFile) = 65</c>,
/// <c>offsetof(MissionParams, verIGCcore) = 120</c>,
/// <c>offsetof(FMD_S_MISSIONDEF, dwCookie) = 460</c>,
/// <c>offsetof(FMD_S_MISSIONDEF, iSideMissionOwner) = 464</c>,
/// <c>offsetof(FMD_S_MISSIONDEF, stage) = 468</c>. (Full probe output in
/// <c>P29_MISSIONDEF_JOINED_MISSION.md</c>.) MSVC default packing — the message
/// has no <c>FM_VAR_ITEM</c>, so it is header(4) + the fields exactly as the
/// compiler lays them out.
///
/// P713 also exposes the native leader array so the join burst can declare
/// empty leadership before sending the player roster and role snapshots.
/// Unspecified fields keep the historical zero-filled codec representation.
/// </summary>
public sealed class MissionDef : IFedMessage
{
    public const ushort Fmid = 38;

    /// <summary><c>sizeof(MissionParams)</c> — the embedded params struct width (compiler-confirmed).</summary>
    public const int MissionParamsSize = 252;

    /// <summary><c>sizeof(FMD_S_MISSIONDEF)</c> — whole-message width (header 4 + MissionParams 252 + trailing MISSIONDEF fields).</summary>
    public const int FixedSize = 864;

    // Named absolute wire offsets, from the P29 MSVC probe.
    private const int MisparmsOffset = 4;                       // offsetof(FMD_S_MISSIONDEF, misparms)
    private const int SzIGCStaticFileOffset = MisparmsOffset + 65;  // + offsetof(MissionParams, szIGCStaticFile)
    private const int VerIgcCoreOffset = MisparmsOffset + 120;     // + offsetof(MissionParams, verIGCcore)
    private const int GameLengthOffset = MisparmsOffset + 132;    // igc.h:1159-1162: version, two rates, duration
    private const int TimeStartOffset = MisparmsOffset + 156;     // P29 MSVC probe; igc.h:1169
    private const int RgCivIdOffset = MisparmsOffset + 168;        // + offsetof(MissionParams, rgCivID)   (P29 probe)
    private const int NTemsOffset = MisparmsOffset + 218;          // + offsetof(MissionParams, nTeams)    (P29 probe)
    private const int DwCookieOffset = 460;
    private const int ISideMissionOwnerOffset = 464;
    private const int StageOffset = 468;
    // Messages.h:302-315; serverName[25], serverAddr[64], sideNames[6][40],
    // one alignment byte, stationIds[6] => rgShipIDLeaders at 814.
    private const int LeadersOffset = 814;

    /// <summary>Width of <c>char c_cbFileName</c> (<c>_Utility/Utility.h:62-63</c>: 12+1 = 13) holding the static-core file name.</summary>
    public const int StaticFileBufSize = 13;

    // --- Fields the join path reads / we intentionally populate -------------
    /// <summary><c>MissionParams.szIGCStaticFile</c> — the static-core file the client must load; must match the client's local file name exactly ("PCore001.igc", NUL-terminated in a 13-byte buffer).</summary>
    public string SzIGCStaticFile = "";

    /// <summary><c>MissionParams.verIGCcore</c> — must equal the version word (first int32, little-endian) at offset 0 of the .igc file or <c>ResetStaticData</c> returns false and the client disconnects (clintlib.cpp:4212-4218).</summary>
    public int VerIgcCore;

    /// <summary><c>MISSIONDEF.dwCookie</c> — the cookie the <c>MissionInfo</c> constructor stores (appmsg.cpp:2416); matches the JOINED_MISSION dwCookie.</summary>
    public uint DwCookie;

    /// <summary><c>MISSIONDEF.iSideMissionOwner</c> (short) — the side whose team leader owns the mission; -1 means no owner.</summary>
    public short ISideMissionOwner;

    /// <summary><c>MISSIONDEF.stage</c> (enum STAGE, int) — not read by the join flow, zeroed.</summary>
    public int Stage;

    /// <summary>MissionParams.dtGameLength, seconds; zero disables the timed game
    /// (igc.h:1162,1526-1529,1557-1560).</summary>
    public float GameLengthSeconds;

    /// <summary>MissionParams.timeStart, server-clock milliseconds (igc.h:1169).
    /// Also replaced by SET_START_TIME (clintlib/appmsg.cpp:2702-2711).</summary>
    public uint TimeStart;

    /// <summary><c>MissionParams.nTeams</c> — number of sides; drives the client's
    /// <c>UpdateSides</c> loop (missionigc.cpp:3749) that creates the real sides
    /// and (via <c>AddSide</c>, missionigc.cpp:3612) the <c>SIDE_TEAMLOBBY</c>
    /// side, which <c>AddPlayerToMission</c>/<c>AddPlayerToSide</c> need. Zero
    /// when unset (the first MISSIONDEF only needs the core-load fields).</summary>
    public short NTems;

    /// <summary><c>MissionParams.rgCivID[c_cSidesMax]</c> (short[6]) — civilisations per side;
    /// must exist in the static core or <c>UpdateSides</c>'s <c>GetCivilization</c>
    /// asserts (missionigc.cpp:3751). Entries beyond the supplied count are zeroed.</summary>
    public short[] RgCivId = Array.Empty<short>();

    /// <summary>Native rgShipIDLeaders[6] (Messages.h:311). Use -1 for an
    /// empty side; zero-filled unspecified entries retain the legacy codec default.</summary>
    public short[] RgShipIdLeaders = Array.Empty<short>();

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.Advance(MisparmsOffset - 4);         // header consumed; land on misparms (offset 4)
        // ------------------------- MissionParams (252 bytes) ---------------
        w.Advance(65);                         // strGameName char[65] (zeros)
        int staticStart = w.Position;          // == SzIGCStaticFileOffset
        WriteFixedString(ref w, SzIGCStaticFile, StaticFileBufSize);  // char[13], NUL + zero-pad
        w.Advance(78 - (65 + StaticFileBufSize));  // pad through szCustomMapFile[13]+strGamePassword[17] gap... corrected below
        // ... see placement comments; Advance arithmetic is validated at the end.
        // After szIGCStaticFile (offset 65+13=78), szCustomMapFile[13] and
        // strGamePassword[17] fill 78..107, then the 37 bool:1 bitfields pack
        // 108..112 (6 bytes), KBlevel short 114, fGoalTeamMoney float 116
        // (4-aligned), verIGCcore int at 124. All untouched by this codec
        // (zeros) except verIGCcore; so we Advance from 78 to 124 directly and
        // set the version.
        w.Advance(VerIgcCoreOffset - w.Position);   // 78 -> 124 (gap: custom map + password + bitfields + money etc.)
        w.WriteI32(VerIgcCore);                     // verIGCcore (int) at 124
        w.Advance(GameLengthOffset - w.Position);
        w.WriteI32(BitConverter.SingleToInt32Bits(GameLengthSeconds));
        w.Advance(TimeStartOffset - w.Position);
        w.WriteU32(TimeStart);
        // rgCivID (short[6]) at 172; nTeams (short) at 222. Skip the gap, write
        // the civ ids, then the team count, then finish the MissionParams block.
        w.Advance(RgCivIdOffset - w.Position);      // 124 -> 172
        for (int i = 0; i < 6; i++)                 // rgCivID[c_cSidesMax] = short[6]
            w.WriteI16(i < RgCivId.Length ? RgCivId[i] : (short)0);
        w.Advance(NTemsOffset - w.Position);        // 184 -> 222
        w.WriteI16(NTems);                          // nTeams (short) at 222
        w.Advance(MissionParamsSize - (NTemsOffset - MisparmsOffset) - 2); // finish MissionParams: 224..252 gap minus the 2 we wrote
        // ------------------------- MISSIONDEF fixed fields ------------------
        w.Advance(256 - w.Position);   // szDescription char[201] zeros -> land at 256
        // dwCookie at 460, iSideMissionOwner 464, stage 468: reach 460 via zeros
        w.Advance(DwCookieOffset - w.Position);
        w.WriteU32(DwCookie);
        w.Advance(ISideMissionOwnerOffset - w.Position);
        w.WriteI16(ISideMissionOwner);
        w.Advance(StageOffset - w.Position);
        w.WriteI32(Stage);
        w.Advance(LeadersOffset - w.Position);
        for (int i = 0; i < 6; i++)
            w.WriteI16(i < RgShipIdLeaders.Length ? RgShipIdLeaders[i] : (short)0);

        // pad to exact total first, then stamp cbmsg = the full 864 (a message
        // length is the whole thing, not just the fields we happened to write).
        if (w.Position < FixedSize) w.Advance(FixedSize - w.Position);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"MISSIONDEF internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)FixedSize);
        // Re-validate the two essential absolute placements independently of
        // the cursor arithmetic above (belt-and-braces against a silent pad slip).
        // szIGCStaticFile is compared against its truncated written form (a name
        // longer than the buffer is clipped to width-1, keeping the NUL).
        if (ReadFixedString(buf, SzIGCStaticFileOffset, StaticFileBufSize) != TruncatedFixedString(SzIGCStaticFile))
            throw new InvalidOperationException("MISSIONDEF szIGCStaticFile mispositioned");
        if (BitConverter.ToInt32(buf, VerIgcCoreOffset) != VerIgcCore)
            throw new InvalidOperationException("MISSIONDEF verIGCcore mispositioned");
        return buf;
    }

    private static string TruncatedFixedString(string s) =>
        s.Length <= StaticFileBufSize - 1 ? s : s[..(StaticFileBufSize - 1)];

    public static MissionDef Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length != FixedSize)
            throw new ArgumentException($"MISSIONDEF needs exactly {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSIONDEF fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != FixedSize) throw new ArgumentException($"cbmsg={hdr.CbMsg} != fixed {FixedSize}");

        string staticFile = ReadFixedString(buf, SzIGCStaticFileOffset, StaticFileBufSize);
        int ver = BitConverter.ToInt32(buf.Slice(VerIgcCoreOffset));
        uint cookie = BitConverter.ToUInt32(buf.Slice(DwCookieOffset));
        short owner = BitConverter.ToInt16(buf.Slice(ISideMissionOwnerOffset));
        int stage = BitConverter.ToInt32(buf.Slice(StageOffset));
        short nTems = BitConverter.ToInt16(buf.Slice(NTemsOffset));
        var rgCivId = new short[6];
        for (int i = 0; i < rgCivId.Length; i++)
            rgCivId[i] = BitConverter.ToInt16(buf.Slice(RgCivIdOffset + i * 2));
        var leaders = new short[6];
        for (int i = 0; i < leaders.Length; i++)
            leaders[i] = BitConverter.ToInt16(buf.Slice(LeadersOffset + i * 2));

        return new MissionDef
        {
            SzIGCStaticFile = staticFile,
            VerIgcCore = ver,
            DwCookie = cookie,
            ISideMissionOwner = owner,
            Stage = stage,
            GameLengthSeconds = BitConverter.ToSingle(buf.Slice(GameLengthOffset)),
            TimeStart = BitConverter.ToUInt32(buf.Slice(TimeStartOffset)),
            NTems = nTems,
            RgCivId = rgCivId,
            RgShipIdLeaders = leaders,
        };
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static void WriteFixedString(ref WireWriter w, string s, int width)
    {
        int n = Math.Min(s.Length, width - 1);
        if (n > 0) w.WriteBytes(Latin1.GetBytes(s[..n]));
        w.WriteByte(0);
        w.WriteZeros(width - n - 1);
    }

    private static string ReadFixedString(ReadOnlySpan<byte> buf, int off, int width)
    {
        var raw = buf.Slice(off, width);
        int n = width;
        while (n > 0 && raw[n - 1] == 0) n--;
        return Latin1.GetString(raw[..n]);
    }
}
