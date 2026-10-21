using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PLAYERINFO</c> (id 10, <c>Messages.h:98-118</c>) — the server's
/// per-player roster message, and the fix for the T17 blocker. The client's
/// SET_CLUSTER handler (<c>appmsg.cpp:1034</c>) faults on
/// <c>GetShip()->GetSide()->GetColor()</c> because the direct-connect ship's
/// side is never assigned — T17 showed <c>m_ship-&gt;GetSide()</c> returns NULL.
/// The C++ client assigns the side in <c>BaseClient::AddPlayerToSide</c>
/// (<c>clintlib.cpp:4101</c>, the <c>pship-&gt;SetSide(pside)</c> at <c>:4121</c>),
/// which is reached from this message's handler (<c>appmsg.cpp:2286-2288</c>),
/// NOT from JOINED_MISSION's. So this message is the missing carrier of the
/// real side to the client's ship.
///
/// Layout (derived from MSVC-default packing of the field types and confirmed
/// empirically by the P31 VM probe — see P31_PLAYERINFO.md for the verbatim
/// sizeof/offsetof output): the message is a var item
/// (<c>rgPersistPlayerScores</c>) followed by a fixed region. P774 — the var
/// item now carries the persist list the way native does: one
/// <c>PersistPlayerScore</c> entry per <c>SendPlayerInfo</c> call — the NA
/// entry <c>(0,0,rank,NA)</c> for a player (FedSrv.CPP:952), rank 0 for a
/// drone whose persist is reset (FedSrv.CPP:2073-2077, igc.h:5449-5455) —
/// the client reads the rank back from it (clintlib.cpp:1019-1051,
/// teampane.cpp:1866). The port simplifies native's NA-entry-plus-one-per-civ
/// list to the single NA entry alone: every native rank reader in the client
/// uses NA (teampane.cpp:308,1866, newgamescreen.cpp:1045,
/// gameoverscreen.cpp:129) and the port has no persisted career stats.
/// Fixed-region offsets, absolute from message byte 0:
/// <code>
///   offset 0: ushort cbmsg (total size)
///   offset 2: ushort fmid  = 10
///   offset 4: VarItem (ib, cb) for rgPersistPlayerScores — ib=68, cb=12n
///             (P774: one 12-byte PersistPlayerScore entry, ib/cb point
///             AFTER the fixed region, clintlib.cpp:1019-1040)
///   offset 8: char  CharacterName[25]   ("<name>\0"+pad)
///   offset 33: 3 pad bytes to the 4-aligned int
///   offset 36: int   money = 0
///   offset 40: byte  3×bool:1 bits (fReady/fTeamLeader/fMissionOwner) = 0
///   offset 41: 1 pad byte to the 2-aligned short
///   offset 42: short nKills = 0
///   offset 44: short nEjections = 0
///   offset 46: short nDeaths = 0
///   offset 48: float fExperience      (HUD-20: the ship's KillBonus, FedSrv.CPP:2084)
///   offset 52: short iSide          ← THE FIELD this message exists to carry
///   offset 54: short shipID         (must match the peer's assigned id)
///   offset 56: short rankID = 0
///   offset 58: short dtidDrone        (NA = human, else the drone type id, FedSrv.CPP:2088-2091)
///   offset 60: uchar pilotType = 10 (c_ptPlayer; harmless since the ship is
///                                    reused from the earlier EXPORT — see below)
///   offset 61: 1 pad byte to the 2-aligned short
///   offset 62: short abmOrders = 0
///   offset 64: short baseObjectID = NA (-1)
///   offset 66: 2 pad bytes (trailing, to the struct's 4-byte alignment)
///   = 68-byte fixed region; total message = 68 + 12n bytes (n = 0..20,
///   cCivsMax, FedSrv.CPP:2058). <see cref="MessageSize"/> stays the FIXED
///   region width (68) exactly as <c>sizeof(FMD_S_PLAYERINFO)</c> = 68; the
///   persist entries trail it.
/// </code>
///
/// Field rationale (grep-verified, not guessed):
/// <c>iSide</c> is the whole point — the real side (ship.SideId). <c>shipID</c>
/// must equal the assigned id: the handler uses it for FindPlayerLink, the
/// DataShipIGC, GetShip and AddPlayerToSide (<c>appmsg.cpp:2226/2250/2276/
/// 2288</c>). <c>CharacterName</c> is only displayed. <c>money</c> must be 0 to
/// satisfy <c>AddPlayerToSide</c>'s <c>assert(GetMoney()==0)</c>
/// (<c>clintlib.cpp:4109</c>). Stats (nKills/nEjections/nDeaths/fExperience),
/// bitflags (<c>SetReady</c> is commented out at <c>:2289</c>) and
/// rankID/dtidDrone/abmOrders are unused by the join/side path.
/// <b>baseObjectID is NOT harmless — P78 finding, corrected here.</b> An
/// earlier version of this comment claimed <c>CreateObject(OT_ship,&amp;ds,
/// ...)</c> at <c>appmsg.cpp:2264</c> "fails" because the id already exists
/// (from the earlier ship EXPORT), so this message's own
/// pilotType/baseObjectID would never be consumed. That's wrong:
/// <c>CmissionIGC::CreateObject</c> does not skip an existing id — it still
/// calls <c>CshipIGC::Initialize</c> → <c>ReInitialize</c>
/// (<c>shipIGC.cpp:105-118</c>) on the pre-existing ship, which reads THIS
/// message's <c>baseObjectID</c> unconditionally. Sending <c>0</c> there
/// (instead of NA) previously made every player ship hit
/// <c>else assert(false)</c> at <c>shipIGC.cpp:118</c> (neither
/// <c>c_ptBuilder</c> nor <c>c_ptLayer</c>) — confirmed via Application
/// Verifier + <c>cdb</c> as the real cause of the native client's asteroid-
/// export-adjacent crash (<c>ASTEROID_EXPORT_CRASH_P78.md</c>): the failed
/// assert's own handler (<c>ZAssertImpl</c>, <c>Win32app.cpp:117</c>)
/// null-derefs when <c>g_papp==NULL</c>, which is what actually raised the
/// access violation. <c>NA</c> takes the early-out branch
/// (<c>if (dataShip->baseObjectID == NA) m_pbaseData = NULL;</c>) instead —
/// matching what <c>ExportShip.cs</c>/<c>NewShipInstance.cs</c> already send
/// for the same conceptual field on a ship spawned directly in space, not
/// docked.
///
/// Code-derived prediction for the assert ambiguity (handed to P31b for
/// empirical confirmation): a single PLAYERINFO with the real <c>iSide</c>
/// should suffice. The assert
/// <c>pPlayerInfo-&gt;SideID() == SIDE_TEAMLOBBY</c> (<c>clintlib.cpp:4107</c>)
/// should NOT fire, because the handler calls <c>AddPlayerToMission</c>
/// (<c>appmsg.cpp:2284</c>) before <c>AddPlayerToSide</c> (<c>:2288</c>), and
/// <c>AddPlayerToMission</c> runs <c>SetSideID(SIDE_TEAMLOBBY)</c>
/// (<c>clintlib.cpp:4085</c>) — so <c>SideID()</c> reads <c>SIDE_TEAMLOBBY</c>
/// (-2) by the time the assert runs. Two messages are NOT expected to be
/// needed. Whether this holds on the live client is P31b's instrumentation
/// question, not this task's.
/// </summary>
public sealed class PlayerInfo : IFedMessage
{
    public const ushort Fmid = 10;

