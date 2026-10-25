using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Local, wire-shape mirrors of the lobby's squad contracts
/// (<c>Allegiance.Lobby.Squad.SquadContracts</c>) so the MonoGame client can
/// drive the Squads screen over the lobby HTTP API without referencing the
/// (Web-SDK) Lobby assembly — the same local-mirror discipline the career
/// screens use. Field names/meaning match the native club squad messages
/// (<c>Inc/ClubMessages.h:129-301</c>).
/// </summary>
public static class SquadDto
{
    public enum SquadRole { Pending = 0, Member = 1, AssistantLeader = 2, Leader = 3 }

    public enum SquadSortColumn { Unknown = 0, Name = 1, Score = 2, Wins = 3, Losses = 4, Hours = 5 }

    public sealed record SquadSummary(
        string Id, string Name, string Tag, short Civ, int Wins, int Losses, int Score,
        int Ranking, int MemberCount, SquadRole? ViewerRole = null, string? LeaderCallsign = null);

    public sealed record SquadDetails(
        string Id, string Name, string Tag, string Description, string Url, short Civ,
        long CreatedAt, string LeaderAccountId, IReadOnlyList<SquadMember> Members);

    public sealed record SquadMember(string AccountId, string Callsign, SquadRole Role, long? LastPlayedAt, long JoinedAt);

    public sealed record SquadLogEntry(long Seq, string Line);

    public sealed record CreateSquadRequest(string Name, string Tag, string Description, string Url, short Civ);
    public sealed record EditSquadRequest(string Description, string Url, short Civ);
    public sealed record SquadActionResult(bool Ok, string? Error = null);
}

/// <summary>Source-generated JSON contract for the squad DTOs (repo-wide
/// no-reflection-serialization discipline).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SquadDto.SquadSummary))]
[JsonSerializable(typeof(SquadDto.SquadDetails))]
[JsonSerializable(typeof(SquadDto.SquadMember))]
[JsonSerializable(typeof(SquadDto.SquadLogEntry))]
[JsonSerializable(typeof(SquadDto.SquadRole))]
[JsonSerializable(typeof(SquadDto.SquadSortColumn))]
[JsonSerializable(typeof(SquadDto.CreateSquadRequest))]
[JsonSerializable(typeof(SquadDto.EditSquadRequest))]
[JsonSerializable(typeof(SquadDto.SquadActionResult))]
[JsonSerializable(typeof(SquadDto.SquadSummary[]))]
[JsonSerializable(typeof(SquadDto.SquadLogEntry[]))]
internal sealed partial class SquadJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadSummary[]> SummaryArray =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadSummary[]>)Default.GetTypeInfo(typeof(SquadDto.SquadSummary[]))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadDetails> Details =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadDetails>)Default.GetTypeInfo(typeof(SquadDto.SquadDetails))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadLogEntry[]> LogArray =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadLogEntry[]>)Default.GetTypeInfo(typeof(SquadDto.SquadLogEntry[]))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.CreateSquadRequest> Create =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.CreateSquadRequest>)Default.GetTypeInfo(typeof(SquadDto.CreateSquadRequest))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.EditSquadRequest> Edit =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.EditSquadRequest>)Default.GetTypeInfo(typeof(SquadDto.EditSquadRequest))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadActionResult> Action =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SquadDto.SquadActionResult>)Default.GetTypeInfo(typeof(SquadDto.SquadActionResult))!;
}
