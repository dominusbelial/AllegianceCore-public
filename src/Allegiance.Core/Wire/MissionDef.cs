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
    private const int LivesOffset = MisparmsOffset + 204;          // HUD-12 — offsetof(MissionParams, iLives), igc.h:1185
    private const int RgCivIdOffset = MisparmsOffset + 168;        // + offsetof(MissionParams, rgCivID)   (P29 probe)
    private const int NTemsOffset = MisparmsOffset + 218;          // + offsetof(MissionParams, nTeams)    (P29 probe)
    // P853 — the MissionParams tail native's FM_CS_MISSIONPARAMS guards read
    // (FedSrv.CPP:7541-7557): iMinRank/iMaxRank (igc.h:1200-1201),
    // nMinPlayersPerTeam/nMaxPlayersPerTeam (igc.h:1232-1233) and
    // nTotalMaxPlayersPerGame (igc.h:1237). MISSIONDEF carries the whole
    // MissionParams, so the advertisement must be truthful about them for the
    // C→S echo guards to be meaningful.
    private const int IMinRankOffset = MisparmsOffset + 220;
    private const int IMaxRankOffset = MisparmsOffset + 222;
    private const int NMinPlayersPerTeamOffset = MisparmsOffset + 246;
    private const int NMaxPlayersPerTeamOffset = MisparmsOffset + 247;
    private const int NTotalMaxPlayersPerGameOffset = MisparmsOffset + 250;
    // P761 — MissionParams.bLockGameOpen (igc.h:1139, "Prevent players from
    // limiting the size of the game"): the 19th of the bool:1 bitfields that
    // pack at MissionParams offset 108 (after strGamePassword[17] ends there —
    // see the encode block below); MSVC packs bool:1 units LSB-first, so bit 18
    // is byte 108+2, bit 2. The gates that read it: Auto-Accept and Lock Lobby
    // are ENABLED only while it is false (teamscreen.cpp:1563-1569).
    //
    // P856 — the same 36-bit block carries the two options this task promotes:
    // bit 6 = bAllowAlliedViz (igc.h:1127) and bit 28 = bAllowFriendlyFire
    // (igc.h:1149). Counting the native declaration order from bEjectPods as
    // bit 0 (igc.h:1120-1156) gives both indices directly; bit 18 matches the
    // pre-existing bLockGameOpen constant, confirming the mapping.
    private const int BitfieldsOffset = MisparmsOffset + 108;
    private const int AllowAlliedVizBit = 6;
    private const int BLockGameOpenBit = 18;
    private const int AllowFriendlyFireBit = 28;
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

    /// <summary>Numeric native goal parameters (Igc/igc.h:1158-1179).</summary>
    public Allegiance.Core.Model.MissionGoals Goals = new();
    /// <summary>MissionParams.dtGameLength, seconds; zero disables the timed game
    /// (Igc/igc.h:1162,1526-1529,1557-1560).</summary>
    public float GameLengthSeconds { get => Goals.GameLengthSeconds; set => Goals.GameLengthSeconds = value; }

    /// <summary>MissionParams.timeStart, server-clock milliseconds (igc.h:1169).
    /// Also replaced by SET_START_TIME (clintlib/appmsg.cpp:2702-2711).</summary>
    public uint TimeStart;

    /// <summary><c>MissionParams.iLives</c> — the native per-player life budget
    /// (<c>igc.h:1185</c>): "Player must die more than this number of times to
    /// exit"; <c>c_cUnlimitedLives = 0x7fff</c> (<c>igc.h:1007</c>, the default
    /// at <c>igc.h:1291</c>). The native HUD reads it as
    /// <c>iLives − ppi-&gt;MissionDeaths()</c> (<c>WinTrek/console.cpp:812-817</c>).
    /// Offset 208 = misparms(4) + 204, derived from the native struct: after
    /// <c>fStartingMoney</c> at params offset 200 (<c>igc.h:1183</c>), iLives
    /// is the next short. The message already had this slot; HUD-12 exposes it.</summary>
    public short ILives = MissionDef.UnlimitedLives;

    /// <summary><c>c_cUnlimitedLives = 0x7fff</c> (<c>src/Igc/igc.h:1007</c>), the
    /// <c>MissionParams::Reset</c> default (<c>igc.h:1291</c>).</summary>
    public const short UnlimitedLives = 0x7fff;

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

    // Native tail after leaders: Inc/Messages.h:312-317. Byte offsets follow
    // the six short leaders at 814, then six char-sized entries per array.
    public byte[] RgcPlayers = [];
    public bool[] RgfAutoAccept = [];
    public bool[] RgfReady = [];
    public bool[] RgfForceReady = [];
    public bool[] RgfActive = [];
    public sbyte[] RgfAllies = [];

    /// <summary><c>MissionParams.bLockGameOpen</c> (igc.h:1139) — the mission
    /// was created with the game-open lock on; gates Auto-Accept and Lock Lobby
    /// OFF in the team screen (teamscreen.cpp:1563-1569). Decode-only: the
    /// port's own MISSIONDEF encoder zeroes the bitfield (the port's server
    /// config has no such flag yet), so it reads false from our own servers.</summary>
    public bool BLockGameOpen;

    /// <summary>P856 — <c>MissionParams.bAllowFriendlyFire</c>
    /// (<c>Igc/igc.h:1149</c>, bit 28 of the bitfield block). Native clients read
    /// it straight out of the mission definition they were sent
    /// (<c>MissionInfo::GetMissionParams()</c>, <c>clintlib/clintlib.h:557</c>).</summary>
    public bool AllowFriendlyFire;

    /// <summary>P856 — <c>MissionParams.bAllowAlliedViz</c>
    /// (<c>Igc/igc.h:1127</c>, bit 6 of the bitfield block). See
    /// <see cref="AllowFriendlyFire"/> for the client read path. </summary>
    public bool AllowAlliedViz;

    /// <summary>P853 — <c>MissionParams.iMinRank</c> (igc.h:1200). The native
    /// <c>Reset</c> default is -1 (no minimum, igc.h:1301).</summary>
    public short IMinRank = -1;

    /// <summary>P853 — <c>MissionParams.iMaxRank</c> (igc.h:1201). The native
    /// <c>Reset</c> default is 1000 (igc.h:1302).</summary>
    public short IMaxRank = 1000;

    /// <summary>P853 — <c>MissionParams.nMinPlayersPerTeam</c> (igc.h:1232;
    /// byte, <c>Reset</c> default 1, igc.h:1318).</summary>
    public byte NMinPlayersPerTeam = 1;

    /// <summary>P853 — <c>MissionParams.nMaxPlayersPerTeam</c> (igc.h:1233;
    /// byte, <c>Reset</c> default 100, igc.h:1319).</summary>
    public byte NMaxPlayersPerTeam = 100;

    /// <summary>P853 — <c>MissionParams.nTotalMaxPlayersPerGame</c> (igc.h:1237;
    /// <c>Reset</c> default 0x7FFF, igc.h:1320). The FM_CS_MISSIONPARAMS handler
    /// TrapHackBoots any client echo that does not match the mission's value
    /// (FedSrv.CPP:7547-7548), so the advertisement must carry it.</summary>
    public short NTotalMaxPlayersPerGame = unchecked((short)0x7FFF);

    /// <summary>P853b — the WHOLE embedded <c>MissionParams</c> block, when the
    /// sender has one. Native's client memcpy's the entire struct out of
    /// MISSIONDEF (<c>appmsg.cpp:2416</c> → <c>clintlib.cpp:670-673</c>
    /// <c>m_pfmMissionDef</c>) and its game-settings screen re-sends that struct
    /// with only the user's edits applied (<c>newgamescreen.cpp:968-1031</c>,
    /// <c>1034-1130</c>), so a client that cannot see the whole block would have
    /// to send zeros for the fields it never learned (treasure rates, He3
    /// density, starting money, the unmodelled bool:1 flags, …) and the server
    /// would adopt them (<c>fsmission.cpp:1843</c>). When this is set,
    /// <see cref="Encode"/> stamps it over the params block and it is
    /// authoritative for the cells it covers; the typed fields above stay as a
    /// compatibility view of the same block. Null (the codec's own tests and any
    /// caller that only cares about the typed fields) leaves the historical
    /// zero-filled representation untouched.</summary>
    public MissionParamsMsg? Params;

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
        // params 108..112 (five bytes), KBlevel short at 114, fGoalTeamMoney
        // float at 116, verIGCcore int at 120 (absolute 124). Populate the
        // money multiplier and version below; the other cells remain zero.
        // Native MissionParams.fGoalTeamMoney at 116 (Igc/igc.h:1158).
        w.Advance(MisparmsOffset + 116 - w.Position);
        w.WriteI32(BitConverter.SingleToInt32Bits(Goals.TeamMoney));
        w.Advance(VerIgcCoreOffset - w.Position);   // 78 -> 124 (gap: custom map + password + bitfields + money etc.)
        w.WriteI32(VerIgcCore);                     // verIGCcore (int) at 124
        w.Advance(GameLengthOffset - w.Position);
        w.WriteI32(BitConverter.SingleToInt32Bits(GameLengthSeconds));
        w.Advance(TimeStartOffset - w.Position);
        w.WriteU32(TimeStart);
        // rgCivID (short[6]) at 172; nTeams (short) at 222. Skip the gap, write
        // the civ ids, then the team count, then finish the MissionParams block.
        w.Advance(RgCivIdOffset - w.Position);      // 160 -> 172
        for (int i = 0; i < 6; i++)                 // rgCivID[c_cSidesMax] = short[6]
            w.WriteI16(i < RgCivId.Length ? RgCivId[i] : (short)0);
        // Native shorts following rgCivID[6] (Igc/igc.h:1174-1179).
        w.WriteI16(Goals.ConquestPercentage);   // params +180
        w.WriteI16(Goals.TerritoryPercentage);  // +182; 100 disables
        w.WriteI16(100);                       // +184 unused artifacts percentage, Reset default
        w.WriteI16(Goals.Flags);               // +186
        w.WriteI16(Goals.Artifacts);           // +188
        w.WriteI16(Goals.TeamKills);           // +190
        // HUD-12 — iLives (short) at absolute 208 (params offset 204), the native
        // MissionParams slot the lives badge reads (console.cpp:812-817).
        w.Advance(LivesOffset - w.Position);        // 184 -> 208
        w.WriteI16(ILives);
        w.Advance(NTemsOffset - w.Position);        // 210 -> 222
        w.WriteI16(NTems);                          // nTeams (short) at 222
        // P853 — the params tail the FM_CS_MISSIONPARAMS echo guards compare
        // against (see the offset constants above).
        w.Advance(IMinRankOffset - w.Position);
        w.WriteI16(IMinRank);
        w.WriteI16(IMaxRank);
        w.Advance(NMinPlayersPerTeamOffset - w.Position);
        w.WriteByte(NMinPlayersPerTeam);
        w.WriteByte(NMaxPlayersPerTeam);
        w.Advance(NTotalMaxPlayersPerGameOffset - w.Position);
        w.WriteI16(NTotalMaxPlayersPerGame);
        w.Advance(MissionParamsSize - (NTotalMaxPlayersPerGameOffset - MisparmsOffset) - 2); // finish MissionParams after the total-max short
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

        for (int i = 0; i < 6; i++)
        {
            buf[826 + i] = i < RgcPlayers.Length ? RgcPlayers[i] : (byte)0;
            buf[832 + i] = (byte)(i < RgfAutoAccept.Length && RgfAutoAccept[i] ? 1 : 0);
            buf[838 + i] = (byte)(i < RgfReady.Length && RgfReady[i] ? 1 : 0);
            buf[844 + i] = (byte)(i < RgfForceReady.Length && RgfForceReady[i] ? 1 : 0);
            buf[850 + i] = (byte)(i < RgfActive.Length && RgfActive[i] ? 1 : 0);
            buf[856 + i] = unchecked((byte)(i < RgfAllies.Length ? RgfAllies[i] : 0));
        }

        // pad to exact total first, then stamp cbmsg = the full 864 (a message
        // length is the whole thing, not just the fields we happened to write).
        // P856 — stamp the native bool:1 block (params offset 108, five bytes):
        // bAllowAlliedViz bit 6, bLockGameOpen bit 18, bAllowFriendlyFire bit 28.
        WriteBitfield(buf, AllowAlliedVizBit, AllowAlliedViz);
        WriteBitfield(buf, BLockGameOpenBit, BLockGameOpen);
        WriteBitfield(buf, AllowFriendlyFireBit, AllowFriendlyFire);

        // P853b — when the caller hands over the whole params block, it is
        // authoritative for the 252-byte MissionParams region (header excluded:
        // the embedded struct starts at offset 4 in both messages, so the
        // MISSIONPARAMS encoding's bytes [4..256) land on MISSIONDEF [4..256)).
        if (Params is { } whole)
        {
            var paramsBytes = whole.Encode();
            Array.Copy(paramsBytes, 4, buf, MisparmsOffset, MissionParamsSize);
        }

        if (w.Position < FixedSize) w.Advance(FixedSize - w.Position);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"MISSIONDEF internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)FixedSize);
        // Re-validate the two essential absolute placements independently of
        // the cursor arithmetic above (belt-and-braces against a silent pad slip).
        // szIGCStaticFile is compared against its truncated written form (a name
        // longer than the buffer is clipped to width-1, keeping the NUL). When
        // the params block was stamped, THAT is what landed in the buffer, so
        // check it against its own values.
        if (ReadFixedString(buf, SzIGCStaticFileOffset, StaticFileBufSize)
            != TruncatedFixedString(Params?.SzIgcStaticFile ?? SzIGCStaticFile))
            throw new InvalidOperationException("MISSIONDEF szIGCStaticFile mispositioned");
        if (BitConverter.ToInt32(buf, VerIgcCoreOffset) != (Params?.VerIgcCore ?? VerIgcCore))
            throw new InvalidOperationException("MISSIONDEF verIGCcore mispositioned");
        if (BitConverter.ToInt16(buf, LivesOffset) != (Params?.ILives ?? ILives))
            throw new InvalidOperationException("MISSIONDEF iLives mispositioned");
        return buf;
    }

    /// <summary>Set one bit of the native <c>MissionParams</c> bool:1 block
    /// (params offset 108 onward; MSVC packs LSB-first).</summary>
    private static void WriteBitfield(byte[] buf, int bit, bool value)
    {
        if (!value) return;
        buf[BitfieldsOffset + bit / 8] |= (byte)(1 << (bit % 8));
    }

    /// <summary>Read one bit of the native bool:1 block at params offset 108.</summary>
    private static bool ReadBitfield(ReadOnlySpan<byte> buf, int bit)
        => (buf[BitfieldsOffset + bit / 8] & (1 << (bit % 8))) != 0;

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
        bool lockGameOpen = ReadBitfield(buf, BLockGameOpenBit);
        // P853 — the params tail (see the offset constants above).
        short minRank = BitConverter.ToInt16(buf.Slice(IMinRankOffset));
        short maxRank = BitConverter.ToInt16(buf.Slice(IMaxRankOffset));
        byte minPerTeam = buf[NMinPlayersPerTeamOffset];
        byte maxPerTeam = buf[NMaxPlayersPerTeamOffset];
        short totalMax = BitConverter.ToInt16(buf.Slice(NTotalMaxPlayersPerGameOffset));

        return new MissionDef
        {
            SzIGCStaticFile = staticFile,
            VerIgcCore = ver,
            DwCookie = cookie,
            ISideMissionOwner = owner,
            Stage = stage,
            AllowFriendlyFire = ReadBitfield(buf, AllowFriendlyFireBit),
            AllowAlliedViz = ReadBitfield(buf, AllowAlliedVizBit),
            Goals = new()
            {
                TeamMoney = BitConverter.ToSingle(buf.Slice(MisparmsOffset + 116)),
                GameLengthSeconds = BitConverter.ToSingle(buf.Slice(GameLengthOffset)),
                ConquestPercentage = BitConverter.ToInt16(buf.Slice(MisparmsOffset + 180)),
                TerritoryPercentage = BitConverter.ToInt16(buf.Slice(MisparmsOffset + 182)),
                Flags = BitConverter.ToInt16(buf.Slice(MisparmsOffset + 186)),
                Artifacts = BitConverter.ToInt16(buf.Slice(MisparmsOffset + 188)),
                TeamKills = BitConverter.ToInt16(buf.Slice(MisparmsOffset + 190)),
            },
            TimeStart = BitConverter.ToUInt32(buf.Slice(TimeStartOffset)),
            ILives = BitConverter.ToInt16(buf.Slice(LivesOffset)),
            NTems = nTems,
            RgCivId = rgCivId,
            RgShipIdLeaders = leaders,
            RgcPlayers = buf.Slice(826, 6).ToArray(),
            RgfAutoAccept = buf.Slice(832, 6).ToArray().Select(b => b != 0).ToArray(),
            RgfReady = buf.Slice(838, 6).ToArray().Select(b => b != 0).ToArray(),
            RgfForceReady = buf.Slice(844, 6).ToArray().Select(b => b != 0).ToArray(),
            RgfActive = buf.Slice(850, 6).ToArray().Select(b => b != 0).ToArray(),
            RgfAllies = buf.Slice(856, 6).ToArray().Select(b => unchecked((sbyte)b)).ToArray(),
            BLockGameOpen = lockGameOpen,
            IMinRank = minRank,
            IMaxRank = maxRank,
            NMinPlayersPerTeam = minPerTeam,
            NMaxPlayersPerTeam = maxPerTeam,
            NTotalMaxPlayersPerGame = totalMax,
            // P853b — the whole embedded params block (see the field remarks).
            // MISSIONDEF carries it at the same offset 4 MISSIONPARAMS uses, so
            // the params codec decodes the region directly.
            Params = MissionParamsMsg.DecodeParams(buf.Slice(MisparmsOffset, MissionParamsSize)),
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