    /// <summary>Width of the whole FIXED message = sizeof(FMD_S_PLAYERINFO),
    /// MSVC probe-confirmed 68 bytes (4 header + 4 var item + 60 bytes of fixed
    /// fields/padding). P774 — any persist entries trail this region (12 bytes
    /// each); this constant is the width of the fixed part alone.</summary>
    public const int MessageSize = 68;

    /// <summary>Width of the region after the var item (CharacterName..trailing pad) = MessageSize − 8.</summary>
    public const int FixedSize = MessageSize - (FedMessageHeader.HeaderSize + VarItem.ItemSize);

    // Named absolute wire offsets (from the P31 probe: P31 offsets
    // cchName=8 money=36 nKills=42 nEject=44 nDeaths=46 exp=48 iSide=52
    // shipID=54 rank=56 drone=58 pilotType=60 abm=62 base=64).
    private const int CharacterNameOffset = 8;
    private const int MoneyOffset = 36;
    private const int FlagsOffset = 40;
    private const int NKillsOffset = 42;
    private const int NEjectionsOffset = 44;
    private const int NDeathsOffset = 46;
    private const int FExperienceOffset = 48;
    private const int ISideOffset = 52;
    private const int ShipIdOffset = 54;
    private const int RankIdOffset = 56;
    private const int DtidDroneOffset = 58;
    private const int PilotTypeOffset = 60;
    private const int AbmOrdersOffset = 62;
    private const int BaseObjectIdOffset = 64;

