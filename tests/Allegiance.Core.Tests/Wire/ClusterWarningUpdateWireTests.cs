using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P374 this-port-only extension codec
/// <see cref="ClusterWarningUpdate"/> (FMID 214) — the per-side, change-driven
/// delivery of a sector's threat state to a side's clients. There is no native
/// <c>DEFINE_FEDMSG</c> (native's client computes warnings locally), so the byte
/// expectation is this port's own documented 10-byte layout.
/// </summary>
public class ClusterWarningUpdateWireTests
{
    [Fact]
    public void RoundTrips_WarningUpdate()
    {
        var d = ClusterWarningUpdate.Decode(ClusterWarningUpdate.From(5, 13).Encode());
        Assert.Equal(214, d.GetFmid());
        Assert.Equal((short)5, d.SectorId);
        Assert.Equal((short)13, d.Warning);
        Assert.False(d.Secured);
        Assert.False(d.Lost);
    }

    [Fact]
    public void RoundTrips_SecuredTransition()
    {
        var d = ClusterWarningUpdate.Decode(ClusterWarningUpdate.From(7, 0, secured: true).Encode());
        Assert.Equal((short)7, d.SectorId);
        Assert.Equal((short)0, d.Warning);
        Assert.True(d.Secured);
        Assert.False(d.Lost);
    }

    [Fact]
    public void RoundTrips_LostTransition()
    {
        var d = ClusterWarningUpdate.Decode(ClusterWarningUpdate.From(9, 3, lost: true).Encode());
        Assert.True(d.Lost);
        Assert.False(d.Secured);
    }

    [Fact]
    public void EmitsExactBytes()
    {
        var got = ClusterWarningUpdate.From(7, 8).Encode();
        Assert.Equal(10, got.Length);
        var exp = ByteBuf.Build(10, w =>
        {
            w.U16(10);    // cbmsg
            w.U16(214);   // fmid
            w.I16(7);     // sectorId @4
            w.I16(8);     // warning @6
            w.Byte(0);    // secured @8
            w.Byte(0);    // lost @9
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = ClusterWarningUpdate.From(1, 2).Encode();
        var bad = (byte[])bytes.Clone();
        bad[2] = 199;
        Assert.Throws<ArgumentException>(() => ClusterWarningUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => ClusterWarningUpdate.Decode(new byte[4]));
    }
}
