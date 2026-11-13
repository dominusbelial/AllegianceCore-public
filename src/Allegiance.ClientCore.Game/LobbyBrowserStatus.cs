using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Shared browser messages for Classic and Modern. A directory reply,
/// team availability and game admission are separate outcomes.</summary>
public static class LobbyBrowserStatus
{
    public static string FetchFailure(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => $"Lobby returned HTTP {(int)status} ({status}). Retrying...",
        HttpRequestException => "Cannot reach the lobby. Retrying...",
        OperationCanceledException => "Lobby request timed out. Retrying...",
        JsonException or NotSupportedException => "Lobby returned an invalid server list. Retrying...",
        _ => "Could not load the lobby server list. Retrying...",
    };

    public static string? ListingMessage(IReadOnlyList<ServerListing> listings, IReadOnlyList<int> visibleRows)
    {
        if (listings.Count == 0) return "No games are currently listed. Waiting for registrations...";
        if (visibleRows.Count == 0) return "No games match the current filters. Press C to reset.";
        // Native keeps red rows selectable (WinTrek/gamescreen.cpp:1299-1305).
        // Neither a full team nor a password means the directory is offline.
        if (visibleRows.All(i => !listings[i].HasOpenTeamSlots))
            return "Listed games report no open team slots. ENTER requests entry; R refreshes.";
        return null;
    }
}
