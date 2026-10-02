namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P216 — pure logic behind the mission-start countdown announcer: which real
/// countdown asset to play for a given "seconds remaining" value. Deliberately
/// free of any MonoGame type (same discipline as <see cref="EngineSoundCurve"/>
/// and <see cref="Flight.FlightInput"/>), so the mapping is unit-testable
/// without a graphics/audio device. The <b>timing</b> (tracking elapsed seconds
/// and firing each tick exactly once) lives in <see cref="Game1"/>, which owns
/// the real per-frame mission-stage read; only the pure "which asset" question
/// is extracted here.
/// </summary>
/// <remarks>
/// <para><b>Origin.</b> Native <c>CheckCountdownSound</c>
/// (<c>WinTrek/WinTrek.cpp:11133-11211</c>) plays a callout whenever the
/// integer seconds-remaining changes, dispatching a per-value sound: coarse
/// checkpoints at 10min / 5min / 1min / 30s, then a per-second voice
/// (<c>countdown10</c> … <c>countdown1</c>) for the last ten seconds. The
/// checkpoints are all ≥30s, so none of them can occur inside this port's
/// 15-second briefing countdown (see <see cref="BriefingCountdownSeconds"/>);
/// only the sub-60-second per-second tail (10→1) is reachable here. That is
/// exactly the <c>switch</c>'s tail (<c>WinTrek.cpp:11172-11210</c>), ported as
/// a pure range check rather than a 10-case switch.</para>
/// <para><b>Asset stems.</b> The per-second cues are the real files
/// <c>countdown10.ogg</c> … <c>countdown1.ogg</c> in
/// <c>/home/domingo/Development/Artwork</c> (the port's
/// <c>ALLEGIANCE_ARTWORK_PATH</c>), loaded via MonoGame content as
/// <c>Sounds/countdown10</c> … <c>Sounds/countdown1</c>. The countdown-start
/// callout is <c>countdownstart.ogg</c> — P216's decision to voice the very
/// first moments rather than leave the first ~4s of the 15s briefing silent
/// (native has no "start" cue; it opens with the 10min checkpoint, which is
/// unreachable here, so the silence gap would be pure dead air in this port).</para>
/// </remarks>
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

    /// <summary>
    /// Map an integer "seconds remaining" (1-based — how many seconds are still
    /// left before launch) to the content asset stem for the dedicated per-second
    /// tick, or <c>null</c> when that second has no per-second voice. Seconds in
    /// <c>1..<see cref="MaxTickSecond"/></c> map to <c>countdown{N}</c>; anything
    /// else (≤0 after launch, or &gt;10 during the quiet early briefing) has no
    /// per-second cue. Mirror of native's <c>case 10 … case 1</c> tail
    /// (<c>WinTrek.cpp:11172-11210</c>), expressed as a range because the coarse
    /// ≥30s checkpoints are unreachable at this port's 15s countdown.
    /// </summary>
    public static string? TickAssetFor(int secondsRemaining)
    {
        if (secondsRemaining >= 1 && secondsRemaining <= MaxTickSecond)
            return $"countdown{secondsRemaining}";
        return null;
    }
}
