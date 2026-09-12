namespace Allegiance.Core.Wire;

/// <summary>
/// The <c>QuitSideReason</c> enum carried by <c>QUIT_MISSION</c>(124) and
/// <c>QUIT_SIDE</c>(126), <c>Messages.h:787-808</c>. Only the two values this
/// port's own logic can actually produce are modeled here as named members:
/// <see cref="LinkDead"/> (a disconnect via the <c>ReleasePeer</c> path) and
/// <see cref="SwitchingSides"/> (the internal quit-then-join half of the
/// side-switch capability). The remaining ~15 native values
/// (<c>QSR_LeaderBooted</c>…<c>QSR_BannedBySteam</c>) have no triggering logic
/// in this port yet (no boot/kick/rank-limits/squad concepts), so they are
/// intentionally absent as named members — a deliberate simplification, not a
/// silent omission. The raw integer values here match native exactly, and the
/// codecs encode/decode the reason as the underlying C++ <c>int</c> enum, so
/// any out-of-range value round-trips correctly if ever received.
/// </summary>
public enum QuitSideReason
{
    /// <summary><c>QSR_LinkDead</c> — the only disconnect-path reason this
    /// port can reach (a peer dropped via <c>ReleasePeer</c>).</summary>
    LinkDead = 9,

    /// <summary><c>QSR_SwitchingSides</c> — emitted on the quit-half of the
    /// side-switch capability, when a player leaves one side to join another
    /// within the same mission.</summary>
    SwitchingSides = 13,
}
