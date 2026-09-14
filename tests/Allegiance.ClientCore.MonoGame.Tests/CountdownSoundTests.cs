using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="CountdownSound"/> — the "which countdown
/// asset for N seconds remaining" mapping behind P216's mission-start countdown
/// announcer. No graphics/audio device involved; the mapping is deliberately
/// free of MonoGame types.
/// </summary>
public class CountdownSoundTests
{
    [Fact]
    public void BriefingCountdownMatchesThePortsConstant()
    {
        // The client supplies the countdown length locally (the SET_START_TIME
        // wire carries only the target start time), and it must agree with the
        // server's real BriefingCountdownSeconds (Server.cs:129), itself a port
        // of native c_fMissionBriefingCountdown = 15.0f (igc.h:1005). A mismatch
        // would desync the announcer's timing from the real launch.
        Assert.Equal(15.0f, CountdownSound.BriefingCountdownSeconds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    public void TickAssetFor_MapsEachAudibleSecondToItsVoice(int second)
    {
        Assert.Equal($"countdown{second}", CountdownSound.TickAssetFor(second));
    }

    [Theory]
    [InlineData(0)]    // launch has passed / nothing left
    [InlineData(-1)]   // negative = already launched
    [InlineData(11)]   // >10: early briefing, no per-second voice (only the ≥30s
                      // native checkpoints exist up there, unreachable at 15s)
    [InlineData(1000)] // far in the future — no dedicated cue
    public void TickAssetFor_ReturnsNullOutsideTheAudibleTail(int second)
    {
        Assert.Null(CountdownSound.TickAssetFor(second));
    }

    [Fact]
    public void TickAssetFor_CoversExactlyTenToOne()
    {
        // The audible tail is exactly native's case 10…case 1 (WinTrek.cpp:11172-11210):
        // one asset per integer second from 10 down to 1, nothing else.
        var audible = Enumerable.Range(-5, 40)
            .Select(CountdownSound.TickAssetFor)
            .Where(a => a is not null)
            .Select(a => int.Parse(a![9..]))
            .OrderByDescending(x => x)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 10).Reverse(), audible);
    }
}
