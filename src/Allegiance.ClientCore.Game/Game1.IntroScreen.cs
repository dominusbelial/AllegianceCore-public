using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using static Allegiance.ClientCore.Training.TrainingMissionIds;
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
    /// models each as a view of the same screen). TR-2 splits Single Player into a
    /// Training | Skirmish chooser (native's big single-player button trains,
    /// introscreen.cpp:1221/:1558) plus a first-run training-suggestion dialog.
    /// P785 adds the training slideshow screens (native ScreenIDTrainSlideshow /
    /// ScreenIDPostTrainSlideshow, Treki.h:31-32 — the port uses one view whose
    /// dismiss behaviour follows the slideshow kind).</summary>
    private enum IntroView { Main, FindServer, DirectConnect, SinglePlayer, SkirmishSetup, Training, TrainingSlideshow, TrainingSuggest, Credits, Benchmark, BenchmarkOffer, Settings, Help, Career }

    private IntroView _introView = IntroView.Main;
    private int _introIndex;

    /// <summary>TR-2 — single-player entry point: the Training | Skirmish chooser.</summary>
    private readonly SinglePlayerChooserModel _singlePlayerChooser = new();

    /// <summary>TR-2 — the training mission list (Basic Flight playable in TR-2).</summary>
    private readonly TrainingScreenModel _trainingScreen = new();

    /// <summary>TR-2 — "don't ask me again" tick on the first-run training suggestion.</summary>
    private bool _trainingSuggestDontAsk;
    private bool _trainingSuggestHandled;
    private bool _trainingDebugLaunched;

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

    // P757 — the splash screen (native WinTrek/splashscreen.cpp). The native
    // constructor arms a timeout that advances to the intro screen — 0.01 s
    // because the released build's `g_bQuickstart || true` REVIEW comment makes
    // the quickstart branch unconditional (else 6.85 s) — and ANY key or click
    // skips straight there (OnKey/OnButton, splashscreen.cpp:34-80).
    private double _splashStartSeconds;

    /// <summary>Native's effective splash timeout in seconds (splashscreen.cpp:36-39:
    /// <c>AddEventTarget(&amp;OnTimeout, GetWindow(), 0.01f)</c> — the quickstart
    /// branch is always taken because of the <c>g_bQuickstart || true</c>
    /// REVIEW).</summary>
    public const double SplashTimeoutSeconds = 0.01;

    /// <summary>Enter the splash state (boot, or the intro menu's Replay Intro —
    /// native <c>ScreenIDSplashScreen</c>).</summary>
    private void BeginSplash()
    {
        _state = ClientState.Splash;
        _splashStartSeconds = _nowSeconds;
        Console.WriteLine("[splash] splash screen (timeout to intro)");
    }

    /// <summary>Native <c>SplashScreen</c>: the timeout (or any key/click)
    /// advances to the intro screen (splashscreen.cpp:34-46, 63-80).</summary>
    private void UpdateSplash(KeyboardState ks)
    {
        bool anyKey = ks.GetPressedKeyCount() > 0;
        bool click = Mouse.GetState().LeftButton == ButtonState.Pressed;
        if (_nowSeconds - _splashStartSeconds >= SplashTimeoutSeconds || anyKey || click)
        {
            _state = ClientState.Intro;
            InitIntro();
            Console.WriteLine("[splash] -> intro screen");
        }
    }

    private void DrawSplash()
    {
        var vp = GraphicsDevice.Viewport;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            var title = _hudBig.MeasureString("ALLEGIANCE");
            _spriteBatch.DrawString(_hudBig, "ALLEGIANCE", new Vector2((vp.Width - title.X) / 2f, vp.Height * 0.42f),
                new Color(0.38f, 0.90f, 0.84f));
            var sub = _hudSmall.MeasureString("loading...");
            _spriteBatch.DrawString(_hudSmall, "loading...", new Vector2((vp.Width - sub.X) / 2f, vp.Height * 0.42f + 70f),
                new Color(0.58f, 0.74f, 0.72f));
        }
        finally { _spriteBatch.End(); }
    }

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
            _directBuf.Append(IntroScreenModel.DirectAddress(prefs.Server, prefs.Port));
        }
        _introView = _introProofDelayMs >= 0 && Environment.GetEnvironmentVariable("ALLEGIANCE_SKIRMISH_SETUP_PROOF") == "1"
            ? IntroView.SkirmishSetup : IntroView.Main;
        _introIndex = 0;
        // TR-2 — the native first-run training suggestion (introscreen.cpp:1305): shown once
        // until the player trains or ticks "don't ask me again" (Ui.ShowStartupTrainingSuggestion).
        _trainingSuggestDontAsk = false;
        _trainingSuggestHandled = false;
        if (_introView == IntroView.Main && prefs.ShowStartupTrainingSuggestion)
        {
            _introView = IntroView.TrainingSuggest;
            Console.WriteLine("[intro] first-run training suggestion shown (Ui.ShowStartupTrainingSuggestion)");
        }
        Console.WriteLine($"[intro] remembered pilot '{_playerName}', server '{prefs.Server}:{prefs.Port}'");
    }

    private void UpdateIntro(KeyboardState ks)
    {
        UpdateIdentity(ks);
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
                _autoShotName = _introView == IntroView.SinglePlayer ? "skirmish_setup_proof" : "intro_proof";
                _autoShotScheduled = true;
                _introProofWaitExit = true;
                Console.WriteLine("[intro] ALLEGIANCE_AUTOSHOT: intro proof queued");
            }
        }

        // TR-5 — native Free Flight (Mission 10) has no training-screen button; it is a dev/debug
        // mission launched through the bStartTraining flag (trainingscreen.cpp:105-108, m_iMissionNext
        // set to c_TM_10_Free_Flight). Match that: ALLEGIANCE_TRAIN_MISSION=&lt;id&gt; force-starts that
        // mission once from the intro (keeps Free Flight off the chooser, as native does).
        if (!_trainingDebugLaunched && Environment.GetEnvironmentVariable("ALLEGIANCE_TRAIN_MISSION") is { Length: > 0 } dbg
            && int.TryParse(dbg, out int dbgMission))
        {
            _trainingDebugLaunched = true;
            Console.WriteLine($"[intro] ALLEGIANCE_TRAIN_MISSION={dbgMission} -> starting training mission (native bStartTraining path)");
            StartTrainingMission(dbgMission);
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
                if (tab && !_iTab && _identityScreen.Account is null) { _introEditingPilot = !_introEditingPilot; }
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

            case IntroView.SinglePlayer:   // TR-2 — the Training | Skirmish chooser
                UpdateSinglePlayerChooser(ks, up, down, enter, esc);
                break;
            case IntroView.SkirmishSetup: // P727 — the skirmish options behind "Skirmish"
                UpdateSkirmishMenu(ks, up, down, enter, esc);
                break;
            case IntroView.Training:      // TR-2 — the native training mission list
                UpdateTrainingScreen(ks, up, down, enter, esc);
                break;
            case IntroView.TrainingSlideshow: // P785 — native ScreenIDTrainSlideshow/PostTrainSlideshow
                UpdateTrainingSlideshow(ks);
                break;
            case IntroView.TrainingSuggest: // TR-2 — the one-shot first-run suggestion dialog
                UpdateTrainingSuggest(ks, enter, esc);
                break;
            case IntroView.Settings:
                UpdateOptionsMenu(ks);
                break;
            case IntroView.Help:
                // P757 — the manual (native HelpPane: topic nav + Back history +
                // page scroll, effect/help.cpp:225-296).
                if (up && !_iUp) _helpManual.MoveTopicSelection(-1);
                if (down && !_iDown) _helpManual.MoveTopicSelection(+1);
                if (ks.IsKeyDown(XnaKeys.Back) && !_iBack) _helpManual.Back();
                if (ks.IsKeyDown(XnaKeys.PageUp) && !_iPgUp) _helpManual.ScrollBy(-6, 14);
                if (ks.IsKeyDown(XnaKeys.PageDown) && !_iPgDn) _helpManual.ScrollBy(+6, 14);
                if (esc && !_iEsc) { _introView = IntroView.Main; _introIndex = 7; }
                break;
            case IntroView.Benchmark:
            case IntroView.BenchmarkOffer:
                UpdateBenchmarkMenu(up, down, enter, esc);
                break;
            case IntroView.Credits:
                if (esc && !_iEsc) { _introView = IntroView.Main; _introIndex = 0; }
                break;
            case IntroView.Career:
                UpdateCareerScreen(ks, up, down, enter, esc, tab);
                break;
        }

        HandleIntroBackspace(ks);

        _iUp = up; _iDown = down; _iEnter = enter; _iEsc = esc; _iTab = tab;
        _iBack = ks.IsKeyDown(XnaKeys.Back); _iPgUp = ks.IsKeyDown(XnaKeys.PageUp); _iPgDn = ks.IsKeyDown(XnaKeys.PageDown);
    }

    // P757 — extra intro edge latches (manual Back/scroll).
    private bool _iBack, _iPgUp, _iPgDn;
    // P806 — the native options MENU TREE (root + Sound/Game/Chat submenus,
    // WinTrek.cpp:4429-4442, 4502-4590) and the intro credits' scroll position
    // (native creditsTime, introscreen.cpp:643-653).
    private readonly OptionsMenuModel _optionsMenu = new();
    private float _introCreditsScroll;
    // P757 — the shared manual model (intro Help + the in-flight H overlay).
    private Rendering.HelpManualModel _helpManual = new();

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
            case IntroCommand.Options:        // native OnButtonOptions → ShowOptionsMenu (introscreen.cpp:1847-1851): the four-group options menu.
                _introView = IntroView.Settings;
                _optionsMenu.Reset();
                _optionsPrefsLoaded = false;   // re-read player.json once on open (P757b)
                EnsureOptionsPrefs();
                break;
            case IntroCommand.Benchmark:
                _introView = IntroView.Benchmark;
                break;
            case IntroCommand.Credits:
                _introView = IntroView.Credits;
                _introCreditsScroll = 0f;      // native creditsTime resets on open (introscreen.cpp:653)
                break;
            case IntroCommand.ReplayIntro:    // native OnButtonIntro → ScreenIDSplashScreen (introscreen.cpp:1865-1876).
                BeginSplash();
                break;
            case IntroCommand.Help:           // native OnButtonHelp → GetWindow()->OnHelp(true) (introscreen.cpp:1806-1810).
                _introView = IntroView.Help;
                _helpManual = new Rendering.HelpManualModel();
                Console.WriteLine("[intro] Help -> manual (hlpStart)");
                break;
            case IntroCommand.Exit:           // native OnButtonExit: GetWindow()->StartClose(), :1799.
                Exit();
                break;
            case IntroCommand.Career:         // P808c — career screens (port's lobby portal).
                EnterCareerScreen();
                break;
        }
    }

    // ── TR-2 — Single Player: Training | Skirmish chooser ──────────────────
    private void UpdateSinglePlayerChooser(KeyboardState ks, bool up, bool down, bool enter, bool esc)
    {
        if (up && !_iUp) _singlePlayerChooser.Move(-1);
        if (down && !_iDown) _singlePlayerChooser.Move(1);
        if (enter && !_iEnter)
        {
            _introView = _singlePlayerChooser.Activate() ? IntroView.Training : IntroView.SkirmishSetup;
            Console.WriteLine($"[intro] Single Player -> {(_singlePlayerChooser.Activate() ? "Training" : "Skirmish")}");
        }
        if (esc && !_iEsc) { _introView = IntroView.Main; _introIndex = 0; }
    }

    // ── TR-2 — the training mission list (every mission playable; P785 adds TM1) ──
    private void UpdateTrainingScreen(KeyboardState ks, bool up, bool down, bool enter, bool esc)
    {
        if (up && !_iUp) _trainingScreen.Move(-1);
        if (down && !_iDown) _trainingScreen.Move(1);
        if (enter && !_iEnter)
        {
            if (_trainingScreen.CanStart)
                StartTrainingMission(_trainingScreen.Selected.MissionId);
            else
                Console.WriteLine($"[training] mission {_trainingScreen.Selected.MissionId} locked (coming soon)");
        }
        if (esc && !_iEsc)
        {
            // P785 — native OnButtonBack (trainingscreen.cpp:160-164): the back button
            // goes to ScreenIDIntroScreen (the main menu), NOT the chooser.
            _introView = IntroView.Main;
            _introIndex = 0;
        }
    }

    /// <summary>
    /// TR-2 — begin a native training mission on the embedded single-player host
    /// (training host mode, P727 + TR-1). P785: missions 2-8 first show the native
    /// pre-mission slideshow (trainingscreen.cpp:187-198 → ScreenIDTrainSlideshow; its
    /// Dismiss starts the mission, TrainingSlideshow.cpp:30-50); TM1 is the Introduction
    /// slideshow; Free Flight (10) skips it (trainingscreen.cpp:198-206).
    /// </summary>
    private void StartTrainingMission(int missionId)
    {
        if (missionId == Tm1Introduction)
            BeginTrainingMission(missionId);   // routes to the tm_1_introduction (post) slideshow
        else if (missionId == Tm10FreeFlight)
            BeginTrainingMission(missionId);   // native skips the slideshow for Free Flight
        else
        {
            var name = TrainingScreenModel.NameOf(missionId);
            BeginTrainingSlideshow(TrainingSlides.PreMission(missionId, name), isPost: false, missionId);
        }
    }

    // ── TR-2 — the one-shot first-run training suggestion dialog ───────────
    private void UpdateTrainingSuggest(KeyboardState ks, bool enter, bool esc)
    {
        // Tab toggles the "don't ask me again" checkbox; Enter accepts (begin training);
        // Esc declines (clear the flag, go to the main menu).
        bool tab = ks.IsKeyDown(XnaKeys.Tab);
        if (tab && !_iTab && _identityScreen.Account is null) _trainingSuggestDontAsk = !_trainingSuggestDontAsk;
        if ((enter || esc) && !_trainingSuggestHandled)
        {
            _trainingSuggestHandled = true;
            ClearTrainingSuggestionFlag();
            if (enter)
            {
                _introView = IntroView.SinglePlayer;      // jump into training
                _singlePlayerChooser.Selected = 0;        // Training highlighted
            }
            else
            {
                _introView = IntroView.Main;
                _introIndex = 0;
            }
            Console.WriteLine($"[intro] training suggestion dismissed (begin={enter}, dontAsk={_trainingSuggestDontAsk})");
        }
    }

    /// <summary>Clear the first-run training-suggestion flag (native introscreen.cpp:1055).</summary>
    private void ClearTrainingSuggestionFlag()
    {
        var prefs = IntroPreferences.Load();
        prefs.ShowStartupTrainingSuggestion = false;
        prefs.Save();
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
                        Version: s.Version, DisplayName: "", Verified: s.Verified, IdentityServerId: s.IdentityServerId));
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
            // P757 — join what the filtered, sorted list shows at this cursor.
            var list = IntroFindServerList.FilterAndSort(_introFound);
            if (list.Count == 0) return;
            target = list[Math.Min(_introFoundIndex, list.Count - 1)];
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
        _spriteBatch.Begin(_introView is IntroView.Benchmark or IntroView.BenchmarkOffer ? SpriteSortMode.Immediate : SpriteSortMode.Deferred,
            BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        try
        {
            var titleCol = new Color(HudColor.TealAccent.R, HudColor.TealAccent.G, HudColor.TealAccent.B);
            var dimCol = new Color(0.48f, 0.64f, 0.62f);
            var textCol = new Color(0.88f, 0.96f, 0.95f);

            switch (_introView)
            {
                case IntroView.Settings: DrawPrivacySettings(w, h, titleCol, dimCol); break;
                case IntroView.Benchmark:
                case IntroView.BenchmarkOffer: DrawBenchmarkMenu(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Main: DrawIntroMain(w, h, titleCol, dimCol, textCol); break;
                case IntroView.FindServer: DrawIntroFindServer(w, h, titleCol, dimCol, textCol); break;
                case IntroView.DirectConnect: DrawIntroDirect(w, h, titleCol, dimCol, textCol); break;
                case IntroView.SinglePlayer: DrawIntroChooser(w, h, titleCol, dimCol, textCol); break;
                case IntroView.SkirmishSetup: DrawIntroSkirmishSetup(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Training: DrawIntroTraining(w, h, titleCol, dimCol, textCol); break;
                case IntroView.TrainingSlideshow: DrawIntroSlideshow(w, h, titleCol, dimCol, textCol); break;
                case IntroView.TrainingSuggest: DrawIntroTrainingSuggest(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Credits: DrawIntroCredits(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Help: DrawIntroHelp(w, h, titleCol, dimCol, textCol); break;
                case IntroView.Career: DrawCareerScreen(w, h, titleCol, dimCol, textCol); break;
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
        var nameLabel = _identityScreen.PilotLabel(_pilotBuf.ToString() + (_introEditingPilot ? "_" : ""));
        var nsz = _hudSmall.MeasureString(nameLabel);
        _spriteBatch.DrawString(_hudSmall, nameLabel, new Vector2(cx - nsz.X / 2f, lay.PilotY),
            _introEditingPilot ? titleCol : dimCol);

        var identityLine = _identityScreen.ApprovalLine;
        var identitySize = _hudSmall.MeasureString(identityLine);
        float identityScale = Math.Min(1f, (w - 40f) / Math.Max(1f, identitySize.X));
        _spriteBatch.DrawString(_hudSmall, identityLine, new Vector2((w - identitySize.X * identityScale) / 2f, lay.PilotY - 48),
            titleCol, 0, Vector2.Zero, identityScale, SpriteEffects.None, 0);

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
            // P757 — native FindGames post-processing on every display pass:
            // dedup + the max-player rule + sorted by game name
            // (introscreen.cpp:903-995).
            list = IntroFindServerList.FilterAndSort(_introFound);
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

    private void DrawIntroSkirmishSetup(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Single Player", "Skirmish", titleCol, dimCol);
        var rows = _skirmishScreen.Rows;
        var layout = IntroLayout.Compute(w, h, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var label = (i == _skirmishScreen.Selected ? "> " : "  ") + rows[i];
            var size = _hudSmall.MeasureString(label);
            _spriteBatch.DrawString(_hudSmall, label, new Vector2((w - size.X) / 2, layout.Rows[i].Y + 4),
                i == _skirmishScreen.Selected ? titleCol : textCol);
        }
        string message = _skirmishStart is not null ? "Starting local game... ESC cancels" : _skirmishError;
        if (message.Length == 0) message = _skirmishScreen.Options.Warning;
        if (message.Length == 0) message = _skirmishScreen.Options.Commander == Allegiance.SinglePlayer.SkirmishCommander.You
            ? "You command side 0. I: invest. F3: command view. Right-click: orders."
            : "Watch the battle or fly for Alpha. AI commanders manage both teams.";
        _spriteBatch.DrawString(_hudSmall, message, new Vector2(24, layout.PilotY), Color.Orange);
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   LEFT/RIGHT change   ENTER choose   ESC back",
            new Vector2(24, layout.FooterY), dimCol);
    }

    // ── TR-2 — the Single Player chooser (Training | Skirmish) ─────────────
    private void DrawIntroChooser(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Single Player", "Training or Skirmish", titleCol, dimCol);
        var rows = _singlePlayerChooser.Rows;
        var layout = IntroLayout.Compute(w, h, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var label = (i == _singlePlayerChooser.Selected ? "> " : "  ") + rows[i];
            var size = _hudSmall.MeasureString(label);
            _spriteBatch.DrawString(_hudSmall, label, new Vector2((w - size.X) / 2, layout.Rows[i].Y + 4),
                i == _singlePlayerChooser.Selected ? titleCol : textCol);
        }
        var fsz = _hudSmall.MeasureString("UP/DOWN select   ENTER choose   ESC back");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER choose   ESC back",
            new Vector2((w - fsz.X) / 2f, layout.FooterY), dimCol);
    }

    // ── TR-2 — the training screen (native training mission list) ──────────
    private void DrawIntroTraining(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Training", "Choose a mission", titleCol, dimCol);
        var rows = _trainingScreen.Rows();
        var layout = IntroLayout.Compute(w, h, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var label = (i == _trainingScreen.Index ? "> " : "  ") + rows[i];
            var size = _hudSmall.MeasureString(label);
            _spriteBatch.DrawString(_hudSmall, label, new Vector2((w - size.X) / 2, layout.Rows[i].Y + 4),
                i == _trainingScreen.Index ? titleCol : textCol);
        }
        // Show the selected mission's description (native missionInfoImage art; port text).
        string desc = _trainingScreen.Selected.Description;
        var dsz = _hudSmall.MeasureString(desc);
        _spriteBatch.DrawString(_hudSmall, desc, new Vector2((w - dsz.X) / 2f, layout.PilotY),
            _trainingScreen.CanStart ? Color.Orange : dimCol);
        var fsz = _hudSmall.MeasureString("UP/DOWN select   ENTER start   ESC back");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER start   ESC back",
            new Vector2((w - fsz.X) / 2f, layout.FooterY), dimCol);
    }

    // ── P785 — the training slideshow (native Slideshow/TrainingSlideshow/PostTrainingSlideshow).
    // Native fills the screen with each slide's full-screen image; the port renders the
    // slide's native narration text wrapped (visual parity — no tm_* slide art ships in
    // the Content pack).
    private void DrawIntroSlideshow(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        var slide = _trainingSlideshow?.Current;
        if (slide is { } s)
        {
            float textWidth = MathF.Min(w * 0.8f, 900f);
            var lines = WrapText(s.Caption.Replace("\n", " "), textWidth);
            var tsz = _hudSmall.MeasureString(lines.Count > 0 ? lines[0] : "");
            float top = (h - lines.Count * (tsz.Y + 6f)) / 2f;
            for (int i = 0; i < lines.Count; i++)
            {
                var size = _hudSmall.MeasureString(lines[i]);
                _spriteBatch.DrawString(_hudSmall, lines[i], new Vector2((w - size.X) / 2f, top + i * (tsz.Y + 6f)), titleCol);
            }
        }
        var hint = $"{_trainingSlideshow?.Title}   {( (_trainingSlideshow?.Index ?? 0) + 1)}/{_trainingSlideshow?.Count ?? 0}   any key: next";
        var hsz = _hudSmall.MeasureString(hint);
        _spriteBatch.DrawString(_hudSmall, hint, new Vector2((w - hsz.X) / 2f, h * 0.92f), dimCol);
    }

    // ── P808c — the career screens (character info + leaderboard) ─────────
    private void DrawCareerScreen(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        CareerScreenRenderer.Draw(_spriteBatch, _hudSmall, _hudBig, _whitePixel, _hudSettings,
            _characterInfo, _leaderboard, _squads, _careerMode == CareerMode.Leaderboard,
            _careerMode == CareerMode.Squads, _careerCursor,
            _careerEditingDesc, _careerDescBuf.ToString(), _careerStatus, _careerViewAccount, w, h);
    }

    /// <summary>Greedy word wrap to the pixel width (the intro slideshow's caption
    /// stand-in; native wraps via StringImage(JustifyLeft, 500, …), tm_1_introduction.mdl).</summary>
    private List<string> WrapText(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (_hudSmall.MeasureString(candidate).X > maxWidth && current.Length > 0)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0 || words.Length == 0) lines.Add(current);
        }
        return lines;
    }

    // ── TR-2 — the one-shot first-run training suggestion dialog ───────────
    private void DrawIntroTrainingSuggest(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Welcome to Allegiance", "Training is strongly recommended before going online.", titleCol, dimCol);
        string[] lines =
        {
            "ALLEGIANCE is a team-based massively-multiplayer space combat game,",
            "in which your goal is to conquer the universe.",
            "",
            "Training is strongly recommended before going online, and will take",
            "approximately 30 minutes. Press ENTER to begin training now.",
            "",
            ( _trainingSuggestDontAsk ? "[x]" : "[ ]" ) + " Don't ask me again",
        };
        float y = h * 0.36f;
        foreach (var ln in lines)
        {
            var sz = _hudSmall.MeasureString(ln);
            _spriteBatch.DrawString(_hudSmall, ln, new Vector2((w - sz.X) / 2f, y),
                ln.Contains("Don't ask") ? ( _trainingSuggestDontAsk ? titleCol : dimCol ) : textCol);
            y += 30f;
        }
        var fsz = _hudSmall.MeasureString("ENTER begin training   TAB don't-ask-again   ESC dismiss");
        _spriteBatch.DrawString(_hudSmall, "ENTER begin training   TAB don't-ask-again   ESC dismiss",
            new Vector2((w - fsz.X) / 2f, h - 40f), dimCol);
    }

    private void DrawIntroCredits(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Credits", "Allegiance  -  a 20-year labour of love", titleCol, dimCol);
        // P757 — native CreditsPopup scrolls its content over time (the
        // creditsTime number drives the pane's animation, introscreen.cpp:643-653).
        _introCreditsScroll += 0.35f;   // px/frame at 60 fps ≈ 21 px/s — a slow roll
        string[] lines =
        {
            "Original game: Microsoft Research / Allegiance team.",
            "Community: FreeAllegiance.",
            "This port: AllegianceCore (P-series).",
            "",
            "Press ESC to return to the main menu.",
        };
        float y = h * 0.4f - (_introCreditsScroll % (lines.Length * 30f + h * 0.3f));
        foreach (var ln in lines)
        {
            if (y > h * 0.24f && y < h * 0.9f)
            {
                var sz = _hudSmall.MeasureString(ln);
                _spriteBatch.DrawString(_hudSmall, ln, new Vector2((w - sz.X) / 2f, y), textCol);
            }
            y += 30f;
        }
    }

    /// <summary>P757 — the intro's Help view: the manual's topic bar + the
    /// current page, with the native navigation (topic Up/Down, Back history,
    /// page scroll — effect/help.cpp:225-296).</summary>
    private void DrawIntroHelp(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Help", "The manual - UP/DOWN topics   BACKSPACE back   PGUP/PGDN scroll   ESC close", titleCol, dimCol);

        // Topic bar (native nav pane).
        float tx = 40f;
        foreach (var t in Rendering.HelpManualModel.Topics)
        {
            bool cur = t.Id == _helpManual.CurrentTopic;
            var label = (cur ? "[" : " ") + t.Title + (cur ? "]" : " ");
            _spriteBatch.DrawString(_hudSmall, label, new Vector2(tx, 112f), cur ? titleCol : dimCol);
            tx += _hudSmall.MeasureString(t.Title).X + 34f;
        }

        // Current page (native main page + scroll).
        var topic = _helpManual.Current;
        float y = 160f;
        int skip = _helpManual.Scroll;
        for (int i = 0; i < topic.Lines.Count; i++)
        {
            if (i < skip) continue;
            if (y > h - 60f) break;
            _spriteBatch.DrawString(_hudSmall, topic.Lines[i], new Vector2(60f, y), textCol);
            y += 26f;
        }
        if (_helpManual.HistoryDepth > 1)
            _spriteBatch.DrawString(_hudSmall, "BACKSPACE: back", new Vector2(60f, h - 46f), dimCol);
    }

    // Guards the shared lobby list (_servers) between the main thread
    // (ConsumeLobbyResultIntoIntro) and the background Find Server scan thread
    // (MergeIntroLobby).
    private readonly object _introLobbyLock = new();
}
