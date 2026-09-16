using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P230 — pure-unit proof of <see cref="MissileLockFeedback"/> (the lock-state
/// classifier + edge-triggered tip rules ported from native
/// <c>WinTrek/console.cpp:929-946</c>). No networking/MonoGame: just the 
/// classification and the exact crossing conditions, so the three messages can
/// be shown to fire once per transition and never while a state is held.
/// </summary>
public class MissileLockFeedbackTests
{
    [Theory]
    [InlineData(0f, MissileLockFeedback.LockState.None)]
    [InlineData(0.005f, MissileLockFeedback.LockState.Partial)]
    [InlineData(0.5f, MissileLockFeedback.LockState.Partial)]
    [InlineData(0.999f, MissileLockFeedback.LockState.Partial)]
    [InlineData(1f, MissileLockFeedback.LockState.Full)]
    [InlineData(1.4f, MissileLockFeedback.LockState.Full)]
    public void Classify_Maps_LockValue_To_DisplayState(float value, MissileLockFeedback.LockState expected)
        => Assert.Equal(expected, MissileLockFeedback.Classify(value));

    [Fact]
    public void Entering_Partial_From_None_Posts_Partial_Tip()
    {
        var tip = MissileLockFeedback.TipFor(
            MissileLockFeedback.LockState.None,
            MissileLockFeedback.LockState.Partial,
            targetChanged: false, missileName: "Dumbo");
        Assert.Equal("Partial missile lock - center the target on screen to improve the lock.", tip);
    }

    [Fact]
    public void Entering_Full_Posts_Acquired_Tip_With_MissileName()
    {
        var tip = MissileLockFeedback.TipFor(
            MissileLockFeedback.LockState.Partial,
            MissileLockFeedback.LockState.Full,
            targetChanged: false, missileName: "Hunter");
        Assert.Equal("Missile lock acquired - press Left Ctrl to fire a Hunter missile at the target.", tip);
    }

    [Fact]
    public void Dropping_To_None_Same_Target_Posts_Lost_Tip()
    {
        var tip = MissileLockFeedback.TipFor(
            MissileLockFeedback.LockState.Full,
            MissileLockFeedback.LockState.None,
            targetChanged: false, missileName: "x");
        Assert.Equal("Missile lock lost.", tip);
    }

    [Fact]
    public void Dropping_To_None_On_Target_Change_Suppresses_Lost()
    {
        // Retargeting resets lock to 0 — native suppresses "lost" via m_fTargetChanged
        // so switching targets doesn't nag. The message must NOT be posted.
        var tip = MissileLockFeedback.TipFor(
            MissileLockFeedback.LockState.Full,
            MissileLockFeedback.LockState.None,
            targetChanged: true, missileName: "x");
        Assert.Null(tip);
    }

    [Fact]
    public void Holding_A_State_Posts_Nothing()
    {
        Assert.Null(MissileLockFeedback.TipFor(MissileLockFeedback.LockState.None, MissileLockFeedback.LockState.None, false, "x"));
        Assert.Null(MissileLockFeedback.TipFor(MissileLockFeedback.LockState.Partial, MissileLockFeedback.LockState.Partial, false, "x"));
        Assert.Null(MissileLockFeedback.TipFor(MissileLockFeedback.LockState.Full, MissileLockFeedback.LockState.Full, false, "x"));
    }

    [Fact]
    public void Partial_Into_Full_Does_Not_Repost_Partial()
    {
        // Crossing partial->full posts ONLY "acquired", not "partial" again.
        var tip = MissileLockFeedback.TipFor(MissileLockFeedback.LockState.Partial, MissileLockFeedback.LockState.Full, false, "Hunter");
        Assert.StartsWith("Missile lock acquired", tip);
    }
}
