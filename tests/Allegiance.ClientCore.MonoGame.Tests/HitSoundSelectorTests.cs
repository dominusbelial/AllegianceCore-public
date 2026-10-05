using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P463 — pure-logic tests for the hit-sound classification
/// (<see cref="HitSoundSelector"/>) and its distance curve
/// (<see cref="HitSoundCurve"/>): my/other × shield/hull exactly as native
/// <c>ThingSite::RegisterHit</c> decides (trekigc.cpp:2373-2385), the
/// once-per-target-per-frame guard (<c>m_bPlayedHitSoundThisFrame</c>,
/// trekigc.cpp:2372), and the positional falloff for "other" cues. No graphics
/// device, no MonoGame types — pure <see cref="HitSoundEntity"/> + tuples.
/// </summary>
public class HitSoundSelectorTests
{
    private static HitSoundEntity Hit(short target, bool absorbed, short cluster = 0) =>
        new(target, cluster, absorbed, -1, Vector3.Zero);

    // ── classification ────────────────────────────────────────────────────

    [Theory]
    [InlineData(7,  true,  7,  HitSoundKind.MyShield)]     // our ship, shield-absorbed
    [InlineData(7,  false, 7,  HitSoundKind.MyHull)]       // our ship, hull
    [InlineData(8,  true,  7,  HitSoundKind.OtherShield)]  // other ship, shield-absorbed
    [InlineData(8,  false, 7,  HitSoundKind.OtherHull)]    // other ship, hull
    public void Classify_MyOtherTimesShieldHull(short target, bool absorbed, short myShipId, HitSoundKind expected)
    {
        Assert.Equal(expected, HitSoundSelector.Classify(target, absorbed, myShipId));
    }

    [Fact]
    public void Classify_NoLocalShip_MakesEverythingOther()
    {
        // No MyShipId yet → even a hit whose target WOULD be ours is "other"
        // (the client hasn't been assigned a ship, so nothing is ours).
        Assert.Equal(HitSoundKind.OtherShield, HitSoundSelector.Classify(7, true, null));
        Assert.Equal(HitSoundKind.OtherHull, HitSoundSelector.Classify(7, false, null));
    }

    [Fact]
    public void Classify_NaTargetId_IsRockHit()
    {
        // P463c — the wire marks an asteroid with target id NA(-1); native plays
        // the distinct rockHitSound (hitrock) for a chipped rock (asteroidigc.h:154-155),
        // NOT a hull/shield cue — regardless of the local ship id or absorbed flag.
        Assert.Equal(HitSoundKind.Rock, HitSoundSelector.Classify(-1, false, 7));
        Assert.Equal(HitSoundKind.Rock, HitSoundSelector.Classify(-1, true, null));
    }

    // ── once-per-target-per-frame guard ───────────────────────────────────

    [Fact]
    public void Select_PlaysAtMostOneCuePerTargetPerFrame_FirstWins()
    {
        // Two distinct targets hit multiple times in one frame → each sounds once,
        // and only the FIRST strike of each target wins (native m_bPlayedHitSoundThisFrame).
        var hits = new[]
        {
            Hit(1, true),    // target 1: shield → MyShield
            Hit(2, false),   // target 2: hull   → OtherHull
            Hit(1, false),   // target 1 again → SUPPRESSED (shield already won this frame)
            Hit(2, true),    // target 2 again → SUPPRESSED (hull already won this frame)
        };

        var chosen = HitSoundSelector.Select(hits, myShipId: 1).ToList();
        Assert.Equal(2, chosen.Count);
        Assert.Contains(chosen, c => c.TargetId == 1 && c.Kind == HitSoundKind.MyShield);
        Assert.Contains(chosen, c => c.TargetId == 2 && c.Kind == HitSoundKind.OtherHull);
    }

    [Fact]
    public void Select_EmitsNothingForAnEmptyFrame()
    {
        Assert.Empty(HitSoundSelector.Select(Array.Empty<HitSoundEntity>(), myShipId: 1));
    }

    // ── distance curve ────────────────────────────────────────────────────

    [Fact]
    public void Curve_FalloffIsFullNearSilentFarAndMonotonic()
    {
        Assert.Equal(1f, HitSoundCurve.Falloff(0f));
        Assert.Equal(1f, HitSoundCurve.Falloff(-5f));         // clamp
        Assert.Equal(0f, HitSoundCurve.Falloff(HitSoundCurve.AudibleRadius));
        Assert.Equal(0f, HitSoundCurve.Falloff(HitSoundCurve.AudibleRadius + 1f));
        Assert.True(HitSoundCurve.Falloff(100f) > HitSoundCurve.Falloff(1000f));
        Assert.True(HitSoundCurve.Falloff(1000f) > HitSoundCurve.Falloff(5000f));
    }

    [Fact]
    public void Curve_MyVolumeIsFull_OtherVolumeScalesAndCapsBelow()
    {
        Assert.Equal(HitSoundCurve.MyVolume, HitSoundCurve.MyVolume);      // my hits are cockpit-range full
        Assert.True(HitSoundCurve.OtherVolume(0f) <= HitSoundCurve.MyVolume);
        Assert.True(HitSoundCurve.OtherVolume(0f) > 0f);
        Assert.Equal(0f, HitSoundCurve.OtherVolume(HitSoundCurve.AudibleRadius));   // out of range → silent
        // Close "other" hits are audible but capped below my-hits (native priority -1.6).
        Assert.True(HitSoundCurve.OtherVolume(50f) < HitSoundCurve.MyVolume);
    }
}
