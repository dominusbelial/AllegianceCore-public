using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P117 codec
/// <see cref="RelaunchShip"/> (<c>FM_S_RELAUNCH_SHIP</c>, 192). The layout is
/// pinned against a programmatically-built expected buffer (the declarative
/// <see cref="ByteBuf"/> builder from MissileWireCodecsWireTests.cs) — fixed
/// fields at their MSVC-aligned offsets and the <c>ShipLoadout</c> blob at the
/// padded <c>sizeof</c> (48), matching the native's
/// <c>pBlobDst = pbFM + cbfm</c> placement (<c>_Utility/Messages.cpp:392</c>).
/// </summary>
public class RelaunchShipWireTests
{
    [Fact]
    public void FixedSize_IsFortyEight()
    {
        // hdr(4) + ib/cb(4) + shipID(2) + carrierID(2) + position(12) +
        // velocity(12) + CompactOrientation(8) + cookie(1) = 45, rounded to the
        // 4-aligned sizeof (two Vector members) = 48.
        Assert.Equal(48, RelaunchShip.FixedSize);
        Assert.Equal(48, RelaunchShip.LoadoutOffset);
    }

    [Fact]
    public void HullOnly_RoundTrips_AllFields()
    {
        var msg = new RelaunchShip
        {
            ShipId = 707, CarrierId = 909,
            Position = new Vector3(101f, -202f, 305f),
            Velocity = new Vector3(12.5f, -7.25f, 0.0625f),
            Orientation = OrientFacing(new Vector3(0f, 1f, 0f)),
            Cookie = 0,
            HullId = 3102,
        };
        var d = RelaunchShip.Decode(msg.Encode());

        Assert.Equal((short)707, d.ShipId);
        Assert.Equal((short)909, d.CarrierId);
        AssertNear((101f, -202f, 305f), d.Position);
        AssertNear((12.5f, -7.25f, 0.0625f), d.Velocity);
        AssertNear((0f, 1f, 0f), d.Orientation.GetForward());   // quantization-tolerant
        Assert.Equal((short)3102, d.HullId);
        Assert.Empty(d.Parts);
    }

