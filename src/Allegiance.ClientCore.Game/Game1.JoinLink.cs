using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private JoinLinkLaunch? _joinLinkLaunch;
    private JoinLinkConfirmation? _joinLinkConfirmation;
    private Task? _joinLinkResolution;
    private bool _joinLinkInputArmed;
    private CancellationTokenSource? _joinLinkCancellation;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _joinLinkEvents = new();

    /// <summary>Platform URL events enqueue data; only the main UI thread changes screens.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string>? UrlOpenEvents { get; set; }

    public void ReceiveJoinLink(string url) => _joinLinkEvents.Enqueue(url);

    private void StartPendingJoinLink()
    {
        while (UrlOpenEvents?.TryDequeue(out var eventUrl) == true) _joinLinkEvents.Enqueue(eventUrl);
        // OS events arriving in a match wait until the player returns to the intro.
        if (_joinLinkConfirmation is { Closed: false }) return;
        if (_joinLinkEvents.TryDequeue(out var url))
        {
            string[] arguments = [url];
            _joinLinkLaunch = JoinLinkLaunch.Take(ref arguments);
        }
        if (_joinLinkLaunch is not { IsLinkLaunch: true } launch) return;
        _joinLinkLaunch = null;
        _joinLinkCancellation?.Dispose();
        _joinLinkCancellation = new();
        _joinLinkConfirmation = new(launch.Link, IntroPreferences.Load().Lobby, launch.Error);
        _lanJoinPassword.Clear();
        _editingLanPassword = false;
        _joinLinkInputArmed = false;
        _introView = IntroView.JoinLink;
        _joinLinkResolution = ResolveJoinLinkAsync(_joinLinkConfirmation, _joinLinkCancellation.Token);
    }

    private static async Task ResolveJoinLinkAsync(JoinLinkConfirmation confirmation, CancellationToken token)
    {
        // A link's lobby is an independent, untrusted origin: no saved credentials,
        // cookies or redirect to another origin, and no mutation of the remembered lobby.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        await confirmation.ResolveAsync(http, token);
    }

    private void UpdateJoinLink(KeyboardState keys)
    {
        // Reading IsCompleted establishes visibility of the resolver's result before drawing/confirming.
        bool ready = _joinLinkResolution?.IsCompleted ?? true;
        bool enter = keys.IsKeyDown(Keys.Enter), escape = keys.IsKeyDown(Keys.Escape), tab = keys.IsKeyDown(Keys.Tab);
        bool up = keys.IsKeyDown(Keys.Up), down = keys.IsKeyDown(Keys.Down);
        if (ready && up && !_iUp) _joinLinkConfirmation?.SelectMission(-1);
        if (ready && down && !_iDown) _joinLinkConfirmation?.SelectMission(1);
        // A key held while opening the app or waiting on HTTP cannot accept a
        // newly displayed confirmation. Require release after resolution first.
        if (!ready) _joinLinkInputArmed = false;
        if (!_joinLinkInputArmed)
        {
            if (ready && !enter && !escape && !tab) _joinLinkInputArmed = true;
            _iEnter = enter; _iEsc = escape; _iTab = tab;
            if (escape) CancelJoinLink();
            return;
        }
        if (tab && !_iTab) _editingLanPassword = !_editingLanPassword;
        if (escape && !_iEsc) CancelJoinLink();
        else if (enter && !_iEnter)
        {
            if (_editingLanPassword) _editingLanPassword = false; // same two-step password edit as browser
            else if (ready) _joinLinkConfirmation?.Confirm(JoinResolvedListing);
        }
        HandleIntroBackspace(keys);
        _iUp = up; _iDown = down;
        _iEnter = enter; _iEsc = escape; _iTab = tab;
    }

    private void CancelJoinLink()
    {
        _joinLinkConfirmation?.Cancel();
        _joinLinkCancellation?.Cancel();
        _lanJoinPassword.Clear();
        _editingLanPassword = false;
        _introView = IntroView.Main;
    }

    private void JoinResolvedListing(ServerListing listing, Uri lobby)
    {
        _introView = IntroView.Main;
        _returnToIntroOnFailure = true;
        _returnToLobbyOnFailure = false;
        StopIntroScan();
        // Exactly the browser's BeginConnect call; listingLobby only pins the
        // introduction origin, without overwriting _lobbyUrl or player.json.
        BeginConnect(listing.Address, (ushort)listing.Port, listing.MissionCookie,
            listingTarget: listing, listingLobby: lobby.AbsoluteUri.TrimEnd('/'));
    }

    private void DrawJoinLink(float w, float h, Color title, Color dim, Color text)
    {
        var confirmation = _joinLinkConfirmation;
        bool ready = _joinLinkResolution?.IsCompleted ?? true;
        DrawIntroTitle(w, "Join game from a web link", "Review the lobby and server before connecting", title, dim);
        string[] lines =
        [
            "Lobby: " + confirmation?.LobbyDescription,
            confirmation?.DifferentLobby == true ? "Different from your saved lobby. Your saved lobby will stay the same." : "Your saved lobby will stay the same.",
            ready ? confirmation?.Message ?? "Invalid Join link." : "Looking up the current lobby listing...",
        ];
        float y = h * .27f;
        foreach (var line in lines) DrawJoinLinkLine(line, w, ref y, text);
        if (ready && confirmation?.Listing is { } listing)
        {
            string[] details =
            [
                "Server: " + JoinLinkConfirmation.Display(confirmation.ServerName),
                (listing.Verified ? "Verified server" : "Unverified server") + "  |  " + (listing.PlayerHosted ? "Player-hosted game" : "Dedicated server"),
                "Mission: " + JoinLinkConfirmation.Display(listing.MissionName, 65) + (confirmation.MissionCount > 1 ? "  (UP/DOWN to choose)" : ""),
                $"Players: {listing.PlayerCount} / {listing.MaxPlayers}",
                "TAB password (if required): " + new string('*', _lanJoinPassword.Length) + (_editingLanPassword ? "_  (ENTER done)" : ""),
            ];
            foreach (var line in details) DrawJoinLinkLine(line, w, ref y, text);
        }
        _spriteBatch.DrawString(_hudSmall, ready && confirmation?.Listing is not null
            ? "ENTER confirm and join     ESC cancel" : "ESC return to the intro", new Vector2(50, h - 58), dim);
    }

    private void DrawJoinLinkLine(string line, float width, ref float y, Color color)
    {
        // Show the entire ASCII/IDN host. Truncating it could hide an attacker's
        // actual domain behind a long trusted-looking prefix.
        int columns = Math.Max(12, (int)((width - 100) / _hudSmall.MeasureString("M").X));
        for (int start = 0; start < line.Length; start += columns)
        {
            _spriteBatch.DrawString(_hudSmall, line.Substring(start, Math.Min(columns, line.Length - start)), new Vector2(50, y), color);
            y += 34;
        }
        if (line.Length == 0) y += 34;
    }

}
