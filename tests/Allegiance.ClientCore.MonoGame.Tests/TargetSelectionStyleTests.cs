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

    // ── P376 friend/enemy recolouring ─────────────────────────────────────

    /// <summary>An ENEMY ship's bracket must be genuinely RED — the red channel
    /// dominant over both blue and green in both engagement states.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enemy_Bracket_Is_Red_In_Both_States(bool engaged)
    {
        var col = TargetSelectionStyle.ColorFor(engaged, true);
        Assert.True(col.R > col.G, "enemy red must dominate green");
        Assert.True(col.R > col.B, "enemy red must dominate blue");
        Assert.True(col.G >= col.B, "red family leans warm (not purple)");
    }

    /// <summary>A FRIENDLY (same-side) ship's bracket must be genuinely BLUE — the
    /// blue channel dominant over both red and green in both engagement states.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Friendly_Bracket_Is_Blue_In_Both_States(bool engaged)
    {
        var col = TargetSelectionStyle.ColorFor(engaged, false);
        Assert.True(col.B > col.R, "friendly blue must dominate red");
        Assert.True(col.B >= col.G, "blue family leans cool");
    }

    /// <summary>A NON-SHIP target (no side) keeps the original P345 neutral aqua
    /// family — not red/blue.</summary>
    [Fact]
    public void NonShip_Target_Keeps_Neutral_Aqua()
    {
        Assert.Equal(TargetSelectionStyle.Selected, TargetSelectionStyle.ColorFor(false, null));
        Assert.Equal(TargetSelectionStyle.Engaged, TargetSelectionStyle.ColorFor(true, null));
    }

    /// <summary>Within EACH side family the engaged variant stays strictly brighter,
    /// so the flying-toward state reads whether the target is friend or enemy.</summary>
    [Theory]
    [InlineData(true)]   // enemy
    [InlineData(false)]  // friendly
    public void Engaged_Is_Brighter_Within_Each_Side_Family(bool isEnemy)
    {
        var sel = TargetSelectionStyle.ColorFor(false, isEnemy);
        var eng = TargetSelectionStyle.ColorFor(true, isEnemy);
        Assert.True(eng.R > sel.R);
        Assert.True(eng.G > sel.G);
        Assert.True(eng.B > sel.B);
    }

    /// <summary>The P376 enemy-red and friendly-blue must still be distinct from every
    /// missile-lock state (grey / amber / hot-red), keeping the two coexisting boxes
    /// separable by colour — the P345 anti-aliasing contract now extended to the new
    /// side colours.</summary>
    [Fact]
    public void SideColours_Are_Distinct_From_Every_MissileLock_State()
    {
        var lockColours = new[]
        {
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.None),
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Partial),
            LockBracketStyle.ColorFor(MissileLockFeedback.LockState.Full),
        };
        Assert.DoesNotContain(TargetSelectionStyle.EnemySelected, lockColours);
        Assert.DoesNotContain(TargetSelectionStyle.EnemyEngaged, lockColours);
        Assert.DoesNotContain(TargetSelectionStyle.FriendSelected, lockColours);
        Assert.DoesNotContain(TargetSelectionStyle.FriendEngaged, lockColours);
    }
}
