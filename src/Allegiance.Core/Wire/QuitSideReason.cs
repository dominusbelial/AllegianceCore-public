namespace Allegiance.Core.Wire;

/// <summary>
/// The <c>QuitSideReason</c> enum carried by <c>QUIT_MISSION</c>(124) and
/// <c>QUIT_SIDE</c>(126), <c>Messages.h:787-808</c>. Values are the native
/// <c>QSR_*</c> integers exactly. Only a few of these have triggering logic in
/// this port (leader boot, self quit, link-dead, side-switch); the rest are
/// named here so the client can render any reason the wire may carry with the
/// native text (<c>trekigc.cpp:5265-5335</c>) instead of falling into a raw
/// integer. The codecs encode/decode the reason as the underlying C++
/// <c>int</c> enum, so every value round-trips.
/// </summary>
public enum QuitSideReason
{
    /// <summary>Native Messages.h:789; a leader boots a teammate.</summary>
    LeaderBooted = 0,
    /// <summary>Native Messages.h:790; the mission owner lobby-boots a player.</summary>
    OwnerBooted = 1,
    /// <summary>Native Messages.h:791; a Steam moderator/developer bans a player.</summary>
    AdminBooted = 2,
    /// <summary>Native Messages.h:792; an administrator shuts the game down.</summary>
    ServerShutdown = 3,
    /// <summary>Native Messages.h:793; a squad change boots a player.</summary>
    SquadChange = 4,
    /// <summary>Native Messages.h:794; the player's side was destroyed.</summary>
    SideDestroyed = 5,
    /// <summary>Native Messages.h:795; rank-limit change boots a player.</summary>
    RankLimits = 6,
    /// <summary>Native Messages.h:796; team-size-limit change boots a player.</summary>
    TeamSizeLimits = 7,

    /// <summary>Native Messages.h:797; a player voluntarily leaves.</summary>
    Quit = 8,
    /// <summary><c>QSR_LinkDead</c> — the only disconnect-path reason this
    /// port can reach (a peer dropped via <c>ReleasePeer</c>).</summary>
    LinkDead = 9,
    /// <summary>Native Messages.h:799; the same zone account logged in elsewhere.</summary>
    DuplicateRemoteLogon = 10,
    /// <summary>Native Messages.h:800; the same zone account logged into this game.</summary>
    DuplicateLocalLogon = 11,
    /// <summary>Native Messages.h:801; the same CD key logged in elsewhere.</summary>
    DuplicateCDKey = 12,

    /// <summary><c>QSR_SwitchingSides</c> — emitted on the quit-half of the
    /// side-switch capability, when a player leaves one side to join another
    /// within the same mission.</summary>
    SwitchingSides = 13,
    /// <summary>Native Messages.h:804; sides were randomized under the player.</summary>
    RandomizeSides = 14,
    /// <summary>Native Messages.h:805; sides were flushed for balance.</summary>
    FlushSides = 15,
    /// <summary>Native Messages.h:806; sides were rebalanced.</summary>
    BalanceSides = 16,
    /// <summary>Native Messages.h:807; a Steam ban (message carried on the wire).</summary>
    BannedBySteam = 17,
}
