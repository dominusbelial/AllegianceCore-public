using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P500 — the Android in-app connect screen, built on top of the existing
/// server-browser (Lobby) state and rendering style rather than a parallel UI.
/// Adds locally-saved servers (seeded once from <c>connect.json</c>, then
/// persisted in the app's private data dir), add/edit/delete + player-name
/// editing through MonoGame's soft keyboard (<see cref="KeyboardInput"/>),
/// an optional lobby URL that merges <c>/api/servers</c> listings via the
/// existing poll, and touch/gamepad navigation of the same rows and buttons.
/// Desktop is unchanged: the editing features are only offered when a
/// <see cref="IConnectStore"/> is supplied (only the Android head supplies one).
/// </summary>
public sealed partial class Game1
{
    private enum ConnectAction { AddServer, EditServer, DeleteServer, PlayerName, LobbyUrl }

    // ── P500 state ─────────────────────────────────────────────────────────
    private readonly IConnectStore? _connectStore;
    private readonly ITextPrompt _textPrompt;
    private List<SavedServer> _savedServers = new();
    private string? _lastUsedKey;

    /// <summary>A soft-keyboard prompt is in flight (awaiting a dialog); gate
    /// re-entry so a held key / re-tap can't stack prompts.</summary>
    private bool _connectPromptActive;

    /// <summary>Combined row list = saved servers first, then lobby listings.
    /// <c>_servers</c> stays the fetched-lobby source; this is what the visible
    /// rows and the join path read.</summary>
    private readonly List<ServerListing> _displayServers = new();

    // Rising-edge detectors for the connect-screen gamepad actions (A=connect,
    // Y=add, X=edit), separate from the keyboard edge set in Game1.cs.
    private bool _gcAWasDown, _gcYWasDown, _gcXWasDown;

    /// <summary>Default text-entry seam: MonoGame's platform soft keyboard.
    /// Exists in both the DesktopGL and Android MonoGame assemblies with the
    /// same signature; the Android runtime resolves it to the on-screen
    /// keyboard. Desktop heads never surface a prompt (no store), so this
    /// default is inert there.</summary>
    private sealed class KeyboardTextPrompt : ITextPrompt
    {
        public async Task<string?> PromptAsync(string title, string description, string defaultValue)
            => await KeyboardInput.Show(title, description, defaultValue, usePasswordMode: false);
    }

    /// <summary>Rebuild <see cref="_displayServers"/> from the saved servers +
    /// fetched lobby listings, then recompute the visible/selected rows and
    /// restore the last-used selection when this is a cold rebuild.</summary>
    private void RebuildDisplay(bool restoreLastUsed = false)
    {
        _displayServers.Clear();
        foreach (var s in _savedServers)
            _displayServers.Add(AsListing(s));
        _displayServers.AddRange(_servers);

        RebuildVisibleRows();

        if (restoreLastUsed && _lastUsedKey is { } key)
        {
            for (int i = 0; i < _displayServers.Count; i++)
            {
                if (_displayServers[i].Address is { } a && $"{a}:{_displayServers[i].Port}" == key)
                {
                    SelectDisplayIndex(i);
                    break;
                }
            }
        }
    }

    /// <summary>Bring the given display index into the selection (it becomes
    /// the sole visible-row selection, so Enter joins it directly).</summary>
    private void SelectDisplayIndex(int displayIndex)
    {
        for (int i = 0; i < _visibleRows.Count; i++)
        {
            if (_visibleRows[i] == displayIndex)
            {
                _selectedIndex = i;
                return;
            }
        }
        // Not in the filtered view: reveal it by clearing the filter.
        if (_filterActive)
            ClearFilter();
        RebuildVisibleRows();
        for (int i = 0; i < _visibleRows.Count; i++)
        {
            if (_visibleRows[i] == displayIndex)
            {
                _selectedIndex = i;
                return;
            }
        }
    }

    private static ServerListing AsListing(SavedServer s)
    {
        string hp = s.HostPort;
        return new ServerListing(
            Id: "local:" + hp,
            Name: string.IsNullOrWhiteSpace(s.Label) ? hp : s.Label,
            Address: s.Host,
            Port: s.Port,
            PlayerCount: 0,
            MaxPlayers: 0,
            MissionName: "manual",
            CoreFile: "",
            Version: "",
            RegisteredUtc: default,
            LastSeenUtc: default);
    }

    /// <summary>Index (into <see cref="_displayServers"/>) of the currently
    /// highlighted row, or -1 when the visible list is empty.</summary>
    private int SelectedDisplayIndex()
    {
        if (_visibleRows.Count == 0)
            return -1;
        return _visibleRows[Math.Min(_selectedIndex, _visibleRows.Count - 1)];
    }

    /// <summary>True when the selected row is one of the locally-saved servers
    /// (edit/delete apply to it).</summary>
    private bool SelectedIsSaved()
    {
        int di = SelectedDisplayIndex();
        return di >= 0 && di < _savedServers.Count;
    }

