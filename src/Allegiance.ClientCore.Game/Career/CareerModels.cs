using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Local, wire-shape mirrors of the lobby's career-statistics contracts
/// (<c>Allegiance.Lobby.Career.CareerContracts</c>), so the MonoGame client can
/// fetch character info and the leaderboard over the lobby's HTTP API without
/// referencing the (Web-SDK) Lobby assembly — the same local-mirror discipline
/// the server uses for its register payload. Field names/meaning match the
/// native club messages (<c>Inc/ClubMessages.h:77-91,329-360</c>).
/// </summary>
public static class CareerDto
{
    /// <summary>Native <c>LeaderBoardSort</c> (<c>Inc/ClubMessages.h:303-318</c>).</summary>
    public enum LeaderBoardSort
    {
        Name = 0, Rank, Rating, Score, TimePlayed,
        BaseKills, BaseCaptures, Kills, GamesPlayed, Deaths,
        Wins, Losses, CommanderWins,
    }

    public const short AllCivs = -1;

    public sealed record CareerCivStats(
        short Civ, short Rank, float Score, int BaseCaptures, int BaseKills,
        int Rating, int MinutesPlayed, int Kills, int Deaths, int Wins, int Losses, int? PromotionAt);

    public sealed record CareerInfo(
        string AccountId, string Callsign, string Description, int Rating,
        IReadOnlyList<CareerCivStats> Civs, short AllCivsRank = 0);

    public sealed record LeaderboardEntry(
        string AccountId, string Callsign, int Position, float TotalScore, int MinutesPlayed,
        int BaseKills, int BaseCaptures, int Kills, int Deaths, short Rank, short Rating,
        int Wins, int Losses, int GamesPlayed, int CommanderWins, int CommanderLosses);

    public sealed record LeaderboardResponse(
        IReadOnlyList<LeaderboardEntry> Entries, LeaderboardEntry? EntryNumber1,
        int? BasisPosition, string? FailReason);

    /// <summary>Profile-description edit body (native <c>CHARACTER_INFO_EDIT_DESCRIPTION</c>).</summary>
    public sealed record DescriptionRequest(string Description);
}

/// <summary>
/// Client-side rank-name lookup for the career screens, mirroring the lobby's
/// <c>CareerRankTable</c> ladder (which mirrors native's standalone rank table file /
/// club <c>Ranks</c> table). Both screens paint rank names via native's
/// <c>BaseClient::LookupRankName</c> clamp-to-top semantics
/// (<c>clintlib.cpp:2670-2710</c>).
/// </summary>
public static class CareerRanks
{
    private static readonly string[] Names =
    {
        "Recruit", "Cadet", "Ensign", "Lieutenant Junior Grade", "Lieutenant",
        "Lieutenant Commander", "Commander", "Captain", "Commodore", "Rear Admiral",
        "Vice Admiral", "Admiral", "Fleet Admiral", "Grand Admiral",
        "Master 1", "Master 2", "Master 3", "Master 4", "Master 5",
        "Master 6", "Master 7", "Master 8", "Master 9",
    };
    public static string Name(short rank) => rank <= 0 ? "" : Names[Math.Min(rank, Names.Length - 1)];
}

/// <summary>Source-generated JSON contract for the career DTOs (repo-wide
/// no-reflection-serialization discipline).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CareerDto.CareerInfo))]
[JsonSerializable(typeof(CareerDto.CareerCivStats))]
[JsonSerializable(typeof(CareerDto.LeaderboardResponse))]
[JsonSerializable(typeof(CareerDto.LeaderboardEntry))]
[JsonSerializable(typeof(CareerDto.LeaderBoardSort))]
[JsonSerializable(typeof(CareerDto.DescriptionRequest))]
internal sealed partial class CareerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.CareerInfo> Info =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.CareerInfo>)Default.GetTypeInfo(typeof(CareerDto.CareerInfo))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.LeaderboardResponse> Board =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.LeaderboardResponse>)Default.GetTypeInfo(typeof(CareerDto.LeaderboardResponse))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.DescriptionRequest> Desc =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<CareerDto.DescriptionRequest>)Default.GetTypeInfo(typeof(CareerDto.DescriptionRequest))!;
}