    /// <summary>Width of <c>char c_cbName</c> (<c>_Utility/Utility.h:60,64</c>: 24+1 = 25).</summary>
    public const int NameBufferSize = 25;

    /// <summary><c>c_ptPlayer</c> — <c>igc.h:685</c>; same pilot type the ship EXPORT uses.</summary>
    public const byte PilotTypePlayer = 10;

    // --- Fields the join/side path actually consumes ----------------------
    /// <summary><c>CharacterName</c> — the peer's real identity, shown in debug output only.</summary>
    public string CharacterName = "";

    /// <summary><c>iSide</c> — THE field: the real side the client ship is assigned to. Must be the peer ship's SideId.</summary>
    public short ISide;

    /// <summary><c>shipID</c> — must equal the peer's assigned ship id (the handler keys on it).</summary>
    public short ShipId;

    /// <summary><c>pilotType</c> — c_ptPlayer (10). Read by <c>CshipIGC::ReInitialize</c> (<c>shipIGC.cpp:105-118</c>) alongside <c>baseObjectID</c> even for the already-exported ship (P78) — must stay c_ptPlayer with <see cref="BaseObjectId"/> = NA, the combination that takes the early-out branch instead of the builder/layer branches or the <c>assert(false)</c> else.</summary>
    public byte PilotType = PilotTypePlayer;

    /// <summary>Edge-negotiated by the server; the rest of the fixed region is zeroed (unused by this flow).</summary>
    public int Money;            // zero → satisfies AddPlayerToSide's GetMoney()==0 assert (clintlib.cpp:4109)

    // Remaining fixed-region fields captured faithfully on decode for
    // round-trip completeness. HUD-12 now FILLS nKills/nDeaths from the live
    // PlayerScore (native FedSrv.CPP:2085-2086 sends GetKills()/GetDeaths()),
    // so the client's MissionKills()/MissionDeaths() equivalents
    // (clintlib.h:346-347) have a real initial value; the native client then
    // keeps them current from KILL_SHIP (appmsg.cpp:2766-2773,2807-2810).
    public short NKills, NEjections, NDeaths;
    public float FExperience;

    /// <summary>P774 — one <c>rgPersistPlayerScores</c> entry
    /// (<c>PersistPlayerScoreObject</c>, igc.h:5421-5497): 12 bytes under
    /// natural alignment — float totalScore @0, float fCombatRating @4,
    /// short rank @8, short civID @10. The port carries RANK only (score and
    /// combat rating are 0: no stats DB); native fills it at logon
    /// (FedSrv.CPP:952-957) and sends it with every PLAYERINFO
    /// (:2060-2077).</summary>
    public readonly record struct PersistPlayerScore(float Score, float CombatRating, short Rank, short CivId)
    {
        /// <summary>Wire width of one entry, igc.h:5486-5494.</summary>
        public const int Size = 12;
    }

    /// <summary>P774 — the persist list carried by this message. Native sends
    /// an NA entry plus one entry per civ (FedSrv.CPP:952-957); the port sends
    /// the single NA entry (see the class doc). Empty = no rank carried
    /// (the client reads 0, clintlib.cpp:1042-1051).</summary>
    public PersistPlayerScore[] PersistScores = [];

    /// <summary>Native <c>PlayerInfo::AddKill()</c> (<c>clintlib.h:346-347</c>;
    /// incremented from KILL_SHIP, <c>appmsg.cpp:2807-2810</c>). Saturates at
    /// <see cref="short.MaxValue"/> rather than wrapping.</summary>
    public void AddKill() => NKills = NKills == short.MaxValue ? NKills : (short)(NKills + 1);

    /// <summary>Native <c>PlayerInfo::AddDeath()</c> (<c>clintlib.h:346-347</c>;
    /// incremented from KILL_SHIP's <c>bDeathCredit</c> branch,
    /// <c>appmsg.cpp:2766-2773</c>). Saturates at <see cref="short.MaxValue"/>.</summary>
    public void AddDeath() => NDeaths = NDeaths == short.MaxValue ? NDeaths : (short)(NDeaths + 1);

