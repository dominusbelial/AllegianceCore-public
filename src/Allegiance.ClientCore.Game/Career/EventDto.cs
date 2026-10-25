using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Local, wire-shape mirrors of the lobby's zone-events contracts
/// (<c>Allegiance.Lobby.Events.EventContracts</c>) so the MonoGame client can
/// drive the Events screen over the lobby HTTP API without referencing the
/// (Web-SDK) Lobby assembly — the same local-mirror discipline the career and
/// squad screens use. Field names/meaning match the native zone-events feed
/// (<c>WinTrek/zoneeventsscreen.cpp</c> <c>EventListing</c>): date, time,
/// name, description, sign-up URL and the attached-file list.
/// </summary>
public static class EventDto
{
    public sealed record ZoneEvent(
        string Id,
        string Date,
        string Time,
        string Name,
        string Description,
        string SignUpUrl,
        IReadOnlyList<string> Files,
        long CreatedAt);

    /// <summary>The lobby's configurable Zone web link (<c>GET /api/config/zone-web</c>).</summary>
    public sealed record ZoneWeb(string Url);
}

/// <summary>Source-generated JSON contract for the event DTOs (repo-wide
/// no-reflection-serialization discipline).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EventDto.ZoneEvent))]
[JsonSerializable(typeof(EventDto.ZoneWeb))]
[JsonSerializable(typeof(EventDto.ZoneEvent[]))]
internal sealed partial class EventJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<EventDto.ZoneEvent[]> EventArray =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<EventDto.ZoneEvent[]>)Default.GetTypeInfo(typeof(EventDto.ZoneEvent[]))!;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<EventDto.ZoneWeb> ZoneWebInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<EventDto.ZoneWeb>)Default.GetTypeInfo(typeof(EventDto.ZoneWeb))!;
}
