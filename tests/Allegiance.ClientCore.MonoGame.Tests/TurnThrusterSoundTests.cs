using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="TurnThrusterSound"/> — the control-input →
/// maneuvering-thruster mapping behind P267's hull turn sound (the local
/// ship's <c>adjust</c> loop). No audio device involved; the mapping is
/// deliberately free of MonoGame types.
/// </summary>
public class TurnThrusterSoundTests
{
    [Fact]
    public void StraightCoast_ProducesInaudibleLevel()
    {
        // No commanded turn → level 0, below the 0.01 audibility threshold
        // (native's m_fTurnSoundLevel > 0.01 gate, trekigc.cpp:1316-1319).
        float level = TurnThrusterSound.LevelFor(0f, 0f, 0f);
        Assert.Equal(0f, level, 5);
        Assert.False(TurnThrusterSound.IsAudible(level));
    }

    [Fact]
    public void Turning_ProducesAudibleLevel()
    {
        // A moderate yaw input (0.5 of max) → turnRate 0.25 → level = 5 (>1
        // clamps to 1 in the full-turn case, but well above 0.01 here).
        float level = TurnThrusterSound.LevelFor(0.5f, 0f, 0f);
        Assert.True(TurnThrusterSound.IsAudible(level), $"expected turn level {level} to be audible");
        Assert.InRange(level, 0f, 1f);
    }

    [Fact]
    public void FullCombinedTurn_ClampsToOne()
    {
        // Yaw+pitch+roll all at max → turnRate 3 → level = min(1, 60) = 1.
        float level = TurnThrusterSound.LevelFor(1f, 1f, 1f);
        Assert.Equal(1f, level, 5);
    }

    [Fact]
    public void PitchFollowsLevel_NativeFormula()
    {
        // Native SetPitch(0.50f + m_fTurnSoundLevel/3), trekigc.cpp:1167-1169.
        Assert.Equal(0.50f, TurnThrusterSound.PitchFor(0f), 5);
        Assert.Equal(0.50f + 0.5f / 3f, TurnThrusterSound.PitchFor(0.5f), 5);
        Assert.Equal(0.50f + 1f / 3f, TurnThrusterSound.PitchFor(1f), 5);
    }

    [Fact]
    public void VerySlightInput_BelowThresholdIsInaudible()
    {
        // Tiny sub-noise input: yaw 0.015 → turnRate 0.000225 → *20 = 0.0045,
        // below the 0.01 threshold — the loop stays silent (native's gate).
        float level = TurnThrusterSound.LevelFor(0.015f, 0f, 0f);
        Assert.False(TurnThrusterSound.IsAudible(level));
    }
}
