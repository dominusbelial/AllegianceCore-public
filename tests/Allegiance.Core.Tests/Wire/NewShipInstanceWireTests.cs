using Allegiance.Core.Wire;
using System.Text;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="NewShipInstance"/>
/// (FM_CS_NEW_SHIP_INSTANCE 31), ported from <c>Messages.h:262-264</c> +
/// <c>DataShipIGC</c> (§OT_ship(0), <c>allegiance-igc-file-format</c>; offsets
/// corrected). Byte-exactness is pinned against a hand-composed buffer derived
/// from the task's field table — not from the encoder — exactly as
/// <c>GameMessageRoundTripTests.SetCluster_EmitsExactDeclaredBytes</c> does.
/// nParts=0 everywhere (no PartData[] tail) is asserted explicitly: that is
/// the deliberate simplification, and the round-trip decode rejects any
/// nParts!=0 / partsOffset!=52 as a layout drift.
/// </summary>
public class NewShipInstanceWireTests
{
    [Fact]
    public void NewShipInstance_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)31, NewShipInstance.Fmid);
        Assert.Equal(52, NewShipInstance.DataShipSize);   // DataShipIGC with nParts=0
        Assert.Equal(60, NewShipInstance.FixedSize);      // 4 hdr + 4 varitem + 52 blob
        Assert.Equal((byte)10, NewShipInstance.PilotTypePlayer); // c_ptPlayer
    }

    [Fact]
    public void NewShipInstance_RoundTrips_ThroughWire()
    {
        var msg = new NewShipInstance
        {
            CharacterName = "Alice",
            HullId = 902,
            ShipId = 771,
            SideId = 514,
            PilotType = 10,
        };
        var d = NewShipInstance.Decode(msg.Encode());
        Assert.Equal("Alice", d.CharacterName);
        Assert.Equal(902, d.HullId);
        Assert.Equal(771, d.ShipId);
        Assert.Equal(514, d.SideId);
        Assert.Equal((byte)10, d.PilotType);
    }

    [Fact]
    public void NewShipInstance_EmitsExactDeclaredBytes()
    {
        // "Alice" (5) → char[25]: "Alice" + NUL + 19 zero bytes
        var msg = new NewShipInstance
        {
            CharacterName = "Alice",
            HullId = 901,
            ShipId = 770,
            SideId = 513,
            PilotType = 10,
        };
        var got = msg.Encode();

        var exp = new byte[60];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(60);   // cbmsg
        u16(31);   // fmid
        u16(8);    // ib exportData
        u16(52);   // cb exportData
        u32(0);        // dwPrivate
        i16(52);       // partsOffset = sizeof(DataShipIGC)
        i16(901);      // hullID
        i16(770);      // shipID
        foreach (var b in Encoding.Latin1.GetBytes("Alice")) exp[p++] = b;
        exp[p++] = 0;                       // name NUL
        pad(19);                            // name zero-pad to 25 total
        exp[p++] = 0;                       // nParts = 0
        i16(513);      // sideID
        exp[p++] = 10;                      // pilotType
        exp[p++] = 0;                       // pad
        i16(0);        // abmOrders
        i16(-1);       // baseObjectID (NA)
        i16(0);        // nDeaths
        i16(0);        // nEjections
        i16(0);        // nKills
        i16(0);        // pad
        Assert.Equal(60, p);                // header(4)+varitem(4)+DataShipIGC(52)

        Assert.Equal(exp, got);
    }

    [Fact]
    public void NewShipInstance_NameTruncatesTo24AndNullTerminates()
    {
        // 30-char name must be truncated to 24 chars + a single NUL; the rest zero.
        var msg = new NewShipInstance { CharacterName = new string('X', 30), HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 };
        var d = NewShipInstance.Decode(msg.Encode());
        Assert.Equal(new string('X', 24), d.CharacterName);
        Assert.Equal('X', d.CharacterName[23]);
        Assert.Equal(1, d.HullId);
    }

    [Fact]
    public void NewShipInstance_EmptyName_IsAllZerosAndPreserved()
    {
        var msg = new NewShipInstance { CharacterName = "", HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 };
        var got = msg.Encode();
        Assert.Equal(60, got.Length);
        // name field = bytes 18..42 (after header+varitem+dwPrivate+partsOffset+hull+ship)
        for (int i = 18; i <= 42; i++) Assert.Equal(0, got[i]); // NUL + zero pad
        Assert.Equal("", NewShipInstance.Decode(got).CharacterName);
    }

    [Fact]
    public void NewShipInstance_RejectsWrongFmid_AndNonZeroNParts()
    {
        var bytes = new NewShipInstance { CharacterName = "x", HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 }.Encode();

        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => NewShipInstance.Decode(badFmid));

        // nParts sits at message offset 8 + 35 = 43. Flip it to 1 → decode must reject.
        var withParts = (byte[])bytes.Clone();
        withParts[43] = 1;
        Assert.Throws<ArgumentException>(() => NewShipInstance.Decode(withParts));
    }
}
