using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpTransitFlash"/> — the P245 warp-transit
/// screen-feedback tracker. No graphics device, no MonoGame types on the
/// surface. Detection is the local ship's <c>SectorId</c> changing (the same
/// one-shot SET_CLUSTER the server sends per transit), which must fire the
/// flash EXACTLY once per transit — never per frame, never on the initial
/// spawn assignment, never re-armed by a repeated same-sector feed. The
/// opacity is a short triangular pulse peaked at full black mid-window.
/// </summary>
public class WarpTransitFlashTests
{
    [Fact]
    public void FreshTracker_NoSector_DoesNotFlashOrArm()
    {
        var t = new WarpTransitFlash();
        Assert.Equal(0f, t.Update(0f, null));
        Assert.False(t.JustTransited);
        Assert.Equal(0f, t.Update(50f, null));   // long after "nothing" still no flash
        Assert.False(t.JustTransited);
    }

    [Fact]
    public void FirstSector_BecomesBaseline_WithoutFlashing()
    {
        // The first sector seen is the "here is your ship" assignment at join —
        // it must establish the baseline, NOT flash (no previous sector to
        // compare against yet).
        var t = new WarpTransitFlash();
        Assert.Equal(0f, t.Update(0f, (short)1050));
        Assert.False(t.JustTransited);
        Assert.Equal(0f, t.Update(0.033f, (short)1050));   // same sector again: still nothing
        Assert.False(t.JustTransited);
    }

    [Fact]
    public void SectorChange_ArmsOnce_ThenSameSectorDoesNotRearm()
    {
        var t = new WarpTransitFlash();
        t.Update(0f, (short)1050);      // baseline
        t.Update(0.030f, (short)1050);

        // Real transit: sector changes to 927 at now=1.0.
        Assert.Equal(0f, t.Update(1.0f, (short)927));   // arm frame: opacity starts at 0
        Assert.True(t.JustTransited);                   // armed exactly once here

        // The very next frame sees the SAME new sector — must NOT re-arm, only
        // continue the pulse (opacity > 0, JustTransited false).
        Assert.True(t.Update(1.075f, (short)927) > 0f);
        Assert.False(t.JustTransited);

        // Well inside the window, same sector: still just the running pulse.
        Assert.True(t.Update(1.095f, (short)927) > 0f);
        Assert.False(t.JustTransited);
    }

    [Fact]
    public void Pulse_PeaksFullyBlackAtMidpoint_AndClearsByDuration()
    {
        var t = new WarpTransitFlash();
        t.Update(0f, (short)10);              // baseline sector 10
        t.Update(1.0f, (short)20);            // transit at now=1.0 -> arms (armedAt=1.0)

        // Symmetric triangular pulse over FlashDurationSeconds=0.36 (mid 0.18):
        // rises 0->1 to full black at the midpoint, falls 1->0 to the end. Sample
        // every 0.01s across the window: the peak must reach ~full black and the
        // window must be clear again by the duration (and stay clear after).
        float dur = WarpTransitFlash.FlashDurationSeconds;
        float peak = 0f;
        for (int k = 1; k <= 36; k++)
        {
            float o = t.Update(1.0f + k * 0.01f, (short)20);
            if (o > peak) peak = o;
        }
        Assert.InRange(peak, 0.99f, 1.001f);          // reached (near-)fully black mid-pulse
        Assert.Equal(0f, t.Update(1.0f + dur, (short)20), 3);   // window over -> clear
        Assert.Equal(0f, t.Update(1.0f + dur + 0.020f, (short)20), 3);   // and stays clear
        Assert.False(t.Active);
    }

    [Fact]
    public void SameSector_OverManyFrames_NeverFlashes()
    {
        var t = new WarpTransitFlash();
        for (int i = 0; i < 901; i++)
        {
            float o = t.Update(i * (1f / 60f), (short)1050);
            Assert.Equal(0f, o);
            Assert.False(t.JustTransited);
        }
    }

    [Fact]
    public void SectorClearingToUnknown_DoesNotFalseArm()
    {
        // If the own ship is momentarily absent (no entity), the tracker must
        // forget the baseline without flashing; a later same-value reappearance
        // is treated as a fresh baseline, not a transit.
        var t = new WarpTransitFlash();
        t.Update(0f, (short)1050);
        Assert.Equal(0f, t.Update(0.060f, null));   // cleared: no previous, no flash
        Assert.False(t.JustTransited);
        Assert.Equal(0f, t.Update(0.080f, (short)1050));   // reappear same sector = new baseline
        Assert.False(t.JustTransited);
    }

    [Fact]
    public void MultipleTransits_EachArmExactlyOnce()
    {
        var t = new WarpTransitFlash();
        int arms = 0;
        t.Update(0f, (short)1);
        for (int i = 1; i < 180; i++)        // ~3s: sector hops 1->2->3 then stops
        {
            float now = i / 60f;
            short sector = (short)((i / 60) % 3 + 1);   // 1,1,...1,2,2,...,2,3,...
            t.Update(now, sector);
            if (t.JustTransited) arms++;
        }
        // Sequence hops 1->2->3 => exactly two transit arms despite hundreds of
        // repeated same-sector frames in between.
        Assert.Equal(2, arms);
    }

    [Fact]
    public void PulseExpires_ThenLaterTransitArmsAgain()
    {
        var t = new WarpTransitFlash();
        t.Update(0f, (short)1);
        t.Update(1.0f, (short)2);                       // first transit
        Assert.True(t.JustTransited);
        Assert.Equal(0f, t.Update(1.0f + WarpTransitFlash.FlashDurationSeconds, (short)2));

        // Much later, another genuine transit arms afresh.
        t.Update(5.0f, (short)3);
        Assert.True(t.JustTransited);
        Assert.True(t.Update(5.007f, (short)3) > 0f);   // into the rising edge of the new pulse
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var t = new WarpTransitFlash();
        t.Update(0f, (short)1);
        t.Update(1.0f, (short)2);
        Assert.True(t.JustTransited);

        t.Reset();
        Assert.False(t.Active);
        Assert.False(t.JustTransited);
        Assert.Equal(0f, t.Opacity);
        // After reset, the next sector is a fresh baseline (no flash).
        Assert.Equal(0f, t.Update(0f, (short)7));
        Assert.False(t.JustTransited);
    }
}