    /// <summary>Persist the current connect profile through the store. Carries
    /// the existing profile's <see cref="ConnectProfile.Initialized"/> flag
    /// forward (otherwise the first in-game edit would reset it to false and
    /// trigger a connect.json re-seed on the next launch — a real bug caught
    /// on the emulator).</summary>
    private void PersistConnectProfile()
    {
        if (_connectStore is null)
            return;
        var existing = _connectStore.Load();
        _connectStore.Save(new ConnectProfile
        {
            Servers = new List<SavedServer>(_savedServers),
            PlayerName = _playerName,
            LobbyUrl = _lobbyUrl,
            LastUsedKey = _lastUsedKey,
            Initialized = existing?.Initialized ?? true,
        });
    }

    /// <summary>Join the currently-highlighted server (shared by Enter, gamepad
    /// A and the CONNECT button); failures revert to the list.</summary>
    private void TryJoinSelected()
    {
        int di = SelectedDisplayIndex();
        if (di < 0)
            return;
        var s = _displayServers[di];
        if (s.Port <= 0)
        {
            _lobbyError = $"Cannot join '{s.Name}' - it advertises no valid port.";
            return;
        }
        _returnToLobbyOnFailure = true;
        Console.WriteLine($"[monogame] joining {s.Name} @ {s.Address}:{s.Port} ...");
        BeginConnect(s.Address, (ushort)s.Port);
    }

    /// <summary>Delete the selected SAVED server (no-op otherwise) and persist.</summary>
    private void DeleteSelectedSaved()
    {
        if (!SelectedIsSaved())
            return;
        int di = SelectedDisplayIndex();
        _savedServers.RemoveAt(di);
        PersistConnectProfile();
        RebuildDisplay();
        Console.WriteLine("[monogame] deleted saved server");
    }

    /// <summary>Route a touch/button/gamepad action to the right handler.</summary>
    private void InvokeConnectAction(ConnectScreenLayout.Button kind)
    {
        switch (kind)
        {
            case ConnectScreenLayout.Button.Connect:
                TryJoinSelected();
                break;
            case ConnectScreenLayout.Button.Add:
                StartConnectPrompt(ConnectAction.AddServer);
                break;
            case ConnectScreenLayout.Button.Edit:
                if (SelectedIsSaved())
                    StartConnectPrompt(ConnectAction.EditServer);
                else
                    Console.WriteLine("[monogame] connect: no saved server selected to edit");
                break;
            case ConnectScreenLayout.Button.Delete:
                DeleteSelectedSaved();
                break;
            case ConnectScreenLayout.Button.Name:
                StartConnectPrompt(ConnectAction.PlayerName);
                break;
            case ConnectScreenLayout.Button.Lobby:
                StartConnectPrompt(ConnectAction.LobbyUrl);
                break;
        }
    }

    /// <summary>Touch input for the connect screen: a tap on a row selects it; a
    /// tap on a bottom button fires that action. Only active when a connect
    /// store is present (Android).</summary>
    private void HandleConnectTouch()
    {
        if (_connectStore is null || _connectPromptActive)
            return;
        var vp = GraphicsDevice.Viewport;
        var touches = TouchPanel.GetState();
        foreach (var t in touches)
        {
            if (t.State != TouchLocationState.Released)
                continue;
            var p = t.Position;

            for (int i = 0; i < _visibleRows.Count; i++)
            {
                var r = ConnectScreenLayout.RowRect(i, vp.Width);
                if (r.Contains(p.X, p.Y))
                {
                    _selectedIndex = i;
                    Console.WriteLine($"[monogame] connect: tapped row {_selectedIndex}");
                    return;
                }
            }

            foreach (var (kind, rect) in ConnectScreenLayout.ComputeButtons(vp.Width, vp.Height))
            {
                if (rect.Contains(p.X, p.Y))
                {
                    Console.WriteLine($"[monogame] connect: tapped {kind}");
                    InvokeConnectAction(kind);
                    return;
                }
            }
        }
    }

    /// <summary>Gamepad navigation for the connect screen: dpad handled as the
    /// Up/Down arrows elsewhere; here A=connect, Y=add, X=edit (edge-triggered).
    /// </summary>
    private void HandleConnectGamepad()
    {
        if (_connectPromptActive)
            return;
        var gp = GamePad.GetState(PlayerIndex.One);
        bool a = gp.Buttons.A == ButtonState.Pressed;
        if (a && !_gcAWasDown)
            TryJoinSelected();
        _gcAWasDown = a;

        if (_connectStore is null)
        {
            _gcYWasDown = gp.Buttons.Y == ButtonState.Pressed;
            _gcXWasDown = gp.Buttons.X == ButtonState.Pressed;
            return;
        }

        bool y = gp.Buttons.Y == ButtonState.Pressed;
        if (y && !_gcYWasDown)
            StartConnectPrompt(ConnectAction.AddServer);
        _gcYWasDown = y;

        bool x = gp.Buttons.X == ButtonState.Pressed;
        if (x && !_gcXWasDown && SelectedIsSaved())
            StartConnectPrompt(ConnectAction.EditServer);
        _gcXWasDown = x;
    }

