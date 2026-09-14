using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// Client-side mirror of <c>Allegiance.Lobby</c>'s public <c>ServerListingDto</c>,
/// spoken over plain HTTP as JSON (camelCase — the default for the ASP.NET Core
/// serializer the Lobby returns, and matched case-insensitively here).
/// Deliberately NOT a reference to the Web-SDK <c>Allegiance.Lobby</c> assembly:
/// pulling that in would drag the AspNetCore shared framework into this client
/// (the same reason <c>Allegiance.Server</c> mirrors the register response
/// locally). Only the public, read-only read projection is mirrored — the Lobby's
/// internal <c>ServerListing</c> (with its ownership token) is never returned over
/// the wire, so there is nothing secret to leak here.
/// </summary>
public sealed record ServerListing(
    string Id,
    string Name,
    string Address,
    int Port,
    int PlayerCount,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc)
{
    /// <summary>The default lobby-directory URL the client browses when none is
    /// given. This is the lobby's real default listen address — confirmed by
    /// launching <c>dotnet run --project src/Allegiance.Lobby</c> (it has no
    /// appsettings/launchSettings, so Kestrel binds its default port 5000). Not a
    /// guessed port; override with <c>--lobby &lt;url&gt;</c>.</summary>
    public const string LobbyUrlDefault = "http://127.0.0.1:5000";

    /// <summary>Case-insensitive web-JSON binding used for the lobby GET (the
    /// Lobby serializes camelCase; this accepts any casing, like the server's
    /// own case-insensitive binding).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>A usable single server-row line: name, address:port, player
    /// count, mission — the columns the browser is asked to show. Lives here
    /// (rather than in a renderer) so it is pure string logic, unit-testable
    /// without a graphics device.</summary>
    public string FormatRow()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
        var addr = (string.IsNullOrWhiteSpace(Address) ? "?" : Address)
                   + ":" + (Port > 0 ? Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?");
        var players = MaxPlayers > 0
            ? $"{PlayerCount}/{MaxPlayers}"
            : PlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mission = string.IsNullOrWhiteSpace(MissionName) ? "-" : MissionName;
        return $"{name}  {addr}   {players} players   {mission}";
    }
}
