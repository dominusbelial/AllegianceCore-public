using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_MISSIONPARAMS</c> (id 113, <c>Inc/Messages.h</c>:
/// <c>DEFINE_FEDMSG(CS, MISSIONPARAMS, 113) MissionParams missionparams;
/// END_FEDMSG</c>) — the mission owner's live game-settings edit. The whole
/// payload is one native <c>MissionParams</c> struct (<c>Igc/igc.h:1109-1251</c> —
/// the struct DOES declare there; the earlier "no declaration anywhere" finding
/// in <see cref="NotSupportedFedMsgs"/> was wrong and is superseded). The server
/// accepts it only from the mission owner while the mission is
/// <c>STAGE_NOTSTARTED</c>, validates it with <c>MissionParams::Invalid(true)</c>
/// (<c>igc.h:1339-1417</c>), overrides the fields players cannot change and
/// applies the rest via <c>CFSMission::SetMissionParams</c>
/// (<c>FedSrv/FedSrv.CPP:7518-7597</c>, <c>FedSrv/fsmission.cpp:1796-1895</c>).
/// <code>
///   offset 0:  ushort cbmsg
///   offset 2:  ushort fmid = 113
///   offset 4:  MissionParams missionparams (252 bytes, MSVC default packing):
///     +0    char[65] strGameName            (c_cbGameName = 65, igc.h:994)
///     +65   char[13] szIGCStaticFile        (c_cbFileName = 13)
///     +78   char[13] szCustomMapFile
///     +91   char[17] strGamePassword        (c_cbGamePassword = 17, igc.h:995)
///     +108  36 bool:1 bitfields (5 bytes, MSVC LSB-first; igc.h:1122-1157)
///     +113  1 pad byte
///     +114  short    KBlevel                (Spunky #300)
///     +116  float    fGoalTeamMoney
///     +120  int      verIGCcore
///     +124  float    nPlayerSectorTreasureRate
///     +128  float    nNeutralSectorTreasureRate
///     +132  float    dtGameLength
///     +136  float    fHe3Density
///     +140  int      m_killPenalty / +144 m_killReward
///     +148  int      m_ejectPenalty / +152 m_ejectReward
///     +156  uint     timeStart
///     +160  float    fStartCountdown / +164 fRestartCountdown
///     +168  short[6] rgCivID                (c_cSidesMax = 6, igc.h:507)
///     +180  short    iGoalConquestPercentage
///     +182  short    iGoalTerritoryPercentage
///     +184  short    iGoalArtifactsPercentage
///     +186  short    nGoalFlagsCount
///     +188  short    nGoalArtifactsCount
///     +190  short    nGoalTeamKills
///     +192  short    tsiPlayerStart / +194 tsiNeutralStart
///     +196  short    tsiPlayerRegenerate / +198 tsiNeutralRegenerate
///     +200  float    fStartingMoney
///     +204  short    iLives
///     +206  short    mmMapType (MapMakerID, igc.h:977-991)
///     +208  short    iMapSize / +210 iRandomEncounters / +212 bNeutralSectors
///     +214  short    iAlephPositioning / +216 iAlephsPerSector
///     +218  short    nTeams
///     +220  short    iMinRank / +222 iMaxRank
///     +224  int      nInvitationListID
///     +228  short    iMaxImbalance
///     +230  short    nPlayerSectorAsteroids / +232 nNeutralSectorAsteroids
///     +234  short    nPlayerSectorTreasures / +236 nNeutralSectorTreasures
///     +238  short    nPlayerSectorMineableAsteroids / +240 nNeutralSectorMineableAsteroids
///     +242  short    nPlayerSectorSpecialAsteroids / +244 nNeutralSectorSpecialAsteroids
///     +246  byte     nMinPlayersPerTeam
///     +247  byte     nMaxPlayersPerTeam
///     +248  char     nInitialMinersPerTeam / +249 char nMaxDronesPerTeam
///     +250  short    nTotalMaxPlayersPerGame
/// </code>
/// Wire size = header(4) + 252 = <b>256 bytes</b> (the struct widths match the
/// P29 MSVC probe: sizeof(MissionParams) = 252, szIGCStaticFile at +65,
/// verIGCcore at +120, timeStart at +156, rgCivID at +168, iLives at +204,
/// nTeams at +218 — all confirmed on the wire by the MissionDef codec).
/// </summary>
public sealed class MissionParamsMsg : IFedMessage
{
    public const ushort Fmid = 113;
    public const int FixedSize = 256;          // header(4) + MissionParams(252)
    public const int MissionParamsSize = 252;

    // ---- named absolute wire offsets (message-relative = header + params offset)
    public const int ParamsOffset = 4;
    public const int StrGameNameOffset = ParamsOffset + 0;
    public const int SzIgcStaticFileOffset = ParamsOffset + 65;
    public const int SzCustomMapFileOffset = ParamsOffset + 78;
    public const int StrGamePasswordOffset = ParamsOffset + 91;
    public const int BitfieldsOffset = ParamsOffset + 108;   // 5 bytes, 36 bool:1 bits
    public const int KbLevelOffset = ParamsOffset + 114;
    public const int FGoalTeamMoneyOffset = ParamsOffset + 116;
    public const int VerIgcCoreOffset = ParamsOffset + 120;
    public const int NPlayerSectorTreasureRateOffset = ParamsOffset + 124;
    public const int NNeutralSectorTreasureRateOffset = ParamsOffset + 128;
    public const int DtGameLengthOffset = ParamsOffset + 132;
    public const int FHe3DensityOffset = ParamsOffset + 136;
    public const int MKillPenaltyOffset = ParamsOffset + 140;
    public const int MKillRewardOffset = ParamsOffset + 144;
    public const int MEjectPenaltyOffset = ParamsOffset + 148;
    public const int MEjectRewardOffset = ParamsOffset + 152;
    public const int TimeStartOffset = ParamsOffset + 156;
    public const int FStartCountdownOffset = ParamsOffset + 160;
    public const int FRestartCountdownOffset = ParamsOffset + 164;
    public const int RgCivIdOffset = ParamsOffset + 168;
    public const int GoalsOffset = ParamsOffset + 180;       // 6 shorts
    public const int TsiPlayerStartOffset = ParamsOffset + 192;
    public const int TsiNeutralStartOffset = ParamsOffset + 194;
    public const int TsiPlayerRegenerateOffset = ParamsOffset + 196;
    public const int TsiNeutralRegenerateOffset = ParamsOffset + 198;
    public const int FStartingMoneyOffset = ParamsOffset + 200;
    public const int ILivesOffset = ParamsOffset + 204;
    public const int MmMapTypeOffset = ParamsOffset + 206;
    public const int IMapSizeOffset = ParamsOffset + 208;
    public const int IRandomEncountersOffset = ParamsOffset + 210;
    public const int BNeutralSectorsOffset = ParamsOffset + 212;
    public const int IAlephPositioningOffset = ParamsOffset + 214;
    public const int IAlephsPerSectorOffset = ParamsOffset + 216;
    public const int NTemsOffset = ParamsOffset + 218;
    public const int IMinRankOffset = ParamsOffset + 220;
    public const int IMaxRankOffset = ParamsOffset + 222;
    public const int NInvitationListIdOffset = ParamsOffset + 224;
    public const int IMaxImbalanceOffset = ParamsOffset + 228;
    public const int NPlayerSectorAsteroidsOffset = ParamsOffset + 230;
    public const int NNeutralSectorAsteroidsOffset = ParamsOffset + 232;
    public const int NPlayerSectorTreasuresOffset = ParamsOffset + 234;
    public const int NNeutralSectorTreasuresOffset = ParamsOffset + 236;
    public const int NPlayerSectorMineableAsteroidsOffset = ParamsOffset + 238;
    public const int NNeutralSectorMineableAsteroidsOffset = ParamsOffset + 240;
    public const int NPlayerSectorSpecialAsteroidsOffset = ParamsOffset + 242;
    public const int NNeutralSectorSpecialAsteroidsOffset = ParamsOffset + 244;
    public const int NMinPlayersPerTeamOffset = ParamsOffset + 246;
    public const int NMaxPlayersPerTeamOffset = ParamsOffset + 247;
    public const int NInitialMinersPerTeamOffset = ParamsOffset + 248;
    public const int NMaxDronesPerTeamOffset = ParamsOffset + 249;
    public const int NTotalMaxPlayersPerGameOffset = ParamsOffset + 250;

    /// <summary>Native <c>c_cSidesMax = 6</c> (igc.h:507) — the count of the
    /// 36 <c>bool:1</c> bitfield entries below and the <c>rgCivID</c> width.</summary>
    public const int CSidesMax = 6;

    // ---- the 36 bool:1 bitfield indices, in igc.h:1122-1157 declaration order
    // (MSVC packs bool:1 units LSB-first; the three indices the port already
    // reads on MissionDef — bAllowAlliedViz 6, bLockGameOpen 18,
    // bAllowFriendlyFire 28 — agree with the P761/P856 mapping).
    public const int BitEjectPods = 0;
    public const int BitInvulnerableStations = 1;
    public const int BitShowMap = 2;
    public const int BitAllowPrivateTeams = 3;
    public const int BitAllowEmptyTeams = 4;
    public const int BitAllowAlliedRip = 5;
    public const int BitAllowAlliedViz = 6;
    public const int BitAllowDevelopments = 7;
    public const int BitAllowShipyardPath = 8;
    public const int BitAllowTacticalPath = 9;
    public const int BitAllowSupremacyPath = 10;
    public const int BitAllowExpansionPath = 11;
    public const int BitPowerUps = 12;
    public const int BitAllowDefections = 13;
    public const int BitAllowJoiners = 14;
    public const int BitLockLobby = 15;
    public const int BitLockSides = 16;
    public const int BitLockTeamSettings = 17;
    public const int BitLockGameOpen = 18;
    public const int BitStations = 19;
    public const int BitScoresCount = 20;
    public const int BitSquadGame = 21;
    public const int BitDrones = 22;
    public const int BitIResources = 23;
    public const int BitResourceAmountsVisible = 24;
    public const int BitRandomWormholes = 25;
    public const int BitNoTeams = 26;
    public const int BitShowHomeSector = 27;
    public const int BitAllowFriendlyFire = 28;
    public const int BitObjectModelCreated = 29;
    public const int BitLobbiedGame = 30;
    public const int BitClubGame = 31;
    public const int BitAutoStart = 32;
    public const int BitAutoRestart = 33;
    public const int BitAllowRestart = 34;
    public const int BitExperimental = 35;
    public const int BitfieldCount = 36;

    public string StrGameName = "";
    public string SzIgcStaticFile = "";
    public string SzCustomMapFile = "";
    public string StrGamePassword = "";

    /// <summary>The 36 <c>bool:1</c> flags, indexed by the <c>Bit*</c> constants
    /// above (igc.h:1122-1157 declaration order).</summary>
    public bool[] Bits = new bool[BitfieldCount];

    public bool this[int bit]
    {
        get => Bits[bit];
        set => Bits[bit] = value;
    }

    public short KbLevel;
    public float FGoalTeamMoney;
    public int VerIgcCore;
    public float NPlayerSectorTreasureRate;
    public float NNeutralSectorTreasureRate;
    public float DtGameLength;
    public float FHe3Density;
    public int MKillPenalty;
    public int MKillReward;
    public int MEjectPenalty;
    public int MEjectReward;
    public uint TimeStart;
    public float FStartCountdown;
    public float FRestartCountdown;
    public short[] RgCivId = new short[CSidesMax];
    public short IGoalConquestPercentage;
    public short IGoalTerritoryPercentage;
    public short IGoalArtifactsPercentage;
    public short NGoalFlagsCount;
    public short NGoalArtifactsCount;
    public short NGoalTeamKills;
    public short TsiPlayerStart = 1;
    public short TsiNeutralStart = 1;
    public short TsiPlayerRegenerate = 1;
    public short TsiNeutralRegenerate = 1;
    public float FStartingMoney = 1f;
    public short ILives = MissionDef.UnlimitedLives;
    public short MmMapType;
    public short IMapSize;
    public short IRandomEncounters;
    public short BNeutralSectors;
    public short IAlephPositioning;
    public short IAlephsPerSector;
    public short NTems;
    public short IMinRank = -1;
    public short IMaxRank = 1000;
    public int NInvitationListId;
    public short IMaxImbalance = 1;
    public short NPlayerSectorAsteroids = 20;
    public short NNeutralSectorAsteroids = 20;
    public short NPlayerSectorTreasures;
    public short NNeutralSectorTreasures = 5;
    public short NPlayerSectorMineableAsteroids = 2;
    public short NNeutralSectorMineableAsteroids = 4;
    public short NPlayerSectorSpecialAsteroids = 1;
    public short NNeutralSectorSpecialAsteroids = 1;
    public byte NMinPlayersPerTeam = 1;
    public byte NMaxPlayersPerTeam = 100;
    public sbyte NInitialMinersPerTeam = 1;
    public sbyte NMaxDronesPerTeam = 4;
    public short NTotalMaxPlayersPerGame = unchecked((short)0x7FFF);

    public ushort GetFmid() => Fmid;

    /// <summary>P853b — a deep copy of this params struct. The client's
    /// game-settings form starts from the mission's advertised params and must
    /// never mutate the world's snapshot in place (native's form reads a local
    /// <c>MissionParams</c> copy, <c>WinTrek/newgamescreen.cpp:968-1031</c>), so
    /// the draft is cloned through the codec — the same
    /// <see cref="Encode"/>/<see cref="Decode"/> pair the wire uses, which also
    /// keeps the two in lock-step if a field is ever added.</summary>
    public MissionParamsMsg Clone() => Decode(Encode());

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                   // cbmsg placeholder
        w.WriteU16(Fmid);
        WriteFixedString(buf, StrGameNameOffset, StrGameName, 65);
        WriteFixedString(buf, SzIgcStaticFileOffset, SzIgcStaticFile, 13);
        WriteFixedString(buf, SzCustomMapFileOffset, SzCustomMapFile, 13);
        WriteFixedString(buf, StrGamePasswordOffset, StrGamePassword, 17);
        // The bitfields are written straight into the buffer at their absolute
        // offsets (the writer cursor is still behind them at the string block).
        for (int i = 0; i < BitfieldCount; i++)
        {
            if (!Bits[i]) continue;
            buf[BitfieldsOffset + i / 8] |= (byte)(1 << (i % 8));
        }
        w.Advance(KbLevelOffset - w.Position);
        w.WriteI16(KbLevel);
        w.WriteI32(BitConverter.SingleToInt32Bits(FGoalTeamMoney));
        w.WriteI32(VerIgcCore);
        w.WriteI32(BitConverter.SingleToInt32Bits(NPlayerSectorTreasureRate));
        w.WriteI32(BitConverter.SingleToInt32Bits(NNeutralSectorTreasureRate));
        w.WriteI32(BitConverter.SingleToInt32Bits(DtGameLength));
        w.WriteI32(BitConverter.SingleToInt32Bits(FHe3Density));
        w.WriteI32(MKillPenalty);
        w.WriteI32(MKillReward);
        w.WriteI32(MEjectPenalty);
        w.WriteI32(MEjectReward);
        w.WriteU32(TimeStart);
        w.WriteI32(BitConverter.SingleToInt32Bits(FStartCountdown));
        w.WriteI32(BitConverter.SingleToInt32Bits(FRestartCountdown));
        for (int i = 0; i < CSidesMax; i++) w.WriteI16(i < RgCivId.Length ? RgCivId[i] : (short)0);
        w.WriteI16(IGoalConquestPercentage);
        w.WriteI16(IGoalTerritoryPercentage);
        w.WriteI16(IGoalArtifactsPercentage);
        w.WriteI16(NGoalFlagsCount);
        w.WriteI16(NGoalArtifactsCount);
        w.WriteI16(NGoalTeamKills);
        w.WriteI16(TsiPlayerStart);
        w.WriteI16(TsiNeutralStart);
        w.WriteI16(TsiPlayerRegenerate);
        w.WriteI16(TsiNeutralRegenerate);
        w.WriteI32(BitConverter.SingleToInt32Bits(FStartingMoney));
        w.WriteI16(ILives);
        w.WriteI16(MmMapType);
        w.WriteI16(IMapSize);
        w.WriteI16(IRandomEncounters);
        w.WriteI16(BNeutralSectors);
        w.WriteI16(IAlephPositioning);
        w.WriteI16(IAlephsPerSector);
        w.WriteI16(NTems);
        w.WriteI16(IMinRank);
        w.WriteI16(IMaxRank);
        w.WriteI32(NInvitationListId);
        w.WriteI16(IMaxImbalance);
        w.WriteI16(NPlayerSectorAsteroids);
        w.WriteI16(NNeutralSectorAsteroids);
        w.WriteI16(NPlayerSectorTreasures);
        w.WriteI16(NNeutralSectorTreasures);
        w.WriteI16(NPlayerSectorMineableAsteroids);
        w.WriteI16(NNeutralSectorMineableAsteroids);
        w.WriteI16(NPlayerSectorSpecialAsteroids);
        w.WriteI16(NNeutralSectorSpecialAsteroids);
        w.WriteByte(NMinPlayersPerTeam);
        w.WriteByte(NMaxPlayersPerTeam);
        w.WriteByte(unchecked((byte)NInitialMinersPerTeam));
        w.WriteByte(unchecked((byte)NMaxDronesPerTeam));
        w.WriteI16(NTotalMaxPlayersPerGame);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"MISSIONPARAMS internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    /// <summary>P853b — decode a bare 252-byte <c>MissionParams</c> block, i.e.
    /// one without the fedmsg header this message normally carries. MISSIONDEF
    /// embeds exactly that block at its own offset 4
    /// (<c>offsetof(FMD_S_MISSIONDEF, misparms)</c> = 4, P29 probe); synthesizing
    /// the 4-byte header here lets the one codec serve both placements instead of
    /// duplicating the field decodes.</summary>
    public static MissionParamsMsg DecodeParams(ReadOnlySpan<byte> block)
    {
        if (block.Length != MissionParamsSize)
            throw new ArgumentException($"MISSIONPARAMS block needs exactly {MissionParamsSize} bytes, got {block.Length}");
        var tmp = new byte[FixedSize];
        tmp[0] = (byte)(FixedSize & 0xFF);
        tmp[1] = (byte)(FixedSize >> 8);
        tmp[2] = (byte)(Fmid & 0xFF);
        tmp[3] = (byte)(Fmid >> 8);
        block.CopyTo(tmp.AsSpan(ParamsOffset));
        return Decode(tmp);
    }

    public static MissionParamsMsg Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length != FixedSize)
            throw new ArgumentException($"MISSIONPARAMS needs exactly {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSIONPARAMS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != FixedSize) throw new ArgumentException($"cbmsg={hdr.CbMsg} != fixed {FixedSize}");

        var p = new MissionParamsMsg
        {
            StrGameName = ReadFixedString(buf, StrGameNameOffset, 65),
            SzIgcStaticFile = ReadFixedString(buf, SzIgcStaticFileOffset, 13),
            SzCustomMapFile = ReadFixedString(buf, SzCustomMapFileOffset, 13),
            StrGamePassword = ReadFixedString(buf, StrGamePasswordOffset, 17),
            KbLevel = BitConverter.ToInt16(buf.Slice(KbLevelOffset)),
            FGoalTeamMoney = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FGoalTeamMoneyOffset))),
            VerIgcCore = BitConverter.ToInt32(buf.Slice(VerIgcCoreOffset)),
            NPlayerSectorTreasureRate = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(NPlayerSectorTreasureRateOffset))),
            NNeutralSectorTreasureRate = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(NNeutralSectorTreasureRateOffset))),
            DtGameLength = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(DtGameLengthOffset))),
            FHe3Density = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FHe3DensityOffset))),
            MKillPenalty = BitConverter.ToInt32(buf.Slice(MKillPenaltyOffset)),
            MKillReward = BitConverter.ToInt32(buf.Slice(MKillRewardOffset)),
            MEjectPenalty = BitConverter.ToInt32(buf.Slice(MEjectPenaltyOffset)),
            MEjectReward = BitConverter.ToInt32(buf.Slice(MEjectRewardOffset)),
            TimeStart = BitConverter.ToUInt32(buf.Slice(TimeStartOffset)),
            FStartCountdown = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FStartCountdownOffset))),
            FRestartCountdown = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FRestartCountdownOffset))),
            IGoalConquestPercentage = BitConverter.ToInt16(buf.Slice(GoalsOffset)),
            IGoalTerritoryPercentage = BitConverter.ToInt16(buf.Slice(GoalsOffset + 2)),
            IGoalArtifactsPercentage = BitConverter.ToInt16(buf.Slice(GoalsOffset + 4)),
            NGoalFlagsCount = BitConverter.ToInt16(buf.Slice(GoalsOffset + 6)),
            NGoalArtifactsCount = BitConverter.ToInt16(buf.Slice(GoalsOffset + 8)),
            NGoalTeamKills = BitConverter.ToInt16(buf.Slice(GoalsOffset + 10)),
            TsiPlayerStart = BitConverter.ToInt16(buf.Slice(TsiPlayerStartOffset)),
            TsiNeutralStart = BitConverter.ToInt16(buf.Slice(TsiNeutralStartOffset)),
            TsiPlayerRegenerate = BitConverter.ToInt16(buf.Slice(TsiPlayerRegenerateOffset)),
            TsiNeutralRegenerate = BitConverter.ToInt16(buf.Slice(TsiNeutralRegenerateOffset)),
            FStartingMoney = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FStartingMoneyOffset))),
            ILives = BitConverter.ToInt16(buf.Slice(ILivesOffset)),
            MmMapType = BitConverter.ToInt16(buf.Slice(MmMapTypeOffset)),
            IMapSize = BitConverter.ToInt16(buf.Slice(IMapSizeOffset)),
            IRandomEncounters = BitConverter.ToInt16(buf.Slice(IRandomEncountersOffset)),
            BNeutralSectors = BitConverter.ToInt16(buf.Slice(BNeutralSectorsOffset)),
            IAlephPositioning = BitConverter.ToInt16(buf.Slice(IAlephPositioningOffset)),
            IAlephsPerSector = BitConverter.ToInt16(buf.Slice(IAlephsPerSectorOffset)),
            NTems = BitConverter.ToInt16(buf.Slice(NTemsOffset)),
            IMinRank = BitConverter.ToInt16(buf.Slice(IMinRankOffset)),
            IMaxRank = BitConverter.ToInt16(buf.Slice(IMaxRankOffset)),
            NInvitationListId = BitConverter.ToInt32(buf.Slice(NInvitationListIdOffset)),
            IMaxImbalance = BitConverter.ToInt16(buf.Slice(IMaxImbalanceOffset)),
            NPlayerSectorAsteroids = BitConverter.ToInt16(buf.Slice(NPlayerSectorAsteroidsOffset)),
            NNeutralSectorAsteroids = BitConverter.ToInt16(buf.Slice(NNeutralSectorAsteroidsOffset)),
            NPlayerSectorTreasures = BitConverter.ToInt16(buf.Slice(NPlayerSectorTreasuresOffset)),
            NNeutralSectorTreasures = BitConverter.ToInt16(buf.Slice(NNeutralSectorTreasuresOffset)),
            NPlayerSectorMineableAsteroids = BitConverter.ToInt16(buf.Slice(NPlayerSectorMineableAsteroidsOffset)),
            NNeutralSectorMineableAsteroids = BitConverter.ToInt16(buf.Slice(NNeutralSectorMineableAsteroidsOffset)),
            NPlayerSectorSpecialAsteroids = BitConverter.ToInt16(buf.Slice(NPlayerSectorSpecialAsteroidsOffset)),
            NNeutralSectorSpecialAsteroids = BitConverter.ToInt16(buf.Slice(NNeutralSectorSpecialAsteroidsOffset)),
            NMinPlayersPerTeam = buf[NMinPlayersPerTeamOffset],
            NMaxPlayersPerTeam = buf[NMaxPlayersPerTeamOffset],
            NInitialMinersPerTeam = unchecked((sbyte)buf[NInitialMinersPerTeamOffset]),
            NMaxDronesPerTeam = unchecked((sbyte)buf[NMaxDronesPerTeamOffset]),
            NTotalMaxPlayersPerGame = BitConverter.ToInt16(buf.Slice(NTotalMaxPlayersPerGameOffset)),
        };
        for (int i = 0; i < BitfieldCount; i++)
            p.Bits[i] = (buf[BitfieldsOffset + i / 8] & (1 << (i % 8))) != 0;
        for (int i = 0; i < CSidesMax; i++)
            p.RgCivId[i] = BitConverter.ToInt16(buf.Slice(RgCivIdOffset + i * 2));
        return p;
    }

    private static void WriteFixedString(byte[] buf, int offset, string s, int capacity)
    {
        // char[N]: content, one NUL, zero-fill (native strncpy semantics).
        Array.Clear(buf, offset, capacity);
        if (s.Length == 0) return;
        int n = Math.Min(s.Length, capacity - 1);
        Encoding.Latin1.GetBytes(s.AsSpan(0, n), buf.AsSpan(offset, n));
    }

    private static string ReadFixedString(ReadOnlySpan<byte> buf, int offset, int capacity)
    {
        int len = 0;
        while (len < capacity && buf[offset + len] != 0) len++;
        return Encoding.Latin1.GetString(buf.Slice(offset, len));
    }

    /// <summary>
    /// Native <c>MissionParams::Invalid(bool bFromClient)</c>
    /// (<c>Igc/igc.h:1339-1506</c>) plus the tail of the port's map-maker cut:
    /// returns the native refusal string or null when the params are valid.
    /// The client leg (<c>bFromClient</c>) adds the game-settings-screen checks
    /// native runs before the common ones; the common leg ends in
    /// <c>ImapMakerIGC::IsValid</c> (<c>igc.h:1504</c>), of which only the
    /// DiamondRing (<c>mapmakerigc.cpp:1930-1937</c>) and EastWest
    /// (<c>:2416-2427</c>) team-count rules are non-trivial — every other map
    /// maker's <c>IsValid</c> returns NULL (<c>mapmakerigc.h:195-198,265-268,
    /// 285-288,310-313,335-338,360-363,387-390,496-499,545-548</c>;
    /// Brawl/PinWheel/LargeSplit checks are commented out in native,
    /// <c>mapmakerigc.cpp:1695-1704,1810-1818,2243-2251</c>).
    /// </summary>
    public string? Invalid(bool bFromClient = false)
    {
        if (bFromClient)
        {
            if (NMaxPlayersPerTeam > 100 || NMaxPlayersPerTeam < 1)
                return "Maximum number of players must be between 1 and 100.";
            if (IsNoWinningConditionsGame)
                return "You must choose one or more winning conditions.";
            if (this[BitAutoRestart] || (this[BitObjectModelCreated] && this[BitClubGame]))   // KGJV #62
                return "HACK: one or more admin-only flags are set.";
        }

        if (NInitialMinersPerTeam > NMaxDronesPerTeam)
            return "Initial miners per team must be less than max miners per team.";
        if (NMaxDronesPerTeam > 10)
            return "Max drones per team must be less than or equal to 10.";
        if (NNeutralSectorSpecialAsteroids > 9)
            return "NeutralSectorSpecialAsteroids must be less than 10.";
        if (NPlayerSectorSpecialAsteroids > 9)
            return "PlayerSectorSpecialAsteroids must be less than 10.";
        if (NNeutralSectorMineableAsteroids > 9)
            return "NeutralSectorMineableAsteroids must be less than 10.";
        if (NPlayerSectorMineableAsteroids > 9)
            return "PlayerSectorMineableAsteroids must be less than 10.";
        if (NNeutralSectorTreasureRate > 1f / 60f)
            return "NeutralSectorTreasureRate must be less than 1/60.";
        if (NPlayerSectorTreasureRate > 1f / 60f)
            return "PlayerSectorTreasureRate must be less than 1/60.";
        if (NNeutralSectorTreasures > 20)
            return "NeutralSectorTreasures must be less than 21.";
        if (NPlayerSectorTreasures > 20)
            return "PlayerSectorTreasures must be less than 21.";
        if (NNeutralSectorAsteroids > 40)
            return "NeutralSectorAsteroids must be less than 40.";
        if (NPlayerSectorAsteroids > 40)
            return "PlayerSectorAsteroids must be less than 40.";
        if (NGoalArtifactsCount < 0 || NGoalArtifactsCount > 100)
            return "GoalArtifactsCount must be between 0% and 100%.";
        if (NGoalFlagsCount < 0 || NGoalFlagsCount > 100)
            return "GoalFlagsCount must be between 0% and 100%.";
        if (FHe3Density < 0f || FHe3Density > 9f)
            return "He3Density must be between 0 and 9.";
        if (FGoalTeamMoney < 0f || FGoalTeamMoney > 9f)
            return "GoalTeamMoney must be between 0 and 9.";
        if (FStartingMoney < 0f || FStartingMoney > 9f)
            return "StartingMoney must be between 0 and 9.";
        if (!StrGameName.Contains('\0') && StrGameName.Length >= 65)
            return "invalid game name";
        if (SzIgcStaticFile.Length >= 13)
            return "invalid IGC static file name";
        if (SzCustomMapFile.Length >= 13)
            return "invalid IGC static file name";
        if (StrGamePassword.Length >= 17)
            return "invalid IGC static file name";
        if (NTems > 6 || NTems < 2)
            return "Number of teams must be between 2 and 6.";
        if (NTotalMaxPlayersPerGame < 1)
            return "Number of players per game must be greater than 0.";
        if (NMaxPlayersPerTeam > 200 || NMaxPlayersPerTeam < 1)
            return "Maximum number of players must be between 1 and 200.";
        if (NMinPlayersPerTeam < 1)
            return "Minimum number of players must be 1 or greater.";
        if (NMinPlayersPerTeam > NMaxPlayersPerTeam)
            return "Minimum number of players must not be greater than the maximum number of players.";
        if (IsConquestGame && this[BitInvulnerableStations])
            return "You can't play a conquest game with invulnerable stations; " +
                   "You must disable the conquest winning condition or turn off " +
                   "the invulnerable stations option.";
        if (IsProsperityGame && !this[BitAllowDevelopments])
            return "You can't play a prosperity game with developments turned off; " +
                   "You must disable the prosperity winning condition or turn on " +
                   "developments.";
        if (NPlayerSectorTreasureRate > 0.01f || NNeutralSectorTreasureRate > 0.01f)
            return "Treasure rate(s) too high; max is 0.01";
        if (IsTerritoryGame)
        {
            if (MmMapType == 6 /* c_mmBrawl, igc.h:983 */)
                return "Brawl maps can not have a territory win condition";
            if (NTems == 2 && (MmMapType == 0 /* c_mmSingleRing */ || MmMapType == 1 /* c_mmDoubleRing */))
                return "Small maps can not have a territory win condition";
        }
        return MapMakerIsValid();
    }

    // ---- winning-condition helpers (igc.h:1510-1548) -----------------------
    public bool IsConquestGame => IGoalConquestPercentage != 0;
    public bool IsTerritoryGame => IGoalTerritoryPercentage != 100;
    public bool IsDeathMatchGame => NGoalTeamKills != 0;
    public bool IsCountdownGame => DtGameLength != 0f;
    public bool IsProsperityGame => FGoalTeamMoney != 0f;
    public bool IsArtifactsGame => NGoalArtifactsCount != 0;
    public bool IsFlagsGame => NGoalFlagsCount != 0;
    public bool IsNoWinningConditionsGame => !(IsConquestGame || IsDeathMatchGame ||
        IsCountdownGame || IsProsperityGame || IsArtifactsGame || IsTerritoryGame || IsFlagsGame);

    /// <summary>The port of <c>ImapMakerIGC::IsValid</c>'s dispatch
    /// (<c>Igc/mapmakerigc.cpp:129-167</c>) for the two rules native's map
    /// makers actually enforce; every other type's <c>IsValid</c> is NULL.</summary>
    private string? MapMakerIsValid()
    {
        return MmMapType switch
        {
            3 /* c_mmDiamondRing */ when NTems < 3 =>
                "Diamond ring maps must have more than 2 teams; " +
                "please change the map type, or number of teams.",
            13 /* c_mmEastWest */ when NTems != 2 =>
                "East West maps must have exactly two teams; " +
                "please change the map type, or the number of teams.",
            < 0 or > 14 => "Invalid map type",     // mapmakerigc.cpp:164-166
            _ => null,
        };
    }
}
