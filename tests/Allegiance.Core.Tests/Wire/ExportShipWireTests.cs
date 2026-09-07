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
/// The <c>nParts=0</c> case (no <c>PartData[]</c> tail) stays byte-for-byte
/// identical to before the tail was added — asserted explicitly below — and
/// remains the default (<c>Parts</c> is empty unless set). A non-empty
/// <c>Parts</c> appends real <see cref="PartData"/> entries after the fixed
/// 52-byte body; <c>partsOffset</c> stays the constant 52 either way (it
/// locates the tail, not its length). The round-trip decode rejects a
/// mismatched <c>nParts</c>/<c>cb</c> pair as a layout drift, and any
/// <c>objecttype!=0</c> as a type this codec does not emit.
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
    public void ExportShip_RejectsWrongFmid_NonZeroObjectType_AndMismatchedNParts()
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

        // nParts sits at blob offset 12 + 35 = message offset 47. Flip it to 1
        // WITHOUT appending a PartData entry or growing cb → the nParts field
        // now disagrees with the tail length cb implies (still 0) → reject.
        var withParts = (byte[])bytes.Clone();
        withParts[47] = 1;
        Assert.Throws<ArgumentException>(() => ExportShip.Decode(withParts));
    }

    [Fact]
    public void ExportShip_WithParts_RoundTripsAndEmitsExactBytes()
    {
        // Extends ExportShip_EmitsExactDeclaredBytes's programmatic byte
        // builder with a real PartData[] tail appended after the fixed 52-byte
        // body — hand-computing the hex directly (BuyLoadoutAckWireTests'
        // style) is error-prone at this message's size, so this test builds
        // its expectation the same declarative way the encoder does, then
        // cross-checks the total length independently.
        var msg = new ExportShip
        {
            ObjectType = 0,
            CharacterName = "Bob",
            HullId = 610,
            ShipId = 620,
            SideId = 1,
            PilotType = 10,
            Parts =
            [
                new PartData(PartId: 41, Amount: 20, MountId: 0),
                new PartData(PartId: 42, Amount: 5, MountId: 1),
            ],
        };
        var got = msg.Encode();

        var exp = new byte[64 + 2 * PartData.Size];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16((ushort)exp.Length); // cbmsg
        u16(26);   // fmid
        u16(12);   // ib exportData
        u16((ushort)(52 + 2 * PartData.Size)); // cb exportData
        u32(0);    // objecttype = OT_ship
        u32(0);    // dwPrivate
        i16(52);   // partsOffset — constant regardless of nParts
        i16(610);  // hullID
        i16(620);  // shipID
        foreach (var b in Encoding.Latin1.GetBytes("Bob")) exp[p++] = b;
        exp[p++] = 0;                       // name NUL
        pad(21);                            // name zero-pad to 25 total (3 + 1 + 21 = 25)
        exp[p++] = 2;                       // nParts = 2
        i16(1);    // sideID
        exp[p++] = 10;                      // pilotType
        exp[p++] = 0;                       // pad
        i16(0);    // abmOrders
        i16(-1);   // baseObjectID (NA)
        i16(0);    // nDeaths
        i16(0);    // nEjections
        i16(0);    // nKills
        i16(0);    // pad
        i16(41); i16(20); exp[p++] = 0;     // PartData[0]
        i16(42); i16(5); exp[p++] = 1;      // PartData[1]
        Assert.Equal(exp.Length, p);

        Assert.Equal(exp, got);

        var d = ExportShip.Decode(got);
        Assert.Equal(2, d.Parts.Count);
        Assert.Equal(new PartData(41, 20, 0), d.Parts[0]);
        Assert.Equal(new PartData(42, 5, 1), d.Parts[1]);
    }

    [Fact]
    public void ExportShip_RejectsNonWholePartTail()
    {
        var bytes = new ExportShip { HullId = 1, ShipId = 1, SideId = 1, PilotType = 10, Parts = [new PartData(1, 2, 3)] }.Encode();
        // cb sits at message offset 6 (2 bytes); DataShipSize(52) + 1×PartData.Size(5) = 57.
        // Force to 56 → not a whole PartData tail beyond the fixed body.
        Assert.Equal(57, bytes[6] | (bytes[7] << 8));
        bytes[6] = 56;
        Assert.Throws<ArgumentException>(() => ExportShip.Decode(bytes));
    }
}
