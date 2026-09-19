using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P266 this-port-only extension codec
/// <see cref="LeadIndicatorUpdate"/> (FMID 210) — the per-tick, owner-only
/// predictive lead-aim delivery (lead world point + target position + range /
/// hit-probability flags) to the MonoGame client. There is no native
/// <c>DEFINE_FEDMSG</c> to match (native's client solved the lead locally,
/// <c>indicator.cpp:199-360</c>), so the byte expectation is this port's own
/// documented 34-byte layout. Bytes are built with <see cref="ByteBuf"/>, not
/// hand-typed hex.
/// </summary>
public class LeadIndicatorUpdateWireTests
{
    [Fact]
    public void LeadIndicatorUpdate_RoundTrips_HasLead()
    {
        var msg = LeadIndicatorUpdate.From(
            shipId: 7, targetShipId: 42, inRange: true, green01: 0.75f,
            leadWorldPos: new Vector3(100f, -200f, 300f),
            targetPos: new Vector3(90f, -180f, 280f));

        var d = LeadIndicatorUpdate.Decode(msg.Encode());

        Assert.Equal(LeadIndicatorUpdate.Fmid, d.GetFmid());
        Assert.Equal(210, d.GetFmid());
        Assert.Equal(7, d.ShipId);
        Assert.Equal(42, d.TargetShipId);
        Assert.True(d.HasLead);
        Assert.True(d.InRange);
        Assert.Equal(180, d.GreenPercent);                 // 0.75 * 240
        Assert.Equal(0.75f, d.Green, 3);
        Assert.Equal(new Vector3(100f, -200f, 300f), d.LeadWorldPos);
        Assert.Equal(new Vector3(90f, -180f, 280f), d.TargetPos);
    }

    [Fact]
    public void LeadIndicatorUpdate_RoundTrips_NoLead()
    {
        var d = LeadIndicatorUpdate.Decode(
            LeadIndicatorUpdate.From(-1, -1, false, 0f, Vector3.Zero, Vector3.Zero).Encode());
        Assert.False(d.HasLead);
        Assert.Equal(-1, d.TargetShipId);
        Assert.False(d.InRange);
        Assert.Equal(0f, d.Green);
    }

    [Fact]
    public void LeadIndicatorUpdate_Clamps_Green()
    {
        // 1.5 (>1) clamps to 240; -0.5 (<0) clamps to 0.
        var hi = LeadIndicatorUpdate.Decode(LeadIndicatorUpdate.From(1, 2, true, 1.5f, new Vector3(1, 1, 1), new Vector3(1, 1, 1)).Encode());
        Assert.Equal(240, hi.GreenPercent);
        var lo = LeadIndicatorUpdate.Decode(LeadIndicatorUpdate.From(1, 2, true, -0.5f, new Vector3(1, 1, 1), new Vector3(1, 1, 1)).Encode());
        Assert.Equal(0, lo.GreenPercent);
    }

    [Fact]
    public void LeadIndicatorUpdate_EmitsExactBytes()
    {
        var got = LeadIndicatorUpdate.From(
            shipId: 7, targetShipId: 42, inRange: true, green01: 0.5f,
            leadWorldPos: new Vector3(1f, 2f, 3f),
            targetPos: new Vector3(4f, 5f, 6f)).Encode();

        Assert.Equal(34, got.Length);
        var exp = ByteBuf.Build(34, w =>
        {
            w.U16(34);                  // cbmsg
            w.U16(210);                 // fmid
            w.I16(7);                   // shipID @4
            w.I16(42);                  // targetShipID @6
            w.Byte(1);                  // inRange @8
            w.Byte(120);                // green @9 (0.5 * 240)
            w.F32(1f); w.F32(2f); w.F32(3f);   // leadWorldPos @10
            w.F32(4f); w.F32(5f); w.F32(6f);   // targetPos @22
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void LeadIndicatorUpdate_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = LeadIndicatorUpdate.From(1, 2, true, 1f, new Vector3(1, 1, 1), new Vector3(1, 1, 1)).Encode();
        var bad = (byte[])bytes.Clone();
        bad[2] = 199;
        Assert.Throws<ArgumentException>(() => LeadIndicatorUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => LeadIndicatorUpdate.Decode(new byte[3]));
    }
}
