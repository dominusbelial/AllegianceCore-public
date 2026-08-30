using Allegiance.Core.Wire;
using System.Text;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="ExportShip"/>
/// (FM_S_EXPORT 26), ported from <c>Messages.h:240-243</c> +
/// <c>DataShipIGC</c> (§OT_ship(0)) — the same DataShipIGC payload as
/// <see cref="NewShipInstance"/> (fmid 31) plus a separately-typed
/// <c>objecttype</c> int32. Byte-exactness is pinned against a hand-composed
/// buffer derived from the task's field table — not from the encoder —
/// exactly as <c>NewShipInstanceWireTests</c> / <c>SetCluster</c>'s tests do.
/// nParts=0 everywhere (no PartData[] tail) is asserted explicitly: that is
/// the deliberate simplification, and the round-trip decode rejects any
/// nParts!=0 / partsOffset!=52 as a layout drift, and any objecttype!=0 as a
/// type we do not emit. Total = 12 + 52 = 64 bytes.
/// </summary>
public class ExportShipWireTests
{
    [Fact]
    public void ExportShip_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)26, ExportShip.Fmid);
        Assert.Equal(0, ExportShip.OtShip);          // OT_ship = AGC_Ship = 0
        Assert.Equal(52, ExportShip.DataShipSize);   // DataShipIGC with nParts=0
        Assert.Equal(64, ExportShip.FixedSize);      // 4 hdr + 4 varitem + 4 objecttype + 52 blob
        Assert.Equal((byte)10, ExportShip.PilotTypePlayer); // c_ptPlayer
    }

    [Fact]
    public void ExportShip_RoundTrips_ThroughWire()
    {
        var msg = new ExportShip
        {
            ObjectType = 0,
            CharacterName = "Alice",
            HullId = 905,
            ShipId = 778,
            SideId = 516,
            PilotType = 10,
        };
        var d = ExportShip.Decode(msg.Encode());
        Assert.Equal(0, d.ObjectType);
        Assert.Equal("Alice", d.CharacterName);
        Assert.Equal(905, d.HullId);
        Assert.Equal(778, d.ShipId);
        Assert.Equal(516, d.SideId);
        Assert.Equal((byte)10, d.PilotType);
    }

    [Fact]
    public void ExportShip_EmitsExactDeclaredBytes()
    {
        // "Alice" (5) → char[25]: "Alice" + NUL + 19 zero bytes
        var msg = new ExportShip
        {
            ObjectType = 0,
            CharacterName = "Alice",
            HullId = 904,
            ShipId = 777,
            SideId = 517,
            PilotType = 10,
        };
        var got = msg.Encode();

        var exp = new byte[64];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(64);   // cbmsg
        u16(26);   // fmid
        u16(12);   // ib exportData
        u16(52);   // cb exportData
        u32(0);    // objecttype = OT_ship (int32, 4-byte)
        u32(0);    // dwPrivate
        i16(52);   // partsOffset = sizeof(DataShipIGC)
        i16(904);  // hullID
        i16(777);  // shipID
        foreach (var b in Encoding.Latin1.GetBytes("Alice")) exp[p++] = b;
        exp[p++] = 0;                       // name NUL
        pad(19);                            // name zero-pad to 25 total
        exp[p++] = 0;                       // nParts = 0
        i16(517);  // sideID
        exp[p++] = 10;                      // pilotType
        exp[p++] = 0;                       // pad
        i16(0);    // abmOrders
        i16(-1);   // baseObjectID (NA)
        i16(0);    // nDeaths
        i16(0);    // nEjections
        i16(0);    // nKills
        i16(0);    // pad
        Assert.Equal(64, p);                // header(4)+varitem(4)+objecttype(4)+DataShipIGC(52)

        Assert.Equal(exp, got);
    }

    [Fact]
    public void ExportShip_NameTruncatesTo24AndNullTerminates()
    {
        var msg = new ExportShip { CharacterName = new string('X', 30), HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 };
        var d = ExportShip.Decode(msg.Encode());
        Assert.Equal(new string('X', 24), d.CharacterName);
        Assert.Equal('X', d.CharacterName[23]);
        Assert.Equal(1, d.HullId);
    }

    [Fact]
    public void ExportShip_EmptyName_IsAllZerosAndPreserved()
    {
        var msg = new ExportShip { CharacterName = "", HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 };
        var got = msg.Encode();
        Assert.Equal(64, got.Length);
        // name field = bytes 22..46 (after header+varitem+objecttype+dwPrivate+partsOffset+hull+ship)
        for (int i = 22; i <= 46; i++) Assert.Equal(0, got[i]); // NUL + zero pad
        Assert.Equal("", ExportShip.Decode(got).CharacterName);
    }

    [Fact]
    public void ExportShip_RejectsWrongFmid_NonZeroObjectType_AndNonZeroNParts()
    {
        var bytes = new ExportShip { CharacterName = "x", HullId = 1, ShipId = 1, SideId = 1, PilotType = 10 }.Encode();

        // wrong fmid
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => ExportShip.Decode(badFmid));

        // objecttype sits at message offset 8 (4 bytes). Set it to 2 → not OT_ship → reject.
        var badOt = (byte[])bytes.Clone();
        badOt[8] = 2;
        Assert.Throws<ArgumentException>(() => ExportShip.Decode(badOt));

        // nParts sits at blob offset 12 + 35 = message offset 47. Flip it to 1 → reject.
        var withParts = (byte[])bytes.Clone();
        withParts[47] = 1;
        Assert.Throws<ArgumentException>(() => ExportShip.Decode(withParts));
    }
}
