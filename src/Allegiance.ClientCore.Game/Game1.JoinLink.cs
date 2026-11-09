using Allegiance.ClientCore.Session;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private JoinLinkLaunch? _joinLinkLaunch;
    private JoinLinkConfirmation? _joinLinkConfirmation;
    private Task? _joinLinkResolution;
    private bool _joinLinkInputArmed;
    private bool _joinLinkPasswordSeeded;
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
        _rememberGamePassword = false;
        _joinLinkPasswordSeeded = false;
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

    /// <summary>P970 — the saved-password key for the listing on this join-link
    /// screen (lobby + listing id, per the store's keying rule).</summary>
    private string? JoinLinkPasswordKey()
        => _joinLinkConfirmation is { Link: { } link, Listing: { } listing }
            ? JoinPasswordKey(link.Lobby.AbsoluteUri, listing, listing.Address, listing.Port)
            : null;

    private void UpdateJoinLink(KeyboardState keys)
    {
        // Reading IsCompleted establishes visibility of the resolver's result before drawing/confirming.
        bool ready = _joinLinkResolution?.IsCompleted ?? true;
        // P970 — once the listing is known, open the screen on its password field
        // when the game advertises one, and prefill a remembered password. One-shot
        // per confirmation so retyping never re-seeds the box.
        if (ready && !_joinLinkPasswordSeeded && _joinLinkConfirmation?.Listing is { } resolved)
        {
            _joinLinkPasswordSeeded = true;
            SeedJoinPassword(JoinLinkPasswordKey());
            if (resolved.PasswordRequired && _lanJoinPassword.Length == 0)
            {
                _editingLanPassword = true;
                Console.WriteLine($"[join] '{resolved.Name}' requires a password - field focused");
            }
        }
        if (ready) UpdateRememberToggle(keys);
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
            // P970 — a password the game REQUIRES submits on ENTER (native's
            // password dialog's OK is Enter, passworddialog.cpp:91-95); an
            // optional one keeps the browser's two-step edit, so a stray key never
            // joins with half a password typed (JoinPasswordPrompt.EnterSubmits).
            if (!Rendering.JoinPasswordPrompt.EnterSubmits(_editingLanPassword, _joinLinkConfirmation?.PasswordRequired == true))
                _editingLanPassword = false;
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
        _rememberGamePassword = false;
        _joinLinkPasswordSeeded = false;
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
            listingTarget: listing, listingLobby: lobby.AbsoluteUri.TrimEnd('/'),
            // P970 — a refused password comes back to THIS screen (native kept the
            // games list up and re-opened the password dialog there).
            origin: JoinOrigin.JoinLink);
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
            var details = new List<string>
            {
                "Server: " + JoinLinkConfirmation.Display(confirmation.ServerName),
                (listing.Verified ? "Verified server" : "Unverified server") + "  |  " + (listing.PlayerHosted ? "Player-hosted game" : "Dedicated server"),
                "Mission: " + JoinLinkConfirmation.Display(listing.MissionName, 65) + (confirmation.MissionCount > 1 ? "  (UP/DOWN to choose)" : ""),
                $"Players: {listing.PlayerCount} / {listing.MaxPlayers}",
            };
            // P970 — the lock, said plainly, with the counted edit line. A game that
            // advertises no password keeps the pre-P970 two-line tail exactly.
            if (listing.PasswordRequired) details.Add(Rendering.JoinPasswordPrompt.RequirementLine(true));
            details.Add(Rendering.JoinPasswordPrompt.EditLine(listing.PasswordRequired,
                _editingLanPassword ? "Password" : "TAB password", _lanJoinPassword.Length, _editingLanPassword));
            foreach (var line in details) DrawJoinLinkLine(line, w, ref y, text);
        }
        // The opt-in checkbox and the key help share the footer line: at 800x480 the
        // detail block already reaches the bottom band, so an extra row would collide.
        string? key = JoinLinkPasswordKey();
        bool saved = key is not null && SavedPasswords.Has(key);
        string footer = ready && confirmation?.Listing is { } confirmed
            ? (confirmed.PasswordRequired && _editingLanPassword
                ? "TYPE the password   ENTER join   ESC cancel   " + Rendering.JoinPasswordPrompt.RememberLine(_rememberGamePassword, saved)
                : "ENTER confirm and join   TAB password   ESC cancel   " + Rendering.JoinPasswordPrompt.RememberLine(_rememberGamePassword, saved))
            : "ESC return to the intro";
        var footerSize = _hudSmall.MeasureString(footer);
        float footerScale = Math.Min(1f, (w - 100f) / Math.Max(1f, footerSize.X));
        _spriteBatch.DrawString(_hudSmall, footer, new Vector2(50, h - 58), dim, 0f, Vector2.Zero, footerScale,
            Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
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
