namespace Allegiance.Core.Wire;

/// <summary>
/// The <c>DelPositionReqReason</c> enum carried by <c>DELPOSITIONREQ</c>(42),
/// <c>Messages.h:338-364</c>. All 24 native values are modeled so the wire
/// codec can name whatever reason round-trips, but this port's own logic only
/// ever <em>sends</em> three of them: <see cref="Rejected"/> (a leader rejects,
/// or a pending request is dropped on disconnect), <see cref="Canceled"/> (the
/// requester withdraws their own request), and <see cref="SideGone"/> (the
/// requested side no longer exists). The raw integer values match native
/// exactly, and the codec encodes/decodes the reason as the underlying C++
/// <c>int</c> enum, so any out-of-range value round-trips correctly if ever
/// received.
/// </summary>
public enum DelPositionReqReason
{
    /// <summary><c>DPR_Rejected</c> — a leader declined a pending request (or
    /// the requester's pending request was dropped, e.g. on disconnect).</summary>
    Rejected = 0,

    /// <summary><c>DPR_Canceled</c> — the requester withdrew their own request.</summary>
    Canceled = 1,

    /// <summary><c>DPR_NoBase</c> — the side has no base to defend (native only).</summary>
    NoBase = 2,

    /// <summary><c>DPR_TeamFull</c> — the side is at its player cap (native only).</summary>
    TeamFull = 3,

    /// <summary><c>DPR_TeamBalance</c> — joining would imbalance the teams (native only).</summary>
    TeamBalance = 4,

    /// <summary><c>DPR_NoMission</c> — no mission to join (native only).</summary>
    NoMission = 5,

    /// <summary><c>DPR_BadPassword</c> — wrong mission password (native only).</summary>
    BadPassword = 6,

    /// <summary><c>DPR_LobbyLocked</c> — the lobby is locked (native only).</summary>
    LobbyLocked = 7,

    /// <summary><c>DPR_InvalidRank</c> — rank gate failed (native only).</summary>
    InvalidRank = 8,

    /// <summary><c>DPR_NoJoiners</c> — no joiners (native only).</summary>
    NoJoiners = 9,

    /// <summary><c>DPR_NoDefections</c> — defections disabled (native only).</summary>
    NoDefections = 10,

    /// <summary><c>DPR_WrongSquad</c> — squad membership gate (native only).</summary>
    WrongSquad = 11,

    /// <summary><c>DPR_OutOfLives</c> — no lives left (native only).</summary>
    OutOfLives = 12,

    /// <summary><c>DPR_Banned</c> — the player is banned from the side (native only).</summary>
    Banned = 13,

    /// <summary><c>DPR_SideGone</c> — the requested side no longer exists. One of
    /// the three this port actually sends (FedSrv.CPP:6700).</summary>
    SideGone = 14,

    /// <summary><c>DPR_GameFull</c> — the whole game is full (native only).</summary>
    GameFull = 15,

    /// <summary><c>DPR_PrivateGame</c> — private/invite-only game (native only).</summary>
    PrivateGame = 16,

    /// <summary><c>DPR_SidesLocked</c> — side changes locked (native only).</summary>
    SidesLocked = 17,

    /// <summary><c>DPR_SideDefeated</c> — the side is defeated (native only).</summary>
    SideDefeated = 18,

    /// <summary><c>DPR_CantLeadSquad</c> — cannot lead a squad (native only).</summary>
    CantLeadSquad = 19,

    /// <summary><c>DPR_ServerPaused</c> — the server is paused (native only).</summary>
    ServerPaused = 20,

    /// <summary><c>DPR_DuplicateLogin</c> — duplicate login (native only).</summary>
    DuplicateLogin = 21,

    /// <summary><c>DPR_Other</c> — unspecified (native only).</summary>
    Other = 22,

    /// <summary><c>DPR_BotsOnTeam0Only</c> — bots-only team 0 (native only).</summary>
    BotsOnTeam0Only = 23,
}
