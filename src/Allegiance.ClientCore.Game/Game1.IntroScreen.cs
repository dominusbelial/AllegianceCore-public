using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P731  -  the intro screen: "choose single player or multiplayer at start".
/// Faithful port of native's <c>WinTrek/introscreen.cpp</c> layout and flow
/// (each element cites its native source below), using the port's 2D UI idiom.
/// With no command-line arguments the client opens here (P644/P726's desktop
/// shortcut); command-line arguments still bypass it entirely.
/// </summary>
public sealed partial class Game1
{
    /// <summary>Which intro sub-view is showing (native moved between the main
    /// screen and its popups  -  FindServerPopup/CreditsPopup/Options; the port
    /// models each as a view of the same screen).</summary>
    private enum IntroView { Main, FindServer, DirectConnect, SinglePlayer, Credits }

    private IntroView _introView = IntroView.Main;
    private int _introIndex;

    /// <summary>Native's main-menu button slots and the port's labels. Citations:
    /// SinglePlayer←trainBigButtonPane (:1221)/OnButtonTraining(:1558);
    /// PlayOnline←playIntButtonPane (:1213)/OnButtonInternet(:1822);
    /// PlayLan←playLanButtonPane (:1212)/OnButtonLAN(:1832);
    /// DirectConnect is a MODERN addition (native had no direct-IP field);
    /// Options←optionsButtonPane (:1223)/OnButtonOptions(:1847);
    /// Credits←creditsButtonPane (:1225)/OnButtonCredits(:1853);
    /// Exit←exitButtonPane (:1238)/OnButtonExit(:1799).</summary>
    private static readonly string[] IntroMainLabels = IntroScreenModel.MainLabels;

    private readonly StringBuilder _pilotBuf = new();
    private readonly StringBuilder _directBuf = new();
    private bool _introEditingPilot = true;   // pilot name is the always-on field
    private bool _introEditingDirect;

    // ── Find Server state ──────────────────────────────────────────────────
    private readonly List<DiscoveredServer> _introFound = new();
    private readonly object _introFoundLock = new();
    private DiscoveryEngine? _introEngine;
    private CancellationTokenSource? _introScanCts;
    private Task? _introScanTask;
    private bool _introScanRunning;
    private string _introFindError = "";
    private int _introFoundIndex;

    // edge latches (main-thread key state)
    private bool _iUp, _iDown, _iEnter, _iEsc, _iTab;

    // P731  -  one-shot real-window proof for the intro screen: ALLEGIANCE_AUTOSHOT
    // (the same env var the in-flight P559 hook uses) requests one real-backbuffer
    // screenshot this many ms after the intro screen appears, then exits.
    private long _introProofDelayMs = -1;
    private long _introProofStartMs;
    private bool _introProofWaitExit;

