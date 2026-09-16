using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P230 this-port-only extension codec
/// <see cref="MissileLockUpdate"/> (FMID 204) — the per-tick, owner-only
/// delivery of a live player's missile-lock quality + target to the MonoGame
/// client. Unlike the native-faithful codecs there is no native
/// <c>DEFINE_FEDMSG</c> to match (native never needs one: its client computes
/// the lock locally), so the byte expectation is this port's own documented
/// 35-byte layout. Byte expectations are built with <see cref="ByteBuf"/>, not
/// hand-typed hex.
/// </summary>
public class MissileLockUpdateWireTests
{
    [Fact]
    public void MissileLockUpdate_RoundTrips_FullFields()
    {
        var msg = MissileLockUpdate.From(
            shipId: 9001, targetShipId: 9011, lockFraction: 1.0f,
            new Vector3(12.5f, -3.25f, 987.125f), "Dumbo");

        var d = MissileLockUpdate.Decode(msg.Encode());

        Assert.Equal(MissileLockUpdate.Fmid, d.GetFmid());
        Assert.Equal(204, d.GetFmid());
        Assert.Equal(9001, d.ShipId);
        Assert.Equal(9011, d.TargetShipId);
        Assert.True(d.HasTarget);
        Assert.Equal(1.0f, d.LockFraction, 3);
        Assert.Equal(240, d.LockPercent);                       // full lock = div-240 240
        Assert.Equal(12.5f, d.TargetPosition.X, 3);
        Assert.Equal(-3.25f, d.TargetPosition.Y, 3);
        Assert.Equal(987.125f, d.TargetPosition.Z, 3);
        Assert.Equal("Dumbo", d.MissileTypeName);
    }

    [Fact]
    public void MissileLockUpdate_RoundTrips_NoTarget_ZeroLock_EmptyName()
    {
        var msg = MissileLockUpdate.From(9001, targetShipId: -1, 0f, Vector3.Zero, "");
        var d = MissileLockUpdate.Decode(msg.Encode());
        Assert.False(d.HasTarget);
        Assert.Equal(-1, d.TargetShipId);
        Assert.Equal(0f, d.LockFraction);
        Assert.Equal(0, d.LockPercent);
        Assert.Equal("", d.MissileTypeName);
    }

    [Fact]
    public void MissileLockUpdate_Quantizes_LockFraction_ToDiv240()
    {
        var msg = MissileLockUpdate.From(1, 2, 0.333333f, Vector3.Zero, "");
        var d = MissileLockUpdate.Decode(msg.Encode());
        Assert.Equal(80, d.LockPercent);   // 0.3333 * 240 = 80.0
    }

    [Fact]
    public void MissileLockUpdate_Truncates_Name_To13Chars()
    {
        var msg = MissileLockUpdate.From(1, 2, 0.5f, Vector3.Zero, "SuperLongMissileNameXYZ");
        Assert.Equal(35, msg.Encode().Length);
        var d = MissileLockUpdate.Decode(msg.Encode());
        Assert.Equal("SuperLongMiss", d.MissileTypeName);   // 13 chars
    }

    [Fact]
    public void MissileLockUpdate_EmitsExactBytes()
    {
        var got = MissileLockUpdate.From(1, 601, 1.0f, new Vector3(1f, 2f, 3f), "Hi").Encode();
        Assert.Equal(35, got.Length);
        var exp = ByteBuf.Build(35, w =>
        {
            w.U16(35);         // cbmsg
            w.U16(204);        // fmid
            w.I16(1);          // shipID @4
            w.I16(601);        // targetShipID @6
            w.Byte(240);       // lockPercent @8 (full)
            w.Byte(0);         // reserved @9
            w.F32(1f);         // targetPosition.X @10
            w.F32(2f);         // .Y @14
            w.F32(3f);         // .Z @18
            w.Byte((byte)'H'); // missileTypeName @22
            w.Byte((byte)'i');
            w.Pad(11);         // NUL pad to 13
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void MissileLockUpdate_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = MissileLockUpdate.From(1, 2, 0.5f, Vector3.Zero, "x").Encode();
        var bad = (byte[])bytes.Clone();
        bad[2] = 199;
        Assert.Throws<ArgumentException>(() => MissileLockUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => MissileLockUpdate.Decode(new byte[20]));
    }
}
