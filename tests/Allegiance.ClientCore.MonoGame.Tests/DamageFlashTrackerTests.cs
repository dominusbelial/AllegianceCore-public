using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="DamageFlashTracker"/> — the P228 local-hull →
/// HUD damage-flash detector (a faithful port of native <c>DamageShipEvent</c> /
/// <c>SetTimeDamaged</c> / <c>GetFlash</c>, trekigc.cpp:4270-4283,
/// WinTrek.cpp:8922-8955). No graphics device, no MonoGame types on the surface.
/// Covers: a hull decrease arms the flash, the flash cycles on/off at the right
/// 0.25s/0.25s cadence over a 2.0s window, no decrease never flashes, tiny
/// within-epsilon jitter never flashes, and re-arming while flashing extends the
/// window via native's boundary-fudge. Because the tracker is fed ONLY the local
/// ship's hull (Game1 gates on <c>world.MyShipId</c>, matching native's
/// <c>pShip == GetShip()</c> at trekigc.cpp:4281), damage to any other ship can
/// never reach it — an unhit/local-only hull stream is exactly the
/// <c>FlatHullStream_NeverFlashes</c> case below.
/// </summary>
public class DamageFlashTrackerTests
{
    [Fact]
    public void FreshTracker_NeverTakenHit_DoesNotFlash()
    {
        var t = new DamageFlashTracker();
        Assert.False(t.IsFlashing(0f));
        Assert.False(t.IsFlashing(50f));   // long after "nothing" still no flash
        Assert.False(t.Update(1f, 0.311f)); // first feed = baseline only
    }

    [Fact]
    public void FirstUpdate_EstablishesBaseline_WithoutFlashing()
    {
        var t = new DamageFlashTracker();
        // No "previous frame" exists on the very first frame, so even a low hull
        // must not false-trigger (the first-frame-after-connect guard).
        Assert.False(t.Update(0f, 0.422f));
        Assert.False(t.Update(0.031f, 0.421f)); // subsequent ~flat frame: still nothing
    }

    [Fact]
    public void HullDecrease_ArmsFlash_AndCyclesOnOffAtRightCadence()
    {
        var t = new DamageFlashTracker();
        t.Arm(0f);                                   // local ship took a hit at t=0

        // On/off halves are 0.25s/0.25s over a 2.0s window (c_dtFlashOn/Off,
        // c_dtFlashingDuration — WinTrek.cpp:35-37). Sample well inside each half
        // so no boundary-float ambiguity.
        Assert.True(t.IsFlashing(0.05f));    // on  [0.00, 0.25]
        Assert.False(t.IsFlashing(0.305f));  // off [0.25, 0.50]
        Assert.True(t.IsFlashing(0.558f));   // on  [0.50, 0.75]
        Assert.False(t.IsFlashing(0.777f));  // off [0.75, 1.00]
        Assert.True(t.IsFlashing(1.028f));   // on  [1.00, 1.25]
        Assert.False(t.IsFlashing(1.312f));  // off [1.25, 1.50]
        Assert.True(t.IsFlashing(1.619f));   // on  [1.50, 1.75]
        Assert.False(t.IsFlashing(1.832f));  // off [1.75, 2.00]
        Assert.False(t.IsFlashing(2.022f));  // window over (t > 2.0)
    }

    [Fact]
    public void Update_HullDecreaseArms_SteadyHullKeepsCadence_ThenExpires()
    {
        var t = new DamageFlashTracker();
        Assert.False(t.Update(0.000f, 1.000f));   // baseline
        Assert.False(t.Update(0.050f, 1.000f));   // hull steady → no flash

        Assert.True(t.Update(0.370f, 0.888f));    // HIT (big drop) → flash ON
        // Hull drifts down a fixed 0.001 per frame — clearly sub-epsilon
        // (HullDiffEpsilon = 0.002), so no re-arm; the rest is pure flash timing.
        Assert.False(t.Update(0.850f, 0.887f));   // off half → OFF
        Assert.True(t.Update(1.390f, 0.886f));    // on half → ON
        Assert.False(t.Update(2.610f, 0.885f));   // past the 2.0s window → OFF
    }

    [Fact]
    public void FlatHullStream_OverManyFrames_NeverFlashes()
    {
        // Represents an unhit ship (and, by construction, any non-local ship —
        // the tracker only ever sees the local hull, so another ship's damage
        // cannot reach it): a constant hull is never a decrease, so no flash.
        var t = new DamageFlashTracker();
        for (int i = 0; i < 481; i++)
            Assert.False(t.Update(i * (1f / 60f), 1.0f));
    }

    [Fact]
    public void SubEpsilonJitter_NeverArmsFlash()
    {
        // Float/rounding noise within HullDiffEpsilon (0.2% hull) must not flash.
        var t = new DamageFlashTracker();
        t.Update(0f, 1.000f);
        Assert.False(t.Update(1f / 60f, 1.0f - 0.001f)); // 0.1% drop < epsilon
        Assert.False(t.Update(2f / 60f, 1.000f));        // recovery, not an arm
    }

    [Fact]
    public void HullDecreased_Static_MatchesThresholdRule()
    {
        Assert.False(DamageFlashTracker.HullDecreased(null, 0.404f));    // no baseline yet
        Assert.False(DamageFlashTracker.HullDecreased(0.405f, 0.406f));  // increased
        Assert.False(DamageFlashTracker.HullDecreased(0.507f, 0.508f));  // increased
        Assert.False(DamageFlashTracker.HullDecreased(0.509f, 0.741f));  // increased strongly
        Assert.False(DamageFlashTracker.HullDecreased(0.824f, 0.823f));  // 0.001 drop < epsilon
        Assert.True(DamageFlashTracker.HullDecreased(0.912f, 0.804f));   // 0.108 drop >> epsilon
    }

    [Fact]
    public void ReArmWhileFlashing_ExtendsWindow_ViaBoundaryFudge()
    {
        var t = new DamageFlashTracker();
        t.Arm(1.0f);
        Assert.True(t.IsFlashing(1.035f));

        // Re-arm at 1.836: within the flash window, so native's boundary fudge
        // (SetTimeDamaged, WinTrek.cpp:8924-8932) snaps the hit instant to the
        // most recent on/off boundary: dt=0.836, n=floor(1.672)=1 → 1.0+0.5=1.5.
        // The window therefore extends to 1.5+2.0=3.5 instead of the original 3.0.
        t.Arm(1.837f);
        Assert.True(t.IsFlashing(3.099f));   // past original end (3.0) but lit again
    }

    [Fact]
    public void WithoutReArm_FlashDoesNotExtendPastOriginalWindow()
    {
        // Same single hit at t=1.0 with NO re-arm: the window ends at 3.0 and the
        // tracker is dark afterwards (contrast with ReArmWhileFlashing... above).
        var t = new DamageFlashTracker();
        t.Arm(1.0f);
        Assert.False(t.IsFlashing(3.088f));
    }

    [Fact]
    public void ArmAfterWindowExpired_ColdRestartsFlashFromNow()
    {
        var t = new DamageFlashTracker();
        t.Arm(1.0f);
        Assert.False(t.IsFlashing(3.077f));   // first window spent

        t.Arm(4.021f);                         // fully expired → cold start from now
        Assert.True(t.IsFlashing(4.024f));     // freshly lit
        Assert.False(t.IsFlashing(4.323f));    // into the first off half
    }
}
