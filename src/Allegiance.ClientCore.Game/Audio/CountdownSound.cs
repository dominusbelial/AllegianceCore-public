namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Native countdown wave selection (WinTrek.cpp:11172-11210).
/// Both mission start and timed-game expiry use this tail; the clock and
/// edge detection live in MissionCountdownSound. Coarse checkpoints remain
/// separate parity rows.</summary>
public static class CountdownSound
{
    /// <summary>
    /// The briefing countdown length in seconds. Port of native's
    /// <c>c_fMissionBriefingCountdown = 15.0f</c> (<c>src/Igc/igc.h:1005</c>);
    /// the port's <c>Allegiance.Server</c> uses the same value as
    /// <c>BriefingCountdownSeconds = 15.0f</c> (<c>Server.cs:129</c>). The wire's
    /// <c>SET_START_TIME(14)</c> carries only the target start time (server-clock
    /// ms), not the length, so — exactly like native's client, which uses its own
    /// compile-time <c>c_fMissionBriefingCountdown</c> — the client supplies the
    /// countdown length locally.</summary>
    public const float BriefingCountdownSeconds = 15.0f;

    /// <summary>Highest integer "seconds remaining" that has a dedicated
    /// per-second tick asset (<c>countdown10</c> … <c>countdown1</c>).</summary>
    public const int MaxTickSecond = 10;

    /// <summary>Map an integer "seconds remaining" (1-based — how many seconds are still
    /// left before launch) to the content asset stem for the dedicated per-second
    /// tick, or <c>null</c> when that second has no per-second voice. Seconds in
    /// <c>1..<see cref="MaxTickSecond"/></c> map to <c>countdown{N}</c>; anything
    /// else (≤0 after launch, or &gt;10 during the quiet early briefing) has no
    /// per-second cue. Mirror of native's <c>case 10 … case 1</c> tail
    /// (<c>WinTrek.cpp:11172-11210</c>).
    /// </summary>
    public static string? TickAssetFor(int secondsRemaining)
    {
        if (secondsRemaining >= 1 && secondsRemaining <= MaxTickSecond)
            return $"countdown{secondsRemaining}";
        return null;
    }

    /// <summary>
    /// Map a "seconds remaining" value to the coarse mission-clock milestone
    /// VO asset native announces ahead of the per-second tail
    /// (<c>CheckCountdownSound</c>, <c>WinTrek.cpp:11157-11169</c>): 10 min →
    /// <c>vo_time_10min</c>, 5 min → <c>vo_time_5min</c>, 1 min →
    /// <c>vo_time_1min</c>, 30 s → <c>vo_time_30sec</c>. These are SIM-0300..0303.
    /// </summary>
    public static string? MilestoneAssetFor(int secondsRemaining) => secondsRemaining switch
    {
        10 * 60 => "vo_time_10min",
        5 * 60 => "vo_time_5min",
        1 * 60 => "vo_time_1min",
        30 => "vo_time_30sec",
        _ => null,
    };
}