    /// <summary>P774 — native <c>PlayerInfo::AddEjection()</c>
    /// (<c>clintlib.h:353-355</c>; incremented from KILL_SHIP's non-death branch,
    /// <c>appmsg.cpp:2770-2771</c>). Same saturating style as
    /// <see cref="AddKill"/>.</summary>
    public void AddEjection() => NEjections = NEjections == short.MaxValue ? NEjections : (short)(NEjections + 1);

    /// <summary>P774 — native <c>IsHuman()</c>: dtidDrone == NA
    /// (<c>clintlib.h:343</c>). A drone's PLAYERINFO carries the drone type id
    /// instead (FedSrv.CPP:2088-2090).</summary>
    public bool IsHuman => DtidDrone == NA;

    /// <summary>P774 — the rank the client reads: the <c>Rank</c> of the
    /// persist entry whose <c>CivId == NA</c> (<c>clintlib.cpp:1042-1051</c>,
    /// <c>teampane.cpp:1866</c> GetPersistScore(NA)); 0 when no NA entry was
    /// carried. Native sends rank 0 in rankID — "//NYI", FedSrv.CPP:2112 —
    /// so the persist entry is the ONLY rank carrier.</summary>
    public short Rank
    {
        get
        {
            foreach (var e in PersistScores)
                if (e.CivId == NA) return e.Rank;
            return 0;
        }
    }

    public short RankId;
    public short DtidDrone = NA;
    public short AbmOrders;

    /// <summary><c>baseObjectID</c> — must be <see cref="NA"/> (P78: see the class doc). Not "unused"; <c>CshipIGC::ReInitialize</c> reads it every time regardless of pilotType.</summary>
    public short BaseObjectId = NA;

    /// <summary>Native <c>NA</c> sentinel (-1) — matches <c>ExportShip.cs</c>/<c>NewShipInstance.cs</c>'s own baseObjectID convention for "not docked/no base".</summary>
    public const short NA = -1;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (PersistScores.Length > 20)
            throw new ArgumentException($"PLAYERINFO persist list exceeds cCivsMax=20 (FedSrv.CPP:2058), got {PersistScores.Length}");
        var buf = new byte[MessageSize + PersistScores.Length * PersistPlayerScore.Size];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        // rgPersistPlayerScores var item: ib points past the fixed region (the
        // blob's absolute offset); cb = 12 bytes per entry (P774 — native
        // clintlib.cpp:1019-1040 computes the count from cb).
        w.WriteU16((ushort)MessageSize);   // ib
        w.WriteU16((ushort)(PersistScores.Length * PersistPlayerScore.Size));   // cb

        // ---- fixed region (offset 8) ----
        WriteFixedName(ref w, CharacterName);        // char[25] @8
        w.Advance(MoneyOffset - w.Position);         // pad 33..35 → money @36
        w.WriteI32(Money);                           // int money @36
        w.Advance(FlagsOffset - w.Position);         // → flags @40
        w.WriteByte(0);                              // 3×bool:1 = 0
        w.Advance(NKillsOffset - w.Position);        // → nKills @42
        w.WriteI16(NKills); w.WriteI16(NEjections); w.WriteI16(NDeaths); // nKills, nEjections, nDeaths @42/44/46
        // HUD-20 — native fills fExperience from the ship's IGC experience
        // (FedSrv.CPP:2084); the target pane's kill bonus reads it back
        // (appmsg.cpp:2280 -> consoledata.cpp:831-843).
        w.WriteI32(BitConverter.SingleToInt32Bits(FExperience)); // fExperience @48
        w.WriteI16(ISide);                           // iSide @52  ← THE FIELD
        w.WriteI16(ShipId);                          // shipID @54
        w.WriteI16(0);                               // rankID @56
        // HUD-20 — native dtidDrone is NA for a human and the drone type id for
        // a drone (FedSrv.CPP:2088-2091); the client tells them apart with it.
        w.WriteI16(DtidDrone);                       // dtidDrone @58
        w.WriteByte(PilotType);                      // pilotType @60
        w.Advance(AbmOrdersOffset - w.Position);     // → abmOrders @62
        w.WriteI16(0);                               // abmOrders @62
        w.WriteI16(NA);                              // baseObjectID @64 (P78: must be NA, not 0 -- see class doc)
        w.Advance(8 + FixedSize - w.Position);       // trailing pad 66..67 → fixed region = 68 bytes

