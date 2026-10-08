namespace Allegiance.ClientCore.State;

/// <summary>
/// P578 — the optional SECOND audio cue a feedback line carries on top of the
/// critical/non-critical message sound, mirroring native's handlers that play
/// an extra effect after <c>PostText</c>: the rescuer plays
/// <c>rescuePlayerSound</c> (appmsg.cpp:325) and the rescued pilot plays
/// <c>jumpSound</c> (appmsg.cpp:339). <see cref="None"/> = no extra cue.
/// </summary>
public enum FeedbackExtraSound
{
    None,
    RescuePlayer,   // hoover (native rescuePlayerSound)
    Jump,           // jumpSound
}

/// <summary>
/// P578 — a transient HUD feedback line, the port of native's
/// <c>PostText(bCritical, …)</c> → <c>SetCriticalTipText</c>/<c>SetTipText</c>
/// (trekigc.cpp:3067-3083). <see cref="IsCritical"/> selects the audio cue
/// (<c>criticalmessage</c> vs <c>noncriticalmessage</c>, sounddef.mdl) and the
/// HUD styling (native <c>CriticalTipText0</c> centred red vs the side-colour
/// <c>TipText0</c> lines). <see cref="AtSeconds"/> is the monotonic
/// <c>Game1._nowSeconds</c> stamp when the world produced it, so the renderer
/// can expire each line after native's tip duration (console.cpp:913 —
/// <c>fTipTextDuration = 7.5f</c>). Plain data, no engine types.
/// </summary>
public readonly record struct FeedbackLine(
    string Text,
    bool IsCritical,
    double AtSeconds,
    FeedbackExtraSound Extra = FeedbackExtraSound.None);

/// <summary>
/// P578 — a request to swing the camera to a ship for native's eject/kill
/// "gloat camera" duration. Mirrors <c>WinTrekClient::EjectPlayer(pcredit)</c>
/// → <c>OverrideCamera(now, pcredit, true)</c> (appmsg.cpp:2900-2902,
/// clintlib.cpp:3063, trekigc.cpp:3101-3105), whose override lasts
/// <c>now + 5.0f</c> (WinTrek.cpp:6501 — <c>bOverridePosition</c>). The client
/// resolves <see cref="FocusShipId"/> to a live world position when it drains
/// the request. One-shot: drained (cleared) by <c>Game1</c> exactly once.
/// </summary>
public readonly record struct CameraOverrideRequest(short FocusShipId, double AtSeconds);