    [Fact]
    public void WithParts_RoundTrips_PartTail()
    {
        var msg = new RelaunchShip
        {
            ShipId = 801, CarrierId = 802,
            Position = Vector3.Zero, Velocity = Vector3.Zero,
            Orientation = Orientation.Identity,
            Cookie = 0,
            HullId = 990,
            Parts =
            [
                new ExpandedPartData(11, 900, 0, ExpandedPartData.FullyMounted),
                new ExpandedPartData(22, 750, 1, 128),
                new ExpandedPartData(33, 640, 2, 240),
            ],
        };
        var d = RelaunchShip.Decode(msg.Encode());
        Assert.Equal((short)990, d.HullId);
        Assert.Collection(d.Parts,
            p => { Assert.Equal((short)11, p.PartId); Assert.Equal((short)900, p.Amount); Assert.Equal((byte)0, p.MountId); Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted); },
            p => { Assert.Equal((short)22, p.PartId); Assert.Equal((short)750, p.Amount); Assert.Equal((byte)1, p.MountId); Assert.Equal((byte)128, p.FractionMounted); },
            p => { Assert.Equal((short)33, p.PartId); Assert.Equal((short)640, p.Amount); Assert.Equal((byte)2, p.MountId); Assert.Equal((byte)240, p.FractionMounted); });
    }

    [Fact]
    public void EmitsExactBytes_HullOnly()
    {
        // nParts=0 → total 48 + 2 = 50. Identity orientation encodes to
        // qa=65535 → [FF,FF,0×6] (see OriId); the byte expectation is written
        // against that real wire value.
        var msg = new RelaunchShip
        {
            ShipId = 710, CarrierId = 820,
            Position = new Vector3(1.5f, -2.5f, 3.25f),
            Velocity = new Vector3(0.5f, -0.25f, 0.125f),
            Orientation = Orientation.Identity,
            Cookie = 0,
            HullId = 3111,
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(50, w =>
        {
            w.U16(50);                          // cbmsg
            w.U16(192);                         // fmid
            w.U16(48);                          // ibLoadout
            w.U16(2);                           // cbLoadout = 2 + 6*0
            w.I16(710);                         // shipID     (offset 8)
            w.I16(820);                         // carrierID  (offset 10)
            Vec(w, 1.5f, -2.5f, 3.25f);         // position   (offset 12)
            Vec(w, 0.5f, -0.25f, 0.125f);       // velocity   (offset 24)
            OriId(w);                           // orientation(offset 36: identity → qa=65535,[FF,FF,0..])
            w.Byte(0);                          // cookie     (offset 44)
            w.Pad(3);                           // 4-aligned pad → 48
            w.I16(3111);                        // hullID     (offset 48)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void EmitsExactBytes_WithOnePart()
    {
        // nParts=1 → total 48 + 2 + 6 = 56.
        var msg = new RelaunchShip
        {
            ShipId = 712, CarrierId = 822,
            Position = Vector3.Zero, Velocity = Vector3.Zero,
            Orientation = Orientation.Identity,
            Cookie = 0,
            HullId = 3122,
            Parts = [new ExpandedPartData(55, 888, 3, 240)],
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(56, w =>
        {
            w.U16(56);                          // cbmsg
            w.U16(192);
            w.U16(48);                          // ibLoadout
            w.U16(8);                           // cbLoadout = 2 + 6*1
            w.I16(712);
            w.I16(822);
            w.Pad(24);                          // position (0) + velocity (0)
            OriId(w);                           // orientation(identity → qa=65535,[FF,FF,0..])
            w.Byte(0);                          // cookie
            w.Pad(3);                           // 4-aligned pad
            w.I16(3122);                        // hullID
            w.I16(55); w.I16(888); w.Byte(3); w.Byte(240);   // ExpandedPartData
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void RejectsWrongFmid()
    {
        var bytes = new RelaunchShip { ShipId = 723, CarrierId = 823, HullId = 771 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => RelaunchShip.Decode(badFmid));
    }

    [Fact]
    public void RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => RelaunchShip.Decode(new byte[47]));
        Assert.Throws<ArgumentException>(() => RelaunchShip.Decode([]));
    }

    [Fact]
    public void RejectsMismatchedCbmsg()
    {
        var bytes = new RelaunchShip { ShipId = 733, CarrierId = 833, HullId = 781 }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 99;               // cbmsg ≠ length 50 → reject
        Assert.Throws<ArgumentException>(() => RelaunchShip.Decode(badCb));
    }

    // ---- helpers ---------------------------------------------------------- //

    /// <summary>Write the 8-byte CompactOrientation slot for an identity
    /// orientation. Encode of identity yields <c>qa = 65535</c> → bytes
    /// <c>[FF, FF, 0, 0, 0, 0, 0, 0]</c> (the qa=65535,a=1 quaternion). This is
    /// the true wire value; the decode side only <em>reads</em> all-zeros as
    /// identity.</summary>
    private static void OriId(ByteBuf.Writer w) { w.Byte(255); w.Byte(255); w.Pad(6); }

    private static void Vec(ByteBuf.Writer w, float x, float y, float z)
    {
        w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(x)));
        w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(y)));
        w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(z)));
    }

    private static void AssertNear((float X, float Y, float Z) exp, Vector3 v)
    {
        Assert.InRange(v.X, exp.X - 0.002f, exp.X + 0.002f);
        Assert.InRange(v.Y, exp.Y - 0.002f, exp.Y + 0.002f);
        Assert.InRange(v.Z, exp.Z - 0.002f, exp.Z + 0.002f);
    }

    private static Orientation OrientFacing(Vector3 forward)
    {
        var o = new Orientation();
        o.Set(forward);
        return o;
    }
}
