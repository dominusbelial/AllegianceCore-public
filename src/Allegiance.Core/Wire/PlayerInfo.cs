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
/// (<c>rgPersistPlayerScores</c>) followed by a fixed region. Sending zero
/// player scores keeps the blob absent (cb=0) — real score history is out of
/// scope and nothing in the join/side path reads it (<c>PlayerInfo::Set</c>,
/// <c>clintlib.cpp:1023-1039</c>, computes the count from <c>cb</c> and skips
/// the copy when it is 0). Fixed-region offsets, absolute from message byte 0:
/// <code>
///   offset 0: ushort cbmsg (total size)
///   offset 2: ushort fmid  = 10
///   offset 4: VarItem (ib, cb) for rgPersistPlayerScores — cb=0 (absent)
///   offset 8: char  CharacterName[25]   ("<name>\0"+pad)
///   offset 33: 3 pad bytes to the 4-aligned int
///   offset 36: int   money = 0
///   offset 40: byte  3×bool:1 bits (fReady/fTeamLeader/fMissionOwner) = 0
///   offset 41: 1 pad byte to the 2-aligned short
///   offset 42: short nKills = 0
///   offset 44: short nEjections = 0
///   offset 46: short nDeaths = 0
///   offset 48: float fExperience = 0
///   offset 52: short iSide          ← THE FIELD this message exists to carry
///   offset 54: short shipID         (must match the peer's assigned id)
///   offset 56: short rankID = 0
///   offset 58: short dtidDrone = 0  (drone piloting out of scope)
///   offset 60: uchar pilotType = 10 (c_ptPlayer; harmless since the ship is
///                                    reused from the earlier EXPORT — see below)
///   offset 61: 1 pad byte to the 2-aligned short
///   offset 62: short abmOrders = 0
///   offset 64: short baseObjectID = 0
///   offset 66: 2 pad bytes (trailing, to the struct's 4-byte alignment)
///   = 68-byte fixed region; total message = 4 (hdr) + 4 (varitem) + 68 = 76 B.
/// </code>
///
/// Field rationale (grep-verified, not guessed):
/// <c>iSide</c> is the whole point — the real side (ship.SideId). <c>shipID</c>
/// must equal the assigned id: the handler uses it for FindPlayerLink, the
/// DataShipIGC, GetShip and AddPlayerToSide (<c>appmsg.cpp:2226/2250/2276/
/// 2288</c>). <c>CharacterName</c> is only displayed. <c>money</c> must be 0 to
/// satisfy <c>AddPlayerToSide</c>'s <c>assert(GetMoney()==0)</c>
/// (<c>clintlib.cpp:4109</c>). Everything else is unused by the join/side path:
/// stats (nKills/nEjections/nDeaths/fExperience), bitflags
/// (<c>SetReady</c> is commented out at <c>:2289</c>), rankID/dtidDrone/
/// abmOrders/baseObjectID. <b>Why zero-side is still right:</b> this codec is
/// intentionally ordered <em>after</em> the ship EXPORT in Server.cs, so the
/// handler's <c>CreateObject(OT_ship,&amp;ds,...)</c> at <c>:2264</c> fails
/// (id exists) and it takes the <c>else</c> branch — <c>GetShip(shipID)</c>
/// (<c>:2276</c>) — reusing the real, hulled ship from EXPORT rather than the
/// hullID=NA dummy the handler would otherwise create.
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

    /// <summary>Width of the whole message = sizeof(FMD_S_PLAYERINFO), MSVC probe-confirmed 68 bytes (4 header + 4 var item + 60 bytes of fixed fields/padding).</summary>
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

    /// <summary><c>pilotType</c> — c_ptPlayer (10). Consumed only if the handler's CreateObject succeeds, which it does not (the ship already exists from EXPORT), but kept faithful/harmless.</summary>
    public byte PilotType = PilotTypePlayer;

    /// <summary>Edge-negotiated by the server; the rest of the fixed region is zeroed (unused by this flow).</summary>
    public int Money;            // zero → satisfies AddPlayerToSide's GetMoney()==0 assert (clintlib.cpp:4109)

    // Remaining fixed-region fields captured faithfully on decode for
    // round-trip completeness. Not populated by the server (all zeroed on
    // encode); the client's join/side path does not consume them.
    public short NKills, NEjections, NDeaths;
    public float FExperience;
    public short RankId;
    public short DtidDrone;
    public short AbmOrders;
    public short BaseObjectId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[MessageSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        // rgPersistPlayerScores var item: absent (cb=0), ib points past the
        // fixed region (meaningless when cb=0 but harmless).
        w.WriteU16((ushort)MessageSize);   // ib
        w.WriteU16(0);                     // cb = 0 → empty score list

        // ---- fixed region (offset 8) ----
        WriteFixedName(ref w, CharacterName);        // char[25] @8
        w.Advance(MoneyOffset - w.Position);         // pad 33..35 → money @36
        w.WriteI32(Money);                           // int money @36
        w.Advance(FlagsOffset - w.Position);         // → flags @40
        w.WriteByte(0);                              // 3×bool:1 = 0
        w.Advance(NKillsOffset - w.Position);        // → nKills @42
        w.WriteI16(0); w.WriteI16(0); w.WriteI16(0); // nKills, nEjections, nDeaths @42/44/46
        w.WriteI32(BitConverter.SingleToInt32Bits(0f)); // fExperience @48 (float 0)
        w.WriteI16(ISide);                           // iSide @52  ← THE FIELD
        w.WriteI16(ShipId);                          // shipID @54
        w.WriteI16(0);                               // rankID @56
        w.WriteI16(0);                               // dtidDrone @58
        w.WriteByte(PilotType);                      // pilotType @60
        w.Advance(AbmOrdersOffset - w.Position);     // → abmOrders @62
        w.WriteI16(0);                               // abmOrders @62
        w.WriteI16(0);                               // baseObjectID @64
        w.Advance(8 + FixedSize - w.Position);       // trailing pad 66..67 → fixed region = 68 bytes

        if (w.Position - 8 != FixedSize)
            throw new InvalidOperationException($"PLAYERINFO internal layout drift: fixed region ended at {w.Position - 8} != {FixedSize}");
        w.WriteU16At(0, (ushort)MessageSize);
        return buf;
    }

    public static PlayerInfo Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length != MessageSize)
            throw new ArgumentException($"PLAYERINFO needs exactly {MessageSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PLAYERINFO fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != MessageSize) throw new ArgumentException($"cbmsg={hdr.CbMsg} != fixed {MessageSize}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb != 0) throw new ArgumentException($"rgPersistPlayerScores cb={vi.Cb} != 0 (score history not supported; re-encode empty)");

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
