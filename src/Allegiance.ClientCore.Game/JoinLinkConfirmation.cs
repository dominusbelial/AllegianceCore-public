using System.Net;
using System.Net.Http.Json;
using Allegiance.ClientCore.Session;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Shared by both HUD styles. Resolution cannot initiate a connection.</summary>
public sealed class JoinLinkConfirmation
{
    public JoinLink? Link { get; }
    public ServerListing? Listing { get; private set; }
    private IReadOnlyList<ServerListing> _missions = [];
    private int _selectedMission;
    public int MissionCount => _missions.Count;
    public string ServerName { get; private set; } = "";
    public string Message { get; private set; }
    public bool Resolving { get; private set; }
    public bool Closed { get; private set; }
    public bool DifferentLobby { get; }
    public string LobbyDescription => Link is null ? "" :
        Link.Lobby.Scheme + "://" + (Link.Lobby.HostNameType == UriHostNameType.IPv6
            ? "[" + Link.Lobby.IdnHost.Trim('[', ']') + "]" : Link.Lobby.IdnHost)
        + (Link.Lobby.IsDefaultPort ? "" : ":" + Link.Lobby.Port);

    public JoinLinkConfirmation(JoinLink? link, string rememberedLobby, string? error = null)
    {
        Link = link;
        Message = error ?? "Looking up the current lobby listing...";
        DifferentLobby = link is not null && (!Uri.TryCreate(rememberedLobby, UriKind.Absolute, out var remembered) ||
            !Uri.Equals(link.Lobby, remembered));
    }

    public async Task ResolveAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        if (Link is null || Resolving || Closed) return;
        Resolving = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var endpoint = new Uri(Link.Lobby, "/api/servers/" + Uri.EscapeDataString(Link.ListingId));
            using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                Message = "This listing is unknown or has expired. Return to the lobby and choose a current game.";
            else
            {
                response.EnsureSuccessStatusCode();
                // Bound response size and prohibit redirects in the production HttpClient.
                await response.Content.LoadIntoBufferAsync(256 * 1024, deadline.Token);
                var listing = await response.Content.ReadFromJsonAsync<ServerListing>(ServerListing.JsonOptions, deadline.Token);
                if (listing is null || listing.Id != Link.ListingId || string.IsNullOrWhiteSpace(listing.Address) ||
                    listing.Port is < 1 or > 65535 || (listing.Missions?.Any(m => m is null) ?? false) || (listing.PlayerHosted && listing.UdpPort is not (> 0 and <= 65535)))
                    Message = "The lobby returned an invalid listing. Return to the lobby and try another game.";
                else if (listing.RequiresNewerClient(Game1.InstalledClientVersion))
                    Message = "This server requires a newer client. Install the latest release before joining.";
                else
                {
                    ServerName = listing.Name;
                    _missions = ServerListing.ExpandMissions([listing]).ToArray();
                    Listing = _missions[0];
                    Message = "Confirm to join this game.";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException or IOException)
        {
            Message = "The lobby could not be reached or returned an invalid listing. Return to the lobby and try again.";
        }
        finally { Resolving = false; }
    }

    public void SelectMission(int direction)
    {
        if (Closed || Resolving || _missions.Count < 2) return;
        _selectedMission = (_selectedMission + direction + _missions.Count) % _missions.Count;
        Listing = _missions[_selectedMission];
    }

    public void Confirm(Action<ServerListing, Uri> join)
    {
        if (Closed || Resolving || Listing is null || Link is null) return;
        Closed = true;
        join(Listing, Link.Lobby);
    }

    public void Cancel() { Closed = true; }

    /// <summary>P970 — a join refused with <c>DPR_BadPassword</c> reopens THIS
    /// confirmation on the same listing so the player retypes next to the game
    /// they chose (native kept the games list up and re-opened its password
    /// dialog there, <c>WinTrek/gamescreen.cpp:1498-1505</c>). The resolution and
    /// the listing stay intact: no second HTTP lookup, no lost selection.</summary>
    public void RejectPassword(string message)
    {
        if (_missions.Count == 0) return;
        Closed = false;   // the confirm already closed it; ENTER must work again
        Message = message;
    }

    /// <summary>P970 — does the selected game advertise a password requirement?</summary>
    public bool PasswordRequired => Listing?.PasswordRequired == true;

    // Network text remains plain text; control characters cannot spoof extra UI rows.
    public static string Display(string? text, int limit = 100) =>
        new((text ?? "").Select(c => c is >= ' ' and <= '~' ? c : '?').Take(limit).ToArray());
}