    private void StartConnectPrompt(ConnectAction action)
    {
        if (_connectPromptActive || _connectStore is null)
            return;
        _connectPromptActive = true;
        _ = RunConnectPromptAsync(action);
    }

    /// <summary>Walk the soft-keyboard prompts for an action, applying the edit
    /// and persisting on success. Cancel (null from any prompt) aborts the whole
    /// action. Runs on the main/UI thread (KeyboardInput's continuation resumes
    /// on the calling SynchronizationContext), so the list mutation is safe.</summary>
    private async Task RunConnectPromptAsync(ConnectAction action)
    {
        try
        {
            switch (action)
            {
                case ConnectAction.AddServer:
                    await PromptAddOrEdit(add: true);
                    break;
                case ConnectAction.EditServer:
                    await PromptAddOrEdit(add: false);
                    break;
                case ConnectAction.PlayerName:
                    var nm = await _textPrompt.PromptAsync("Player name", "Name shown to other pilots", _playerName);
                    if (nm is not null && !string.IsNullOrWhiteSpace(nm))
                    {
                        _playerName = nm.Trim();
                        PersistConnectProfile();
                        Console.WriteLine($"[monogame] player name set to '{_playerName}'");
                    }
                    break;
                case ConnectAction.LobbyUrl:
                    var lobby = await _textPrompt.PromptAsync("Lobby URL", "Blank to disable (manual only)", _lobbyUrl);
                    if (lobby is not null)
                    {
                        _lobbyUrl = lobby.Trim();
                        // Rebuild the poll client for the new base; reset cadence so it fetches soon.
                        _lastLobbyFetch = double.NegativeInfinity;
                        Console.WriteLine($"[monogame] lobby URL {'"'}{_lobbyUrl}{'"'} ({(_lobbyUrl.Length == 0 ? "polling OFF" : "polling ON")})");
                        PersistConnectProfile();
                    }
                    break;
                case ConnectAction.DeleteServer:
                    DeleteSelectedSaved();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] connect prompt error: {ex.Message}");
        }
        finally
        {
            _connectPromptActive = false;
        }
    }

    private async Task PromptAddOrEdit(bool add)
    {
        string? label = "", host = "", port = "2326";
        if (!add)
        {
            int di = SelectedDisplayIndex();
            if (di < 0 || di >= _savedServers.Count)
                return;
            var cur = _savedServers[di];
            label = cur.Label;
            host = cur.Host;
            port = cur.Port.ToString(CultureInfo.InvariantCulture);
        }

        label = await _textPrompt.PromptAsync(add ? "Add server" : "Edit server", "Label", label);
        if (label is null)
            return;
        host = await _textPrompt.PromptAsync(add ? "Add server" : "Edit server", "Host / IP", host);
        if (host is null)
            return;
        port = await _textPrompt.PromptAsync(add ? "Add server" : "Edit server", "Port", port);
        if (port is null)
            return;

        if (string.IsNullOrWhiteSpace(host) || !TryParsePort(port, out ushort p) || p == 0)
        {
            Console.WriteLine($"[monogame] connect: invalid entry host='{host}' port='{port}' - ignored");
            return;
        }

        var sv = new SavedServer(label.Trim(), host.Trim(), p);
        if (add)
        {
            _savedServers.Add(sv);
        }
        else
        {
            int di = SelectedDisplayIndex();
            if (di < 0 || di >= _savedServers.Count)
                return;
            _savedServers[di] = sv;
        }
        PersistConnectProfile();
        RebuildDisplay();
        Console.WriteLine($"[monogame] saved server {(add ? "added" : "edited")}: '{sv.Label}' {sv.Host}:{sv.Port}");
    }

    private static bool TryParsePort(string text, out ushort port)
        => ushort.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);

    /// <summary>Format one combined row for the connect screen. Saved servers
    /// carry <c>MissionName="manual"</c> + 0/0 players, so they get a terse
    /// line; real lobby listings use their full <c>FormatRow()</c>.</summary>
    private static string RowText(ServerListing s)
    {
        if (s.MissionName != "manual")
            return s.FormatRow();
        string addr = (string.IsNullOrWhiteSpace(s.Address) ? "?" : s.Address)
                      + ":" + (s.Port > 0 ? s.Port.ToString(CultureInfo.InvariantCulture) : "?");
        string name = string.IsNullOrWhiteSpace(s.Name) ? addr : s.Name;
        return $"{name}   {addr}   manual";
    }
}
