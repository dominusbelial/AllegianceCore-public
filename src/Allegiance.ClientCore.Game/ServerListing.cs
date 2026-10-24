using System;
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
    DateTimeOffset LastSeenUtc,
    /// <summary>
    /// P806 — the game settings this server published (native
    /// <c>MissionInfo</c>, <c>clintlib/clintlib.h:540-557</c>), the data the
    /// server-list filters and the status/skill/settings sort columns evaluate.
    /// Null when a host publishes none: the client then cannot prove either
    /// side of a Yes/No filter and hides that listing while the flag is set
    /// (see <see cref="LobbyGameFilters"/>). Optional so the record keeps the
    /// pre-P806 construction sites working.
    /// </summary>
    GameParams? Game = null,
    bool Verified = false,
    string? IdentityServerId = null)
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
        return $"[{(Verified ? "verified" : "unverified")}] {name}  {addr}   {players} players   {mission}";
    }

    /// <summary>
    /// Whether this listing should survive the lobby text filter: true when
    /// <paramref name="needle"/> is empty/whitespace (no filtering), or is a
    /// case-insensitive substring of one of the browseable text fields the row
    /// genuinely carries — server <see cref="Name"/>, <see cref="MissionName"/>
    /// or <see cref="Address"/>.
    /// <para>Faithful equivalent of native's game-name substring filter
    /// (<c>WinTrek/gamescreen.cpp:997-999</c>: <c>ZString(pgame-&gt;Name())
    /// .ToLower().Find(m_strGameNameSubstring.ToLower()) == -1</c> → excluded,
    /// fed from the <c>m_peditGameName</c> edit box at <c>gamescreen.cpp:510</c>).
    /// Native matches only <c>Name()</c>; this port extends to the other
    /// genuinely-carried string columns shown in the row so the filter is useful
    /// on real listings (mode-type metadata native also filters on is NOT carried
    /// by <see cref="ServerListing"/> — that is out of scope).</para>
    /// <para>Pure string logic, so it is unit-testable without a graphics device
    /// (same discipline as <see cref="FormatRow"/>).</para>
    /// </summary>
    public bool Matches(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        var cmp = StringComparison.OrdinalIgnoreCase;
        return (!string.IsNullOrEmpty(Name) && Name.Contains(needle, cmp))
            || (!string.IsNullOrEmpty(MissionName) && MissionName.Contains(needle, cmp))
            || (!string.IsNullOrEmpty(Address) && Address.Contains(needle, cmp));
    }

}

/// <summary>
/// P761b — one hit of the lobby's per-name find-player query
/// (<c>GET /api/servers/findplayer?name=</c>), the client mirror of the lobby's
/// <c>FindPlayerResultDto</c>. It names the SERVER the queried pilot is on and
/// nothing about any other pilot: the directory no longer publishes rosters
/// (privacy fix — native's FIND_PLAYER asked the lobby about exactly one name,
/// <c>WinTrek/gamescreen.cpp:1348-1352</c> → <c>FM_C_FIND_PLAYER</c>).
/// </summary>
public sealed record FindPlayerResult(
    string Id,
    string Name,
    string Address,
    int Port,
    int MaxPlayers,
    string MissionName,
    string CoreFile,
    string Version);

/// <summary>
/// P761/P761b — pure logic for the SERVER LIST's find-player popup and details
/// panel (native FindDialogPopup, gamescreen.cpp:531-628 + OnButtonFindPlayer
/// :1348-1352, and OnButtonDetails :1308-1326).
/// <para>Native sends <c>FM_C_FIND_PLAYER</c> to the lobby and the lobby replies
/// locating the game. This port's directory IS the lobby: the popup's Enter asks
/// it about ONE name (<see cref="FindPlayerRequestPath"/> →
/// <c>GET /api/servers/findplayer?name=</c>), and the answer names the server
/// that pilot is on. The open listing deliberately carries no rosters (P761b
/// privacy fix), so the search CANNOT run over the fetched list; only servers
/// that opted in to publishing are searchable at all. The details panel mirrors
/// native's purpose — show the selected game's details — with the fields the
/// directory genuinely carries (a player COUNT, never names).</para>
/// </summary>
public static class ServerDirectorySearch
{
    /// <summary>P761b — the lobby query path for a find-player search
    /// (<c>findplayer?name=</c>), URL-escaped. Null/blank names produce null:
    /// native does not ask the lobby about an empty name.</summary>
    public static string? FindPlayerRequestPath(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : "/api/servers/findplayer?name=" + Uri.EscapeDataString(name.Trim());

    /// <summary>The index in <paramref name="listings"/> of the listing with
    /// <paramref name="id"/>, or -1 (the find-player answer names the server by
    /// id, and the browser selects that row).</summary>
    public static int IndexOfListing(IReadOnlyList<ServerListing> listings, string? id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < listings.Count; i++)
            if (string.Equals(listings[i].Id, id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>The details-panel lines for the selected game (the port's
    /// stand-in for native's details flow, gamescreen.cpp:1308-1326 — native
    /// downloads mission details files; this port shows the directory's genuine
    /// per-listing fields). The player COUNT is shown; pilot NAMES are not part
    /// of the public listing (P761b privacy fix). Pure ASCII, one line per
    /// field.</summary>
    public static IReadOnlyList<string> DetailsLines(ServerListing l)
    {
        string addr = (string.IsNullOrWhiteSpace(l.Address) ? "?" : l.Address)
            + ":" + (l.Port > 0 ? l.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?");
        var lines = new List<string>
        {
            "GAME:    " + (string.IsNullOrWhiteSpace(l.MissionName) ? "-" : l.MissionName),
            "SERVER:  " + (string.IsNullOrWhiteSpace(l.Name) ? "(unnamed)" : l.Name),
            "ADDRESS: " + addr,
            "CORE:    " + (string.IsNullOrWhiteSpace(l.CoreFile) ? "-" : l.CoreFile),
            "VERSION: " + (string.IsNullOrWhiteSpace(l.Version) ? "-" : l.Version),
            "PLAYERS: " + l.PlayerCount + (l.MaxPlayers > 0 ? "/" + l.MaxPlayers : ""),
        };
        // P806 — the directory now carries the native MissionInfo settings, so
        // the panel can show the same facts native's list paints from them:
        // the winning condition (GameType, gametypes.cpp), the developments and
        // lives switches and the running/waiting state. Only carried values are
        // shown; a host that publishes none keeps the pre-P806 panel.
        if (l.Game is { } g)
        {
            lines.Add("TYPE:    " + GameParams.GameTypeName(g));
            lines.Add("STATE:   " + (g.InProgress ? "running" : "waiting"));
            lines.Add("GOALS:   " + g.GoalSummary());
            // One fact per line, same label column: the combined line ran past the
            // panel's right edge at 1280 wide.
            lines.Add("DEVEL:   " + (g.AllowDevelopments ? "on" : "off"));
            lines.Add("LIVES:   " + g.LivesLabel());
        }
        return lines;
    }
}
