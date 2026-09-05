using System.Text;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="PlayerInfo"/>
/// (FM_S_PLAYERINFO 10), the message that carries a real side to the client's
/// ship. Offsets are the P31 VM-probe-confirmed layout (MSVC default packing,
/// toolset v145 — verbatim probe "P31 offsets cchName=8 money=36 nKills=42
/// nEject=44 nDeaths=46 exp=48 iSide=52 shipID=54 rank=56 drone=58 pilotType=60
/// abm=62 base=64"; "sizeof(FMD_S_PLAYERINFO)=68"): header(4) + var item(4) +
/// fixed region of 60 bytes (CharacterName@8 … baseObjectID@64, trailing pad
/// 66..67). Whole message = 68 bytes. Name buffer = c_cbName = 25.
/// pilotType c_ptPlayer = 10.
/// </summary>
public class PlayerInfoWireTests
{
    [Fact]
    public void PlayerInfo_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)10, PlayerInfo.Fmid);
        Assert.Equal(60, PlayerInfo.FixedSize);            // region after header+var-item
        Assert.Equal(68, PlayerInfo.MessageSize);          // sizeof(FMD_S_PLAYERINFO) = 4+4+60
        Assert.Equal(25, PlayerInfo.NameBufferSize);       // c_cbName
        Assert.Equal((byte)10, PlayerInfo.PilotTypePlayer);// c_ptPlayer
    }

    [Fact]
    public void PlayerInfo_RoundTrips_ThroughWire()
    {
        var msg = new PlayerInfo
        {
            CharacterName = "t14Bot",
            Money = 0,
            ISide = 0,
            ShipId = 8103,
            PilotType = 10,
        };
        var d = PlayerInfo.Decode(msg.Encode());
        Assert.Equal("t14Bot", d.CharacterName);
        Assert.Equal(0, d.ISide);
        Assert.Equal(8103, d.ShipId);
        Assert.Equal((byte)10, d.PilotType);
        Assert.Equal(0, d.Money);
        Assert.Equal(PlayerInfo.NA, d.BaseObjectId); // P78: must be NA (-1), not 0 -- shipIGC.cpp:105-118 asserts false otherwise
    }

    [Fact]
    public void PlayerInfo_EmitsExactDeclaredBytes_ForASideAssignment()
    {
        // t14Bot → char[25]: "t14Bot" (6) + NUL + 18 zero bytes.
        // iSide=0, shipID=8103 (0x1FA7), pilotType=10, everything else zero.
        var msg = new PlayerInfo { CharacterName = "t14Bot", ISide = 0, ShipId = 8103 };
        var got = msg.Encode();

        var exp = new byte[68];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void i32(int v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(68);                 // cbmsg = 68
        u16(10);                 // fmid
        u16(68);                 // ib rgPersistPlayerScores (past fixed region; meaningless at cb=0)
        u16(0);                  // cb rgPersistPlayerScores = 0 (absent)
        // fixed region @8
        foreach (var b in Encoding.Latin1.GetBytes("t14Bot")) exp[p++] = b;
        exp[p++] = 0;                       // NUL
        pad(18);                            // name zero-pad → 25 total (8..32)
        pad(3);                             // 33..35 → money @36
        i32(0);                             // money
        exp[p++] = 0;                       // flags byte @40 (fReady/fTeamLeader/fMissionOwner = 0)
        pad(1);                             // → nKills @42
        i16(0); i16(0); i16(0);             // nKills @42, nEjections @44, nDeaths @46
        pad(4);                             // fExperience @48 (float 0)
        i16(0);                             // iSide @52
        i16(8103);                          // shipID @54 (0x1FA7 → A7 1F)
        i16(0);                             // rankID @56
        i16(0);                             // dtidDrone @58
        exp[p++] = 10;                      // pilotType @60
        pad(1);                             // → abmOrders @62
        i16(0);                             // abmOrders @62
        i16(-1);                            // baseObjectID @64 = NA (P78: not 0 -- see PlayerInfo's class doc)
        pad(2);                             // trailing pad 66..67 → struct size 68
        Assert.Equal(68, p);                // 4 + 4 + 60

        Assert.Equal(exp, got);
    }

    [Fact]
    public void PlayerInfo_NonZeroSide_SurvivesRoundTrip()
    {
        var msg = new PlayerInfo { CharacterName = "x", ISide = 753, ShipId = 8088 };
        var d = PlayerInfo.Decode(msg.Encode());
        Assert.Equal(753, d.ISide);
        Assert.Equal(8088, d.ShipId);
    }

    [Fact]
    public void PlayerInfo_EmptyName_IsAllZerosAndPreserved()
    {
        var msg = new PlayerInfo { CharacterName = "", ShipId = 8066 };
        var got = msg.Encode();
        Assert.Equal(68, got.Length);
        for (int i = 8; i < 33; i++) Assert.Equal(0, got[i]);  // NUL + zero pad
        Assert.Equal("", PlayerInfo.Decode(got).CharacterName);
    }

    [Fact]
    public void PlayerInfo_LongName_TruncatesTo24AndNullTerminates()
    {
        var msg = new PlayerInfo { CharacterName = new string('Q', 40), ISide = 1, ShipId = 8077 };
        var d = PlayerInfo.Decode(msg.Encode());
        Assert.Equal(new string('Q', 24), d.CharacterName);
        Assert.Equal('Q', d.CharacterName[23]);
        Assert.Equal(1, d.ISide);
    }

    [Fact]
    public void PlayerInfo_ZerosEveryCellExceptThePopulatedOnes()
    {
        var msg = new PlayerInfo { CharacterName = "Bob", ISide = 911, ShipId = 8022 };
        var b = msg.Encode();
        var nameStart = 8;             // CharacterName[25]
        var nameEnd = nameStart + 25;
        for (int i = 8; i < b.Length; i++)   // after header+var-item (ib legitimately holds 68)
        {
            if (i >= nameStart && i < nameEnd) continue;          // name (nonzero text + NUL + pad)
            if (i >= 52 && i < 56) continue;                      // iSide @52, shipID @54
            if (i >= 60 && i < 61) continue;                      // pilotType @60
            if (i >= 64 && i < 66) continue;                      // baseObjectID @64 = NA (-1, P78), not zero
            Assert.True(b[i] == 0, $"offset {i} non-zero ({b[i]:X2})");
        }
    }

    [Fact]
    public void PlayerInfo_RejectsWrongFmid_NonZeroScoreBlob_AndWrongLength()
    {
        var bytes = new PlayerInfo { CharacterName = "c", ISide = 0, ShipId = 8011 }.Encode();

        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 95;
        Assert.Throws<ArgumentException>(() => PlayerInfo.Decode(badFmid));

        Assert.Throws<ArgumentException>(() => PlayerInfo.Decode(bytes.AsSpan(0, 67)));  // short
        Assert.Throws<ArgumentException>(() => PlayerInfo.Decode(new byte[70]));         // long (length gate)

        // rgPersistPlayerScores cb sits at message offset 6 (VarItem cb = [4..5]=ib,[6..7]=cb).
        var withScores = (byte[])bytes.Clone();
        withScores[6] = 1;
        Assert.Throws<ArgumentException>(() => PlayerInfo.Decode(withScores));
    }
}
