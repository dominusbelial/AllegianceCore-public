using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="TargetSelectionStyle"/> — the tint/thickness of
/// the P345 player autopilot-target selection bracket. No graphics device (same
/// discipline as <see cref="LockBracketStyleTests"/>). Besides pinning the
/// selected-vs-engaged contract, they pin the VISUAL-DISTINCTION requirement from
/// the brief: the target-selection box must not be confusable with the missile-lock
/// bracket (<see cref="LockBracketStyle"/> grey/amber/red), because both can be on
/// screen at once.
/// </summary>
public class TargetSelectionStyleTests
{
    [Fact]
    public void Selected_And_Engaged_Colours_Differ()
    {
        Assert.NotEqual(TargetSelectionStyle.Selected, TargetSelectionStyle.Engaged);
    }

    [Fact]
    public void Engaged_Is_Brighter_Than_Selected()
    {
        // Engaged should read as clearly "actively flying there" — strictly more
        // luminous in every channel than the merely-selected state.
        Assert.True(TargetSelectionStyle.Engaged.R > TargetSelectionStyle.Selected.R);
        Assert.True(TargetSelectionStyle.Engaged.G > TargetSelectionStyle.Selected.G);
        Assert.True(TargetSelectionStyle.Engaged.B > TargetSelectionStyle.Selected.B);
    }

    [Fact]
    public void Engaged_Border_Is_Thicker_Than_Selected()
    {
        Assert.True(TargetSelectionStyle.ThicknessFor(true) > TargetSelectionStyle.ThicknessFor(false));
    }

    /// <summary>The core "don't make them visually identical" requirement: neither
    /// target-selection colour may match any of the missile-lock bracket's three
    /// states (dim grey / amber / hot red). A shape difference (closed rectangle vs
    /// open corner-L arms) additionally separates them, but the colours must not
    /// alias either.</summary>
    [Fact]
    public void Colours_Are_Distinct_From_Every_MissileLock_State()
    {
        var lockColours = new[]
        {
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.None),
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Partial),
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Full),
        };
        Assert.DoesNotContain(TargetSelectionStyle.Selected, lockColours);
        Assert.DoesNotContain(TargetSelectionStyle.Engaged, lockColours);
    }

    /// <summary>The chosen hue is a COOL aqua-blue (blue channel dominant) — not the
    /// teal HUD accent, not the amber/red lock tones, not the green/red lead ramp.
    /// Documented so a future tweak can't drift it into the lock palette by accident.</summary>
    [Fact]
    public void Chosen_Hue_Is_Cool_AquaBlue()
    {
        Assert.True(TargetSelectionStyle.Selected.B > TargetSelectionStyle.Selected.R);
        Assert.True(TargetSelectionStyle.Selected.G > TargetSelectionStyle.Selected.R);
        Assert.True(TargetSelectionStyle.Engaged.B > TargetSelectionStyle.Engaged.R);
    }

    [Fact]
    public void HalfSize_Is_Positive_And_Reasonable()
    {
        Assert.True(TargetSelectionStyle.HalfSize > 0f);
        Assert.InRange(TargetSelectionStyle.HalfSize, 8f, 64f);
    }
}
