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

    /// <summary>P1028 — per-match admission limits for result ingestion.
    /// Defence in depth against a buggy or compromised enrolled game server:
    /// native had no such surface (the club DB was fed by the operator-run
    /// server itself, FedSrv.CPP:1045-1140), so no native values exist to
    /// mirror — the caps are chosen well above anything a real Allegiance
    /// match can produce (see each constant's rationale).</summary>
    public static class Limits
    {
        /// <summary>A match cannot outlast a day — Allegiance games end on
        /// goals or server-side time limits well under three hours.</summary>
        public const int MaxMinutes = 1440;

        /// <summary>The rank ladder tops out at 1,000,000 career score
        /// (CareerRankTable); one match cannot award a tenth of that.</summary>
        public const float MaxScore = 100_000f;

        /// <summary>Per-match combat rating, same magnitude bound as the score.</summary>
        public const float MaxCombatRating = 100_000f;

        /// <summary>Kills/deaths/ejections/rescues/warps: with respawn cycles
        /// alone eating minutes, no pilot reaches a thousand in one sitting.</summary>
        public const int MaxKills = 1000;
        public const int MaxDeaths = 1000;
        public const int MaxEjections = 1000;
        public const int MaxRescues = 1000;
        public const int MaxWarpsSpotted = 1000;

        /// <summary>Bases, flags, artifacts and techs are map-limited: sectors
        /// hold a handful of bases and at most dozens of flaggable objects.</summary>
        public const int MaxBaseKills = 100;
        public const int MaxBaseCaptures = 100;
        public const int MaxFlags = 100;
        public const int MaxArtifacts = 100;
        public const int MaxTechsRecovered = 100;

        /// <summary>A sector holds at most hundreds of asteroids.</summary>
        public const int MaxAsteroidsSpotted = 10_000;
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
        bool ScoresCount,
        // P844/SIM-0526 — the remaining native CQPlayerResults columns
        // (fsmission.cpp:2969-2986), optional on the wire; the career store
        // currently persists the core subset and ignores these until its
        // schema grows columns for them.
        int Ejections = 0,
        int Rescues = 0,
        int Flags = 0,
        int Artifacts = 0,
        int TechsRecovered = 0,
        int WarpsSpotted = 0,
        int AsteroidsSpotted = 0,
        float TimePlayedSeconds = 0f)
    {
        /// <summary>P1028 — admission check for a server-reported result:
        /// <c>null</c> when every numeric field is within
        /// <see cref="Limits"/>, otherwise a short rejection reason naming the
        /// offending field (the caller turns this into a 4xx).</summary>
        public string? Validate()
        {
            if (!float.IsFinite(Score) || Score < 0 || Score > Limits.MaxScore)
                return $"score {Score} must be between 0 and {Limits.MaxScore}";
            if (!float.IsFinite(CombatRating) || CombatRating < 0 || CombatRating > Limits.MaxCombatRating)
                return $"combat rating {CombatRating} must be between 0 and {Limits.MaxCombatRating}";
            if (Minutes < 0) return "minutes must not be negative";
            if (Minutes > Limits.MaxMinutes) return $"minutes {Minutes} exceed the per-match limit of {Limits.MaxMinutes}";
            if (Kills < 0 || Kills > Limits.MaxKills) return $"kills {Kills} must be between 0 and {Limits.MaxKills}";
            if (Deaths < 0 || Deaths > Limits.MaxDeaths) return $"deaths {Deaths} must be between 0 and {Limits.MaxDeaths}";
            if (!float.IsFinite(BaseKills) || BaseKills < 0 || BaseKills > Limits.MaxBaseKills)
                return $"base kills {BaseKills} must be between 0 and {Limits.MaxBaseKills}";
            if (!float.IsFinite(BaseCaptures) || BaseCaptures < 0 || BaseCaptures > Limits.MaxBaseCaptures)
                return $"base captures {BaseCaptures} must be between 0 and {Limits.MaxBaseCaptures}";
            if (Ejections < 0 || Ejections > Limits.MaxEjections) return $"ejections {Ejections} must be between 0 and {Limits.MaxEjections}";
            if (Rescues < 0 || Rescues > Limits.MaxRescues) return $"rescues {Rescues} must be between 0 and {Limits.MaxRescues}";
            if (Flags < 0 || Flags > Limits.MaxFlags) return $"flags {Flags} must be between 0 and {Limits.MaxFlags}";
            if (Artifacts < 0 || Artifacts > Limits.MaxArtifacts) return $"artifacts {Artifacts} must be between 0 and {Limits.MaxArtifacts}";
            if (TechsRecovered < 0 || TechsRecovered > Limits.MaxTechsRecovered) return $"techs recovered {TechsRecovered} must be between 0 and {Limits.MaxTechsRecovered}";
            if (WarpsSpotted < 0 || WarpsSpotted > Limits.MaxWarpsSpotted) return $"warps spotted {WarpsSpotted} must be between 0 and {Limits.MaxWarpsSpotted}";
            if (AsteroidsSpotted < 0 || AsteroidsSpotted > Limits.MaxAsteroidsSpotted) return $"asteroids spotted {AsteroidsSpotted} must be between 0 and {Limits.MaxAsteroidsSpotted}";
            if (!float.IsFinite(TimePlayedSeconds) || TimePlayedSeconds < 0 || TimePlayedSeconds > Limits.MaxMinutes * 60)
                return $"time played {TimePlayedSeconds}s must be between 0 and {Limits.MaxMinutes * 60}";
            return null;
        }
    }

    /// <summary>Whole-match report: one <see cref="MatchResultEntry"/> per
    /// verified player who sat out that match. <c>MatchId</c> is the server's
    /// unique identifier for the finished match (anti-abuse: one result per
    /// player per match). <c>ServerId</c>/<c>ServerSecret</c> prove the report
    /// came from an enrolled (operator-approved) game server, mirroring the
    /// register payload (<c>LobbyEndpoints.cs:25-27</c>). <c>SquadGame</c> —
    /// P855/SIM-0765 — carries the finished squad game's result (native
    /// <c>RecordSquadGame</c>, FedSrv.CPP:1198-1300, gated at
    /// fsmission.cpp:3421-3427); absent for non-squad or non-qualifying games.</summary>
    public sealed record MatchReport(string MatchId, IReadOnlyList<MatchResultEntry> Players,
        string? ServerId = null, string? ServerSecret = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SquadGameResult? SquadGame = null,
        // P844/SIM-0524/0525 — the game-level row (native CQGameResults,
        // fsmission.cpp:2757-2836) and per-side rows (native CQTeamResults,
        // fsmission.cpp:2845-2915); absent (null) from older servers.
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] GameResult? Game = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TeamResult>? Teams = null);

    /// <summary>P844/SIM-0524 — one finished match's game-level result row
    /// (server mirror of the wire shape; see Allegiance.Server's
    /// LobbyGameResult). Persisted alongside the match id once the career
    /// store grows a games table; currently carried for wire compatibility.</summary>
    public sealed record GameResult(
        string? GameName,
        short WinnerSideId,
        string? WinnerName,
        float DurationSeconds,
        bool GoalConquest,
        bool GoalCountdown,
        bool GoalTeamKills,
        bool GoalProsperity,
        bool GoalArtifacts,
        bool GoalFlags,
        short GoalConquestPercentage,
        float GoalCountdownLength,
        short GoalTeamKillsCount,
        float GoalProsperityMoney,
        short GoalArtifactsCount,
        short GoalFlagsCount);

    /// <summary>P844/SIM-0525 — one finished side's outcome row (server mirror
    /// of the wire shape; see Allegiance.Server's LobbyTeamResult).</summary>
    public sealed record TeamResult(
        short TeamNumber,
        string Name,
        string? Faction,
        short Kills,
        short BaseKills,
        short BaseCaptures,
        short Deaths,
        short Ejections,
        short Flags,
        short Artifacts,
        byte ConquestPercent,
        byte TerritoryCount);

    /// <summary>P855/SIM-0765 — one finished squad game's standings facts: the
    /// winning side's squad id (native <c>squadIDWon</c>, may be NA/null when
    /// the winning side had no squad) and every losing side's squad id
    /// (native <c>squadIDLost1..5</c>; sides without a squad are skipped,
    /// FedSrv.CPP:1206-1290). The lobby credits the winner squad one win and
    /// each loser squad one loss.</summary>
    public sealed record SquadGameResult(string? WinnerSquadId, IReadOnlyList<string> LoserSquadIds);

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