    /// <summary>Load remembered values (player.json, P731) into the intro screen.</summary>
    private void InitIntro()
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_AUTOSHOT"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var delay) && delay >= 0)
        {
            _introProofDelayMs = delay;
            _introProofStartMs = Environment.TickCount64;
        }
        var prefs = IntroPreferences.Load();
        if (!string.IsNullOrWhiteSpace(prefs.Pilot))
        {
            _pilotBuf.Clear();
            _pilotBuf.Append(prefs.Pilot);
            _playerName = prefs.Pilot;
        }
        if (prefs.Lobby.Length > 0) _lobbyUrl = prefs.Lobby;
        _directBuf.Clear();
        if (!string.IsNullOrWhiteSpace(prefs.Server))
        {
            _directBuf.Append(prefs.Server);
            if (prefs.Port > 0 && _directBuf.Length == 0)
                _directBuf.Append(prefs.Port);
        }
        _introView = IntroView.Main;
        _introIndex = 0;
        Console.WriteLine($"[intro] remembered pilot '{_playerName}', server '{prefs.Server}:{prefs.Port}'");
    }

    private void UpdateIntro(KeyboardState ks)
    {
        // P731  -  one-shot proof: schedule the real-backbuffer screenshot after
        // the delay, then exit once the Draw-end flush has written it.
        if (_introProofDelayMs >= 0)
        {
            if (_introProofWaitExit)
            {
                if (!_autoShotScheduled) { Exit(); return; }   // flushed; leave
            }
            else if (_introProofStartMs > 0
                && Environment.TickCount64 - _introProofStartMs >= _introProofDelayMs)
            {
                _autoShotName = "intro_proof";
                _autoShotScheduled = true;
                _introProofWaitExit = true;
                Console.WriteLine("[intro] ALLEGIANCE_AUTOSHOT: intro proof queued");
            }
        }

        // Drive the (reused) lobby fetch so the Find Server list can merge the
        // online lobby over the VPN/network too.
        ConsumeLobbyResultIntoIntro();
        if (!_lobbyFetchInFlight && _lobbyUrl.Length > 0 && _nowSeconds - _lastLobbyFetch >= LobbyRefreshSeconds)
        {
            _lastLobbyFetch = _nowSeconds;
            _lobbyFetchInFlight = true;
            _ = FetchServersAsync();
        }

        bool up = ks.IsKeyDown(XnaKeys.Up), down = ks.IsKeyDown(XnaKeys.Down);
        bool enter = ks.IsKeyDown(XnaKeys.Enter), esc = ks.IsKeyDown(XnaKeys.Escape);
        bool tab = ks.IsKeyDown(XnaKeys.Tab);

        switch (_introView)
        {
            case IntroView.Main:
                if (tab && !_iTab) { _introEditingPilot = !_introEditingPilot; }
                if (up && !_iUp && !_introEditingPilot)
                    _introIndex = (_introIndex - 1 + IntroMainLabels.Length) % IntroMainLabels.Length;
                if (down && !_iDown && !_introEditingPilot)
                    _introIndex = (_introIndex + 1) % IntroMainLabels.Length;
                if (enter && !_iEnter) ActivateIntroMain();
                break;

            case IntroView.FindServer:
                if (up && !_iUp)
                {
                    lock (_introFoundLock) if (_introFound.Count > 0)
                        _introFoundIndex = (_introFoundIndex - 1 + _introFound.Count) % _introFound.Count;
                }
                if (down && !_iDown)
                {
                    lock (_introFoundLock) if (_introFound.Count > 0)
                        _introFoundIndex = (_introFoundIndex + 1) % _introFound.Count;
                }
                if (enter && !_iEnter) JoinIntroFound();
                if (esc && !_iEsc) LeaveIntroFindServer();
                break;

            case IntroView.DirectConnect:
                if (enter && !_iEnter) ConnectDirectIntro();
                if (esc && !_iEsc) { _introView = IntroView.Main; _introIndex = 3; }
                break;

            case IntroView.SinglePlayer:
            case IntroView.Credits:
                if (esc && !_iEsc) { _introView = IntroView.Main; _introIndex = 0; }
                break;
        }

        HandleIntroBackspace(ks);

        _iUp = up; _iDown = down; _iEnter = enter; _iEsc = esc; _iTab = tab;
    }

    private void HandleIntroBackspace(KeyboardState ks)
    {
        bool back = ks.IsKeyDown(XnaKeys.Back);
        // Track a hold so holding Back repeats (simple repeat, like native edit).
        if (!back) { _iBackRepeat = 0; return; }
        _iBackRepeat++;
        if (_iBackRepeat == 1 || (_iBackRepeat > 20 && _iBackRepeat % 4 == 0))
        {
            if (_introEditingPilot && _introView == IntroView.Main && _pilotBuf.Length > 0)
                _pilotBuf.Length--;
            else if (_introEditingDirect && _introView == IntroView.DirectConnect && _directBuf.Length > 0)
                _directBuf.Length--;
        }
    }
    private int _iBackRepeat;

    private void ActivateIntroMain()
    {
        switch (IntroScreenModel.Activate(_introIndex))
        {
            case IntroCommand.SinglePlayer:   // → skirmish entry hook (P727 implements the skirmish).
                _introView = IntroView.SinglePlayer;
                break;
            case IntroCommand.Lobby:          // Play Online → the port's lobby server browser.
                if (string.IsNullOrEmpty(_lobbyUrl)) _lobbyUrl = ServerListing.LobbyUrlDefault;
                _state = ClientState.Lobby;
                Console.WriteLine("[intro] Play Online -> lobby server browser");
                break;
            case IntroCommand.FindServer:     // Play LAN / Find Server → merged discovery list.
                EnterIntroFindServer();
                break;
            case IntroCommand.DirectConnect:  // modern addition.
                _introView = IntroView.DirectConnect;
                _introEditingDirect = true;
                break;
            case IntroCommand.Options:        // the existing settings (Ctrl+F6 key-bindings popup).
                ToggleKeyBindings();
                break;
            case IntroCommand.Credits:
                _introView = IntroView.Credits;
                break;
            case IntroCommand.Exit:           // native OnButtonExit: GetWindow()->StartClose(), :1799.
                Exit();
                break;
        }
    }

    private void EnterIntroFindServer()
    {
        _introView = IntroView.FindServer;
        _introFindError = "";
        lock (_introFoundLock) { _introFound.Clear(); _introFoundIndex = 0; }
        _introEngine?.Dispose();
        _introEngine = new DiscoveryEngine();
        _introScanRunning = true;
        _introScanCts = new CancellationTokenSource();
        var ct = _introScanCts.Token;
        _introScanTask = Task.Run(() => IntroScanLoop(ct));
        Console.WriteLine("[intro] Find Server: discovery scan started (LAN broadcast/multicast, Tailscale, subnet, favorites, lobby)");
    }

    private void LeaveIntroFindServer()
    {
        StopIntroScan();
        _introView = IntroView.Main;
        _introIndex = 2;
    }

    /// <summary>Background scan loop: run a full multi-source scan, publish
    /// incrementally, then re-scan on native's LAN poll cadence (1.0 s, native
    /// introscreen.cpp:824) while the view is open.</summary>
    private void IntroScanLoop(CancellationToken ct)
    {
        var coord = new DiscoveryCoordinator(_introEngine!);
        var opts = new DiscoveryScanOptions
        {
            Favorites = IntroPreferences.Load().Recent,
            OnFound = s =>
            {
                lock (_introFoundLock)
                {
                    // de-dupe against what's already shown
                    if (_introFound.All(e => e.HostPort != s.HostPort))
                        _introFound.Add(s);
                }
            },
        };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                coord.Scan(opts, ct);
                // Merge lobby listings (Play Online's directory) as Lobby entries.
                MergeIntroLobby();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _introFindError = ex.Message;
                break;
            }
            if (ct.WaitHandle.WaitOne((int)(DiscoveryDefaults.LanRefreshSeconds * 1000)))
                break;
        }
    }

    private void MergeIntroLobby()
    {
        List<ServerListing> servers;
        lock (_introLobbyLock)
        {
            servers = new List<ServerListing>(_servers);
        }
        lock (_introFoundLock)
        {
            foreach (var s in servers)
            {
                var id = $"{s.Address}:{s.Port}";
                if (_introFound.All(e => e.HostPort != id))
                    _introFound.Add(new DiscoveredServer(
                        Name: s.Name, Address: s.Address, Port: s.Port,
                        Source: DiscoverySource.Lobby, PingMs: 0,
                        Map: s.MissionName, Players: s.PlayerCount, MaxPlayers: s.MaxPlayers,
                        Version: s.Version, DisplayName: ""));
            }
        }
    }

    private void ConsumeLobbyResultIntoIntro()
    {
        if (!_lobbyResultPending) return;
        _lobbyResultPending = false;
        _lobbyResultError = null;
        if (_lobbyResultData is { } data)
        {
            lock (_introLobbyLock)
            {
                _servers.Clear();
                _servers.AddRange(data);
            }
        }
        _lobbyResultData = null;
    }

    private void JoinIntroFound()
    {
        DiscoveredServer? target;
        lock (_introFoundLock)
        {
            if (_introFound.Count == 0) return;
            target = _introFound[Math.Min(_introFoundIndex, _introFound.Count - 1)];
        }
        JoinFromIntro(target.Address, target.Port);
    }

    /// <summary>Common join path for every intro route: remember in player.json,
    /// stamp the pilot name, then connect.</summary>
    private void JoinFromIntro(string host, int port)
    {
        if (port <= 0) { _introFindError = "Server advertises no valid port."; return; }
        if (_pilotBuf.Length > 0) _playerName = _pilotBuf.ToString();
        var prefs = IntroPreferences.Load();
        prefs.Pilot = _playerName;
        prefs.Lobby = _lobbyUrl;
        prefs.Remember(host, port);
        prefs.Save();
        StopIntroScan();
        _returnToIntroOnFailure = true;   // failed joins come back to the intro
        Console.WriteLine($"[intro] joining {host}:{port} as '{_playerName}'");
        BeginConnect(host, (ushort)port);
    }

    /// <summary>Cancel + join the background Find Server scan (called when the
    /// player joins a server or leaves the view).</summary>
    private void StopIntroScan()
    {
        _introScanRunning = false;
        _introScanCts?.Cancel();
        try { _introScanTask?.Wait(200); } catch { /* ignore */ }
        _introScanTask = null;
        _introScanCts?.Dispose();
        _introScanCts = null;
        _introEngine?.Dispose();
        _introEngine = null;
    }

    private void ConnectDirectIntro()
    {
        if (!IntroScreenModel.TryParseDirect(_directBuf.ToString(), 6073, out var host, out var port))
            return;
        JoinFromIntro(host, port);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────
    private void DrawIntro()
    {
        var vp = GraphicsDevice.Viewport;
        float w = vp.Width, h = vp.Height;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var titleCol = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
            var dimCol = new Color(0.48f, 0.64f, 0.62f);
            var textCol = new Color(0.88f, 0.96f, 0.95f);

            switch (_introView)
            {
                case IntroView.Main: DrawIntroMain(w, h, titleCol, dimCol, textCol); break;
                case IntroView.FindServer: DrawIntroFindServer(w, h, titleCol, dimCol, textCol); break;
                case IntroView.DirectConnect: DrawIntroDirect(w, h, titleCol, dimCol, textCol); break;
                case IntroView.SinglePlayer: DrawIntroSinglePlayer(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Credits: DrawIntroCredits(w, h, titleCol, dimCol, textCol); break;
            }
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    private void DrawIntroTitle(float w, string title, string sub, Color titleCol, Color dimCol)
    {
        var tsz = _hudBig.MeasureString(title);
        _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, 28f), titleCol);
        if (sub.Length > 0)
        {
            var ssz = _hudSmall.MeasureString(sub);
            _spriteBatch.DrawString(_hudSmall, sub, new Vector2((w - ssz.X) / 2f, 82f), dimCol);
        }
    }

    private void DrawIntroMain(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "ALLEGIANCE", "Choose how to play  -  Single Player or Multiplayer", titleCol, dimCol);

        float cx = w / 2f;
        var lay = IntroLayout.Compute(w, h, IntroMainLabels.Length);
        float y = lay.Rows.Length > 0 ? lay.Rows[0].Y + 4f : h * 0.24f;
        float rowH = lay.RowHeight;
        for (int i = 0; i < IntroMainLabels.Length; i++)
        {
            string label = IntroMainLabels[i];
            var sz = _hudSmall.MeasureString(label);
            bool sel = i == _introIndex;
            if (sel)
            {
                _spriteBatch.Draw(_whitePixel, new Rectangle((int)lay.Rows[i].X, (int)lay.Rows[i].Y,
                        (int)lay.Rows[i].Width, (int)lay.Rows[i].Height),
                    new Color(0.08f, 0.35f, 0.33f, 0.55f));
            }
            _spriteBatch.DrawString(_hudSmall, label, new Vector2(cx - sz.X / 2f, y + 2f),
                sel ? titleCol : textCol);
            y += rowH;
        }

        // Pilot name entry (native remembered the last character name,
        // introscreen.cpp:1607; this is the port's pilot-name field).
        var nameLabel = "Pilot: " + _pilotBuf.ToString() + (_introEditingPilot ? "_" : "");
        var nsz = _hudSmall.MeasureString(nameLabel);
        _spriteBatch.DrawString(_hudSmall, nameLabel, new Vector2(cx - nsz.X / 2f, lay.PilotY),
            _introEditingPilot ? titleCol : dimCol);

        var fsz = _hudSmall.MeasureString("UP/DOWN select   ENTER choose   TAB edit pilot   ESC quit");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER choose   TAB edit pilot   ESC quit",
            new Vector2((w - fsz.X) / 2f, lay.FooterY), dimCol);
    }

    private void DrawIntroFindServer(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Find Server", "LAN / Tailscale / VPN subnet / favorites / lobby  -  select to join", titleCol, dimCol);

        bool scanning;
        List<DiscoveredServer> list;
        lock (_introFoundLock)
        {
            scanning = _introScanRunning;
            list = new List<DiscoveredServer>(_introFound);
            if (_introFoundIndex >= list.Count) _introFoundIndex = list.Count > 0 ? list.Count - 1 : 0;
        }

        if (list.Count == 0)
        {
            string msg = scanning ? "Searching..." : "No servers found.";
            var sz = _hudSmall.MeasureString(msg);
            _spriteBatch.DrawString(_hudSmall, msg, new Vector2((w - sz.X) / 2f, h * 0.4f),
                new Color(1f, 0.6f, 0.4f));
        }
        else
        {
            float y = 120f;
            float rowH = 26f;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                bool sel = i == _introFoundIndex;
                string line = (sel ? "> " : "  ") + s.FormatRow();
                if (sel)
                {
                    _spriteBatch.Draw(_whitePixel, new Rectangle(40, (int)y - 3, (int)(w - 80f), (int)(rowH - 6)),
                        new Color(0.08f, 0.35f, 0.33f, 0.55f));
                }
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(50f, y), sel ? titleCol : textCol);
                y += rowH;
            }
        }

        if (_introFindError.Length > 0)
            _spriteBatch.DrawString(_hudSmall, _introFindError, new Vector2(50f, 96f), new Color(1f, 0.45f, 0.35f));

        var fsz = _hudSmall.MeasureString("UP/DOWN select   ENTER join   ESC back (rescanning every 1 s)");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER join   ESC back (rescanning every 1 s)",
            new Vector2((w - fsz.X) / 2f, h - 40f), dimCol);
    }

    private void DrawIntroDirect(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Direct Connect", "Type an address[:port]  -  a hostname, IPv4/IPv6, or a MagicDNS name", titleCol, dimCol);

        var label = "Address: " + _directBuf.ToString() + (_introEditingDirect ? "_" : "");
        var lsz = _hudSmall.MeasureString(label);
        _spriteBatch.DrawString(_hudSmall, label, new Vector2((w - lsz.X) / 2f, h * 0.42f),
            _introEditingDirect ? titleCol : textCol);
        _spriteBatch.Draw(_whitePixel, new Rectangle((int)(w / 2f - 170), (int)(h * 0.42f) + 14, 340, 2),
            new Color(0.25f, 0.5f, 0.5f, 0.8f));

        var fsz = _hudSmall.MeasureString("TYPE address   ENTER connect   ESC back");
        _spriteBatch.DrawString(_hudSmall, "TYPE address   ENTER connect   ESC back",
            new Vector2((w - fsz.X) / 2f, h - 40f), dimCol);
    }

    private void DrawIntroSinglePlayer(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Single Player", "Skirmish", titleCol, dimCol);
        string[] lines =
        {
            "Skirmish mode is under construction (P727).",
            "Single-player training missions are planned as a later track.",
            "",
            "Press ESC to return to the main menu.",
        };
        float y = h * 0.4f;
        foreach (var ln in lines)
        {
            var sz = _hudSmall.MeasureString(ln);
            _spriteBatch.DrawString(_hudSmall, ln, new Vector2((w - sz.X) / 2f, y), textCol);
            y += 30f;
        }
    }

    private void DrawIntroCredits(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Credits", "Allegiance  -  a 20-year labour of love", titleCol, dimCol);
        string[] lines =
        {
            "Original game: Microsoft Research / Allegiance team.",
            "Community: FreeAllegiance.",
            "This port: AllegianceCore (P-series).",
            "",
            "Press ESC to return to the main menu.",
        };
        float y = h * 0.4f;
        foreach (var ln in lines)
        {
            var sz = _hudSmall.MeasureString(ln);
            _spriteBatch.DrawString(_hudSmall, ln, new Vector2((w - sz.X) / 2f, y), textCol);
            y += 30f;
        }
    }

    // Guards the shared lobby list (_servers) between the main thread
    // (ConsumeLobbyResultIntoIntro) and the background Find Server scan thread
    // (MergeIntroLobby).
    private readonly object _introLobbyLock = new();
}
