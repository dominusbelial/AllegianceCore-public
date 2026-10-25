namespace Allegiance.Lobby.Career;

/// <summary>
/// Lobby career-statistics wire contracts (P808b). These mirror the native
/// club messages the client screens consume — <c>CHARACTER_INFO_*</c>
/// (<c>Inc/ClubMessages.h:67-152</c>) and <c>LEADER_BOARD_*</c>
/// (<c>Inc/ClubMessages.h:320-360</c>) — delivered over HTTP instead of the
/// native Club sockets. Field names/meaning match the native messages so the
/// port screens render the same data.
/// </summary>
public static class CareerContracts
{
    /// <summary>Native <c>CivID NA</c> (−1): "any civ / sum of all civs"
    /// (<c>Inc/ClubMessages.h:79</c>).</summary>
    public const short AllCivs = -1;

    /// <summary>Native <c>LeaderBoardSort</c> (<c>Inc/ClubMessages.h:303-318</c>).</summary>
    public enum LeaderBoardSort
    {
        Name = 0,
        Rank,
        Rating,
        Score,
        TimePlayed,
        BaseKills,
        BaseCaptures,
        Kills,
        GamesPlayed,
        Deaths,
        Wins,
        Losses,
        CommanderWins,
    }

    /// <summary>One verified player's end-of-match result, reported by the game
    /// server (the port of native <c>SetCharStats</c>' per-game
    /// <c>PlayerScoreObject</c>, <c>FedSrv/FedSrv.CPP:1045-1140</c>).</summary>
    public sealed record MatchResultEntry(
        string AccountId,
        short Civ,
        float Score,
        float CombatRating,
        int Kills,
        int Deaths,
        float BaseKills,
        float BaseCaptures,
        int Minutes,
        bool Win,
        bool Loss,
        bool CommanderWin,
        bool CommanderLoss,
        bool ScoresCount);

    /// <summary>Whole-match report: one <see cref="MatchResultEntry"/> per
    /// verified player who sat out that match. <c>MatchId</c> is the server's
    /// unique identifier for the finished match (anti-abuse: one result per
    /// player per match). <c>ServerId</c>/<c>ServerSecret</c> prove the report
    /// came from an enrolled (operator-approved) game server, mirroring the
    /// register payload (<c>LobbyEndpoints.cs:25-27</c>).</summary>
    public sealed record MatchReport(string MatchId, IReadOnlyList<MatchResultEntry> Players, string? ServerId = null, string? ServerSecret = null);

    /// <summary>Result of ingesting one <see cref="MatchReport"/>.</summary>
    public sealed record MatchReportOutcome(int Applied, int Duplicates, int Skipped);

    /// <summary>One civilization's career line (native
    /// <c>CHARACTER_INFO_BY_CIV</c>, <c>Inc/ClubMessages.h:77-91</c>).</summary>
    public sealed record CareerCivStats(
        short Civ,
        short Rank,
        float Score,
        int BaseCaptures,
        int BaseKills,
        int Rating,
        int MinutesPlayed,
        int Kills,
        int Deaths,
        int Wins,
        int Losses,
        int? PromotionAt);

    /// <summary>A player's full character info (native
    /// <c>CHARACTER_INFO_GENERAL</c> + per-civ lines).</summary>
    public sealed record CareerInfo(
        string AccountId,
        string Callsign,
        string Description,
        int Rating,
        IReadOnlyList<CareerCivStats> Civs,
        // The "all civilizations" rank: native ranks the summed line like any other
        // (civid -1 = sum of all civs, Inc/ClubMessages.h:79; FedSrv.CPP:1070-1078).
        short AllCivsRank = 0);

    /// <summary>One leaderboard row (native <c>LeaderBoardEntry</c>,
    /// <c>Inc/ClubMessages.h:329-350</c>).</summary>
    public sealed record LeaderboardEntry(
        string AccountId,
        string Callsign,
        int Position,
        float TotalScore,
        int MinutesPlayed,
        int BaseKills,
        int BaseCaptures,
        int Kills,
        int Deaths,
        short Rank,
        short Rating,
        int Wins,
        int Losses,
        int GamesPlayed,
        int CommanderWins,
        int CommanderLosses);

    /// <summary>Leaderboard query parameters (native <c>LEADER_BOARD_QUERY</c>,
    /// <c>Inc/ClubMessages.h:320-327</c>): <c>civ</c>, <c>sort</c> and a
    /// <c>basis</c> (a character name, or account id) to center the returned
    /// window on — <c>-1</c>/<c>NA</c> meaning "top of the list".</summary>
    public sealed record LeaderboardQuery(
        short Civ,
        LeaderBoardSort Sort,
        string? Basis,
        string? BasisAccountId);

    /// <summary>Leaderboard response (native <c>LEADER_BOARD_LIST</c>,
    /// <c>Inc/ClubMessages.h:356-360</c>): a window of up to
    /// <c>cLinesPerScreen·3</c> (84) entries plus the #1 entry and the basis's
    /// position so the client can draw the top-row and page around the basis.
    /// <c>FailReason</c> mirrors native's <c>LEADER_BOARD_QUERY_FAIL</c>
    /// (<c>Inc/ClubMessages.h:352-354</c>, handled at
    /// <c>WinTrek/leaderboardscreen.cpp:763-781</c>): null when the query
    /// succeeded, "no players" when no characters exist for the chosen civ,
    /// or the unfound basis character name.</summary>
    public sealed record LeaderboardResponse(
        IReadOnlyList<LeaderboardEntry> Entries,
        LeaderboardEntry? EntryNumber1,
        int? BasisPosition,
        string? FailReason);
}
