using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P264 this-port-only extension codec
/// <see cref="MissileTrackWarningUpdate"/> (FMID 208) — the per-tick, owner-only
/// "an enemy missile is tracking YOU" delivery (armed bool + best lock fraction)
/// to the MonoGame client. Unlike the native-faithful codecs there is no native
/// <c>DEFINE_FEDMSG</c> to match (native's client arms the warning locally by
/// walking its own IGC sim, <c>trekigc.cpp:5025-5071</c>), so the byte expectation
/// is this port's own documented 6-byte layout. Byte expectations are built with
/// <see cref="ByteBuf"/>, not hand-typed hex.
/// </summary>
public class MissileTrackWarningUpdateWireTests
{
    [Fact]
    public void MissileTrackWarningUpdate_RoundTrips_Tracked()
    {
        var msg = MissileTrackWarningUpdate.From(tracked: true, bestLock: 1.0f);
        var d = MissileTrackWarningUpdate.Decode(msg.Encode());

        Assert.Equal(MissileTrackWarningUpdate.Fmid, d.GetFmid());
        Assert.Equal(208, d.GetFmid());
        Assert.True(d.Tracked);
        Assert.Equal(240, d.BestLockPercent);
        Assert.Equal(1.0f, d.BestLock, 3);
    }

    [Fact]
    public void MissileTrackWarningUpdate_RoundTrips_Clear()
    {
        var d = MissileTrackWarningUpdate.Decode(MissileTrackWarningUpdate.From(tracked: false, bestLock: 0f).Encode());
        Assert.False(d.Tracked);
        Assert.Equal(0, d.BestLockPercent);
        Assert.Equal(0f, d.BestLock);
    }

    [Fact]
    public void MissileTrackWarningUpdate_Quantizes_BestLock_ToDiv240()
    {
        var d = MissileTrackWarningUpdate.Decode(MissileTrackWarningUpdate.From(true, 0.5f).Encode());
        Assert.Equal(120, d.BestLockPercent);   // 0.5 * 240 = 120
        Assert.Equal(0.5f, d.BestLock, 3);
    }

    [Fact]
    public void MissileTrackWarningUpdate_Untracked_ForcesZeroPercent()
    {
        // Even if a nonzero lock is supplied, not-tracked must read clear (the
        // server never sets it, but the codec guarantees the invariant).
        var d = MissileTrackWarningUpdate.Decode(MissileTrackWarningUpdate.From(false, 0.9f).Encode());
        Assert.False(d.Tracked);
        Assert.Equal(0, d.BestLockPercent);
    }

    [Fact]
    public void MissileTrackWarningUpdate_EmitsExactBytes()
    {
        var got = MissileTrackWarningUpdate.From(tracked: true, bestLock: 0.5f).Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);       // cbmsg
            w.U16(208);     // fmid
            w.Byte(1);      // bTracked @4
            w.Byte(120);    // bestLockPercent @5 (0.5 * 240)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void MissileTrackWarningUpdate_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = MissileTrackWarningUpdate.From(true, 0.5f).Encode();
        var bad = (byte[])bytes.Clone();
        bad[2] = 199;
        Assert.Throws<ArgumentException>(() => MissileTrackWarningUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => MissileTrackWarningUpdate.Decode(new byte[3]));
    }
}