        if (w.Position - 8 != FixedSize)
            throw new InvalidOperationException($"PLAYERINFO internal layout drift: fixed region ended at {w.Position - 8} != {FixedSize}");

        // ---- persist entries trail the fixed region (P774) ----
        foreach (var ps in PersistScores)
        {
            w.WriteU32(BitConverter.SingleToUInt32Bits(ps.Score));        // m_totalScore
            w.WriteU32(BitConverter.SingleToUInt32Bits(ps.CombatRating)); // m_fCombatRating
            w.WriteI16(ps.Rank);                                          // m_rank (igc.h:843)
            w.WriteI16(ps.CivId);                                         // m_civID (igc.h:498)
        }

        w.WriteU16At(0, (ushort)buf.Length);
        return buf;
    }

    public static PlayerInfo Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < MessageSize)
            throw new ArgumentException($"PLAYERINFO needs at least {MessageSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PLAYERINFO fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        // P774 — rgPersistPlayerScores: 0..20 entries of 12 bytes each
        // (cCivsMax = 20, FedSrv.CPP:2058), starting right after the fixed
        // region and filling the buffer exactly.
        if (vi.Ib != MessageSize || vi.Cb % PersistPlayerScore.Size != 0 || MessageSize + vi.Cb != buf.Length
            || vi.Cb / PersistPlayerScore.Size > 20)
            throw new ArgumentException($"rgPersistPlayerScores invalid: ib={vi.Ib} cb={vi.Cb} len={buf.Length}");
        int entryCount = vi.Cb / PersistPlayerScore.Size;
        var persist = new PersistPlayerScore[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int off = MessageSize + i * PersistPlayerScore.Size;
            persist[i] = new PersistPlayerScore(
                Score: BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(off))),
                CombatRating: BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(off + 4))),
                Rank: BitConverter.ToInt16(buf.Slice(off + 8)),
                CivId: BitConverter.ToInt16(buf.Slice(off + 10)));
        }

        string name = ReadFixedName(buf, CharacterNameOffset);
        int money = BitConverter.ToInt32(buf.Slice(MoneyOffset));
        short kills = BitConverter.ToInt16(buf.Slice(NKillsOffset));
        short ejections = BitConverter.ToInt16(buf.Slice(NEjectionsOffset));
        short deaths = BitConverter.ToInt16(buf.Slice(NDeathsOffset));
        float experience = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(buf.Slice(FExperienceOffset)));
        short iSide = BitConverter.ToInt16(buf.Slice(ISideOffset));
        short shipId = BitConverter.ToInt16(buf.Slice(ShipIdOffset));
        short rankId = BitConverter.ToInt16(buf.Slice(RankIdOffset));
        short dtidDrone = BitConverter.ToInt16(buf.Slice(DtidDroneOffset));
        byte pilotType = buf[PilotTypeOffset];
        short abmOrders = BitConverter.ToInt16(buf.Slice(AbmOrdersOffset));
        short baseObjectId = BitConverter.ToInt16(buf.Slice(BaseObjectIdOffset));

        return new PlayerInfo
        {
            CharacterName = name,
            Money = money,
            ISide = iSide,
            ShipId = shipId,
            PilotType = pilotType,
            RankId = rankId,
            DtidDrone = dtidDrone,
            AbmOrders = abmOrders,
            BaseObjectId = baseObjectId,
            NKills = kills, NEjections = ejections, NDeaths = deaths, FExperience = experience,
            PersistScores = persist,
        };
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static void WriteFixedName(ref WireWriter w, string s)
    {
        int n = Math.Min(s.Length, NameBufferSize - 1);
        if (n > 0) w.WriteBytes(Latin1.GetBytes(s[..n]));
        w.WriteByte(0);
        w.WriteZeros(NameBufferSize - n - 1);
    }

    private static string ReadFixedName(ReadOnlySpan<byte> buf, int off)
    {
        var raw = buf.Slice(off, NameBufferSize);
        int n = NameBufferSize;
        while (n > 0 && raw[n - 1] == 0) n--;
        return Latin1.GetString(raw[..n]);
    }
}
