using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly HostLanScreenModel _hostLanScreen = new();
    private readonly System.Text.StringBuilder _lanJoinPassword = new();
    private bool _editingLanPassword;
    private bool _internetPasswordTabWasDown;

    // ── P970 — the join-password UX shared by every join screen ──────────────
    // "Remember for this server" checkbox (default OFF) plus the refusal state a
    // password-refused join comes back to. The shared text/focus rules live in
    // Rendering/JoinPasswordPrompt.cs so Classic and Modern cannot diverge.
    private bool _rememberGamePassword;
    private bool _rememberKeyWasDown;
    private string? _joinPasswordKey;
    private JoinOrigin _joinOrigin = JoinOrigin.Intro;
    private string _joinPasswordError = "";
    private SavedGamePasswords? _savedGamePasswords;
    private string? _lobbyPasswordFocusTarget;

    /// <summary>Where a refused join returns to (native kept the player on the
    /// game list and re-opened the password dialog there, gamescreen.cpp:1498-1505;
    /// this port has one join screen per route and returns to the one used).</summary>
    private enum JoinOrigin { Intro, Lobby, JoinLink }

    /// <summary>The per-user saved-password store, loaded once per process and
    /// mutated in place (never re-read per frame: Draw must not touch the disk).</summary>
    private SavedGamePasswords SavedPasswords => _savedGamePasswords ??= SavedGamePasswords.Load();

    /// <summary>Both halves of the store key: a lobby listing is identified by
    /// lobby + listing id; a bare connect target by host:port (P970 rules).</summary>
    private static string JoinPasswordKey(string? lobbyUrl, ServerListing? listing, string host, int port)
        => listing is { Id.Length: > 0 } && !string.IsNullOrEmpty(lobbyUrl)
            ? SavedGamePasswords.LobbyKey(lobbyUrl, listing.Id)
            : SavedGamePasswords.HostKey(host, port);

    /// <summary>The store key the CURRENT join screen targets (the checkbox's
    /// context before any connect happens). Null when the screen has no target yet.</summary>
    private string? ActiveJoinPasswordKey()
    {
        switch (_introView)
        {
            case IntroView.JoinLink:
                return _joinLinkConfirmation?.Link is { } link && _joinLinkConfirmation.Listing is { } chosen
                    ? JoinPasswordKey(link.Lobby.AbsoluteUri, chosen, chosen.Address, chosen.Port) : _joinPasswordKey;
            case IntroView.FindServer:
            {
                DiscoveredServer? target;
                lock (_introFoundLock)
                {
                    var list = IntroFindServerList.FilterAndSort(_introFound);
                    target = list.Count == 0 ? null : list[Math.Min(_introFoundIndex, list.Count - 1)];
                }
                return target is null ? _joinPasswordKey : SavedGamePasswords.HostKey(target.Address, target.Port);
            }
            case IntroView.DirectConnect:
                return IntroScreenModel.TryParseDirect(_directBuf.ToString(), 6073, out var host, out var port)
                    ? SavedGamePasswords.HostKey(host, port) : SavedGamePasswords.HostKey(_directBuf.ToString(), 0);
            default:
                return _joinPasswordKey;
        }
    }

    /// <summary>M — the opt-in checkbox. Turning it OFF is also the join screen's
    /// "forget this server's saved password" (the value is deleted immediately).</summary>
    private void UpdateRememberToggle(KeyboardState keys)
    {
        // F8 (NOT a printable key): the password box owns typed text while it has
        // focus, so ticking the box must never be a character.
        bool toggle = keys.IsKeyDown(Keys.F8);
        if (toggle && !_rememberKeyWasDown) ToggleRememberPassword(ActiveJoinPasswordKey() ?? _joinPasswordKey);
        _rememberKeyWasDown = toggle;
    }

    private void ToggleRememberPassword(string? key)
    {
        _rememberGamePassword = !_rememberGamePassword;
        if (!_rememberGamePassword && key is not null)
        {
            var store = SavedPasswords;
            if (store.Remove(key))
            {
                store.Save();
                Console.WriteLine("[join] saved password forgotten for this server");
            }
        }
        Console.WriteLine($"[join] remember password for this server: {(_rememberGamePassword ? "ON" : "OFF")}");
    }

    /// <summary>Drop one server's stored password (a refused password clears its
    /// saved entry, and the explicit forget).</summary>
    private void ForgetSavedPassword(string? key)
    {
        if (key is null) return;
        var store = SavedPasswords;
        if (store.Remove(key))
        {
            store.Save();
            Console.WriteLine("[join] saved password cleared (join refused or forgotten)");
        }
    }

    /// <summary>Prefill the edit box from the opt-in store and mirror the stored
    /// state in the checkbox. Called when a join screen opens for a game.</summary>
    /// <param name="resetRemember">True when the JOIN TARGET changed (the checkbox
    /// follows the store for the new server). False at connect time, so a box the
    /// player just ticked is not silently switched back off.</param>
    private void SeedJoinPassword(string? key, bool resetRemember = true)
    {
        if (key is null) return;
        var store = SavedPasswords;
        if (resetRemember) _rememberGamePassword = store.Has(key);
        // `is { Length: 0 }` keeps this safe for the reflection-built Game1 the UI
        // tests use (no field initializers run there); production always has the buffer.
        if (_lanJoinPassword is { Length: 0 } && store.Get(key) is { Length: > 0 } saved
            && saved.Length <= Rendering.JoinPasswordPrompt.MaxLength)
            _lanJoinPassword.Append(saved);
    }

    /// <summary>Persist the typed password for this server once the join was
    /// ACCEPTED (never before: a refused password is not worth remembering).</summary>
    private void SaveRememberedPassword(string? key)
    {
        if (key is null || !_rememberGamePassword || _lanJoinPassword.Length == 0) return;
        var store = SavedPasswords;
        if (store.Set(key, _lanJoinPassword.ToString()))
        {
            store.Save();
            Console.WriteLine("[join] password remembered for this server (opt-in)");
        }
    }

    /// <summary>P970 — the server browser's password state follows its selection: a
    /// remembered password for the new server is prefilled and the advertisement is
    /// shown (the requirement line + the counted edit line). The field is NOT
    /// force-focused here: this screen joins with ENTER straight off the row list,
    /// and the browser's password-edit mode owns Up/Down while it is open
    /// (<see cref="HandleLobbyPassword"/>), so stealing focus would stop the player
    /// from moving the cursor. TAB focuses it; a refused join arrives focused.</summary>
    private void SyncLobbyPasswordFocus()
    {
        if (_visibleRows.Count == 0 || _displayServers.Count == 0) return;
        var s = _displayServers[_visibleRows[Math.Clamp(_selectedIndex, 0, _visibleRows.Count - 1)]];
        string key = JoinPasswordKey(_lobbyUrl, s, s.Address, s.Port);
        if (key == _lobbyPasswordFocusTarget) return;
        _lobbyPasswordFocusTarget = key;
        // A different server never inherits the previous one's typed password.
        _lanJoinPassword.Clear();
        _editingLanPassword = false;
        SeedJoinPassword(key);
    }

    private bool HandleLobbyPassword(KeyboardState keys, bool backspace)
    {
        bool toggle = ActionDown(keys, "LobbyPassword");
        if (toggle && !_internetPasswordTabWasDown && !_filterActive && !_findPlayerActive)
            _editingLanPassword = !_editingLanPassword;
        _internetPasswordTabWasDown = toggle;
        if (!_editingLanPassword) return false;
        if (backspace && !_backWasDown && _lanJoinPassword.Length > 0) _lanJoinPassword.Length--;
        _backWasDown = backspace;
        bool done = ActionDown(keys, "LobbyJoin");
        bool submit = done && !_enterWasDown;
        // Release the Enter press that opened the prompt before submitting.
        // Latch submissions so holding the key cannot start another handshake.
        _enterWasDown = done;
        if (submit)
        {
            _editingLanPassword = false;
            int selected = SelectedDisplayIndex();
            if (selected >= 0 && _displayServers[selected].PasswordRequired)
                TryJoinSelected(); // required password: ENTER is the dialog's OK
        }
        return true;
    }

    private void UpdateHostLan(KeyboardState keys, bool up, bool down, bool enter, bool esc)
    {
        if (_skirmishStart is null)
        {
            if (up && !_iUp) _hostLanScreen.Move(-1);
            if (down && !_iDown) _hostLanScreen.Move(1);
            if (keys.IsKeyDown(Keys.Left) && !_iLeft) _hostLanScreen.Change(-1);
            if (keys.IsKeyDown(Keys.Right) && !_iRight) _hostLanScreen.Change(1);
            if (keys.IsKeyDown(Keys.Back) && !_iBack) _hostLanScreen.Backspace();
            if (enter && !_iEnter)
            {
                if (_hostLanScreen.Selected == HostLanScreenModel.BackRow) _introView = IntroView.Main;
                else if (_hostLanScreen.Selected == HostLanScreenModel.StartRow)
                {
                    try
                    {
                        var choices = _hostLanScreen.CreateOptions();
                        _skirmishError = "";
                        _skirmishCancel?.Dispose();
                        _skirmishCancel = new();
                        var token = _skirmishCancel.Token;
                        string pilot = _pilotBuf.Length > 0 ? _pilotBuf.ToString() : _playerName;
                        _skirmishStart = Task.Run(() => SkirmishHost.StartAsync(choices, cancellationToken: token, commanderPilotName: pilot));
                    }
                    catch (ArgumentException ex) { _skirmishError = ex.Message; }
                }
                else _hostLanScreen.Change(1);
            }
        }
        if (esc && !_iEsc)
        {
            if (_skirmishStart is not null) _skirmishCancel?.Cancel();
            else _introView = IntroView.Main;
        }
    }

    private void DrawIntroHostLan(float w, float h, Color title, Color dim, Color text)
    {
        bool internet = _hostLanScreen.Options.HostInternet;
        DrawIntroTitle(w, internet ? "Host internet game" : "Host LAN game", internet
            ? "Friends join through Play Online on the same lobby" : "Friends join through Play LAN / Find Server or Direct Connect", title, dim);
        var rows = _hostLanScreen.Rows;
        for (int i = 0; i < rows.Length; i++)
            _spriteBatch.DrawString(_hudSmall, (i == _hostLanScreen.Selected ? "> " : "  ") + rows[i],
                new Vector2(48, 150 + i * 32), i == _hostLanScreen.Selected ? title : text);
        string[] help =
        [
            _skirmishStart is not null ? "Starting host... ESC cancels" : _skirmishError,
            "Bots keep their seats. Friends join beside them, within native team capacity and balance limits.",
            internet ? "Lobby: " + _hostLanScreen.Options.InternetLobbyUrl : "Share your PC's IPv4 address and the game port shown above. Live addresses and count appear in game.",
            internet ? "The lobby must enable player hosting. Allow the game's UDP traffic through your firewall." : HostLanScreenModel.NetworkHelp,
            internet ? "Direct connection unavailable? Forward TCP/UDP or use Tailscale. Host quitting ends the game." : HostLanScreenModel.InternetHelp,
            "TYPE name / port / password   UP/DOWN select   LEFT/RIGHT change   ENTER choose   ESC back"
        ];
        for (int i = 0; i < help.Length; i++)
            _spriteBatch.DrawString(_hudSmall, help[i], new Vector2(32, h - 178 + i * 27), i == 0 ? Color.Orange : dim,
                0, Vector2.Zero, Math.Min(1f, (w - 64) / Math.Max(1, _hudSmall.MeasureString(help[i]).X)), Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0);
    }
}
