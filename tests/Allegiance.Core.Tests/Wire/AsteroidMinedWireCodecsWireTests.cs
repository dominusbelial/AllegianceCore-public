using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="AsteroidMined"/> — the P120
/// <c>FM_S_ASTEROID_MINED</c>(198) codec (the server's "this He3 rock was
/// mined" notice, <c>Messages.h:1027-1031</c>). Same fixed
/// short/short/byte layout as <see cref="AsteroidDestroyed"/> (which is why the
/// two share a 9-byte width), differing only in fmid and in carrying the rock's
/// remaining ore as a div-240 <c>BytePercentage</c> instead of an
/// <c>explodeF</c> bool. Bytes are pinned against a programmatically-built
/// expectation (the <see cref="ByteBuf"/> builder lifted from
/// <c>MissileWireCodecsWireTests.cs</c>), not hand-typed hex.
/// </summary>
public class AsteroidMinedWireCodecsWireTests
{
    [Fact]
    public void Constants_AreDeclared()
    {
        Assert.Equal((ushort)198, AsteroidMined.Fmid);
        Assert.Equal(9, AsteroidMined.FixedSize);   // hdr(4) + clusterID(2) + asteroidID(2) + bpOreFraction(1)
    }

    [Fact]
    public void RoundTrips_ThreeFields()
    {
        var msg = new AsteroidMined { ClusterId = 503, AsteroidId = 907, BpOreFraction = 0xA0 };
        var d = AsteroidMined.Decode(msg.Encode());
        Assert.Equal((short)503, d.ClusterId);
        Assert.Equal((short)907, d.AsteroidId);
        Assert.Equal((byte)0xA0, d.BpOreFraction);
    }

    [Fact]
    public void EmitsExactNineBytes()
    {
        var msg = new AsteroidMined { ClusterId = 708, AsteroidId = 819, BpOreFraction = 0xB1 };
        var got = msg.Encode();
        var exp = ByteBuf.Build(9, w =>
        {
            w.U16(9);      // cbmsg
            w.U16(198);    // fmid
            w.I16(708);    // clusterID (SectorID)
            w.I16(819);    // asteroidID (AsteroidID)
            w.Byte(0xB1);  // bpOreFraction (BytePercentage, div-240)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ForAsteroid_Fills_Fields_And_Div240_Fraction()
    {
        // A live Asteroid carries the ids and OreFraction the broadcast should
        // reflect; the factory must bake the div-240 conversion in (the caller
        // never touches the wire convention directly).
        var data = new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [0f, 0f, 0f],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: 960f,
            OreMax: 1200f,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: 605,
            HitPoints: 0f,
            Radius: 40,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: 612,
            Name: "ROID_605",
            Fraction: 0);
        var ast = new Asteroid(data);
        // 960/1200 = 0.8 → 0.8*240 = 192.
        var msg = AsteroidMined.ForAsteroid(ast, ast.OreFraction);

        Assert.Equal((short)612, msg.ClusterId);
        Assert.Equal((short)605, msg.AsteroidId);
        Assert.Equal((byte)192, msg.BpOreFraction);
    }

    [Fact]
    public void RejectsWrongFmid()
    {
        var bytes = new AsteroidMined { ClusterId = 501, AsteroidId = 902, BpOreFraction = 1 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 197;
        Assert.Throws<ArgumentException>(() => AsteroidMined.Decode(badFmid));
    }

    [Fact]
    public void RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => AsteroidMined.Decode(new byte[8]));
    }
}
