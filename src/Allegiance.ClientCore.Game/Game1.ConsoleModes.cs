using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private ConsolePaneState _consolePanes;
    private bool _hangarOnly;
    private bool _commandGridVisible = CommandGridPreference.Load(CommandGridPreference.DefaultPath);
    private bool _mainMenuVisible;
    private bool _menuConsumedFrame;
    private int _mainMenuCursor;
    private bool _playerPingsVisible;
    private long _playerPingsSeenRevision;
    private int _playerPingsPage;
    private const int PlayerPingsPageSize = 16;
    private readonly ActionPressEdges _consolePressEdges = new();
    // HUD-10 — native overlay-mask change detection + .5 s pane slides
    // (console.cpp:3268-3290,3400-3415; dialog.mdl:5355-5387).
    private readonly PaneSlideTracker _paneSlides = new();
    private ConsoleOverlayMask _lastOverlayMask;
    private bool _overlayMaskInitialized;
    private NativeComposeState _composeVisualState = NativeComposeState.NotComposing;
    // P770 review: the live compose state for sound decisions. _composeVisualState
    // only updates in UpdateConsoleOverlay and lags a frame (and stays Chat while the
    // pane slides out), so it is not native's m_csComposeState.
    // P784 — _composeState IS the live m_csComposeState now (all five states);
    // Quick stays derived from the quick-chat menu bool (the port's adaptation
    // of native StartQuickChat, console.cpp:1508-1532).
    private NativeComposeState LiveComposeState =>
        _qcMenuOpen ? NativeComposeState.Quick
        : _composeState;
    private readonly ActionPressEdges _playerPingsPressEdges = new();
    private readonly FlightCameraZoom _flightZoom = new();
    private static readonly string[] ConsoleActions = ["ConModeCombat", "ConModeGameState", "ConModeNav",
        "ConModeInventory", "TeamPane", "ConModeMiniTeam", "ViewExternal", "ToggleGrid"];
    private static readonly string[] MainMenuItems = ["Resume", "Help / quick reference", "Controls", "Credits / licences", "Game state", "Quit", "Settings"];

    private bool ConsoleEdge(KeyboardState keys, string action)
    {
        return _consolePressEdges.Observe(action, ActionDown(keys, action));
    }

    private void ApplyConsoleMode(string action, bool updatePointer = true)
    {
        // P784 — native OnSwitchViewMode quits only the MouseCommand compose
        // (console.cpp:1031-1035): switching console/camera views always ends
        // a mouse-command selection.
        if (_composeState == NativeComposeState.MouseCommand)
            SetComposeState(NativeComposeState.NotComposing);
        _consolePanes = ConsoleModeRules.Switch(_consolePanes with { Nav = _minimapExpanded,
            Team = _teamPaneVisible ? (_consolePanes.Team == TeamConsoleSize.Mini ? TeamConsoleSize.Mini : TeamConsoleSize.Expanded) : TeamConsoleSize.Hidden }, action);
        _minimapExpanded = _consolePanes.Nav;
        _teamPaneVisible = _consolePanes.Team != TeamConsoleSize.Hidden;
        if (action is "ConModeGameState" or "TeamPane" or "ConModeMiniTeam" or "ConModeCombat" or "ViewCommand" or "ViewExternal")
        { _investActive = _teleportActive = _scoreboardVisible = _pilotInfoVisible = false; }
        if (action is "ConModeCombat" or "ViewExternal")
        {
            _cameraMode = action == "ConModeCombat" || _cameraMode == CameraMode.External ? CameraMode.Cockpit : CameraMode.External;
            _directorActive = _freeCamActive = _orbitCamActive = false;
            _headLookYaw = 0;
            if (_dockedActive) _hangarOnly = true;
        }
        if (action == "ConModeInventory" && _dockedActive)
        {
            var dockedView = ConsoleModeRules.DockedInventory(_hangarOnly,
                _investActive || _teleportActive || _consolePanes.GameState || _teamPaneVisible);
            _hangarOnly = dockedView.HangarOnly;
            if (dockedView.ClosePanes)
            {
                _investActive = _teleportActive = _teamPaneVisible = false;
                _consolePanes = _consolePanes with { GameState = false, Team = TeamConsoleSize.Hidden };
            }
            _cameraMode = CameraMode.Cockpit;
        }
        if (updatePointer)
            IsMouseVisible = _mainMenuVisible || CommandCameraActive || _minimapExpanded || _dockedActive || !_mouseFlightEnabled;
        Console.WriteLine($"[p642] console {action}: {_consolePanes}, hangar={_hangarOnly}");
    }

    /// <summary>HUD-10 — compute the native overlay mask from the live port pane
    /// state and advance the native .5 s slides. A mask change is exactly when
    /// native <c>SetOverlayFlags</c> plays <c>paneSlideSound</c>
    /// (console.cpp:3272-3274; sounddef.mdl:323). Returns the mask.</summary>
    private ConsoleOverlayMask ComputeOverlayMask()
    {
        ConsoleOverlayMask mask = ConsoleOverlayMask.None;
        if (_state == ClientState.Flying)
        {
            mask |= ConsoleOverlayMask.InFlight;
            if (_dockedActive) mask |= ConsoleOverlayMask.InHangar;
            if (_cameraMode == CameraMode.Cockpit) mask |= ConsoleOverlayMask.InCockpit;
            else if (_cameraMode == CameraMode.Chase) mask |= ConsoleOverlayMask.InChase;
            if (CommandCameraActive)
                mask |= ConsoleOverlayMask.InFlightCommand | ConsoleOverlayMask.CommandPane;
            if (_consolePanes.Inventory) mask |= ConsoleOverlayMask.Inventory;
            if (_consolePanes.GameState) mask |= ConsoleOverlayMask.GameState;
            if (_teamPaneVisible)
                mask |= _consolePanes.Team == TeamConsoleSize.Mini
                    ? ConsoleOverlayMask.Team : ConsoleOverlayMask.ExpandedTeam;
            if (_investActive) mask |= ConsoleOverlayMask.Investment;
            if (_teleportActive) mask |= ConsoleOverlayMask.TeleportPane;
            if (_minimapExpanded) mask |= ConsoleOverlayMask.SectorPane;
        }
        return mask;
    }

    private void UpdateConsoleOverlay()
    {
        var mask = ComputeOverlayMask();
        bool changed = _paneSlides.Update(_nowSeconds, mask);
        // The compose panel slides on the compose state, not the overlay mask.
        // P784 — the mouse-command/command/shell states slide the pane too
        // (native SetComposeState drives ComposeState, dialog.mdl:5355-5387);
        // MouseCommand shows the chatmouse art exactly like native's
        // c_csComposeMouseCommand pane.
        _paneSlides.SetTarget(SlidingPane.ChatComposition, _nowSeconds,
            _composeState != NativeComposeState.NotComposing || _qcMenuOpen ? 1f : 0f);
        if (changed && _overlayMaskInitialized)
            _sound?.PlayPaneSlide();
        _overlayMaskInitialized = true;
        _lastOverlayMask = mask;

        // Keep the compose panel's visual state while it slides out (native
        // Show(false) animates the pane off screen rather than popping it).
        // P784 — every live compose state now drives the slide target and the
        // ComposingPane art variant; not just Chat/Quick.
        if (LiveComposeState != NativeComposeState.NotComposing)
            _composeVisualState = LiveComposeState;
        else if (_paneSlides.IsOff(SlidingPane.ChatComposition, _nowSeconds))
            _composeVisualState = NativeComposeState.NotComposing;
    }

    private float ChatComposeProgress =>
        _paneSlides.Progress(SlidingPane.ChatComposition, _nowSeconds);

    private void HandleConsoleKeys(KeyboardState keys, bool composing, float dt)
    {
        HandlePlayerPingsKey(keys, composing);
        foreach (var action in ConsoleActions)
            if (ConsoleEdge(keys, action) && !composing && _state == ClientState.Flying)
            {
                if (action == "ToggleGrid") ToggleCommandGrid();
                else ApplyConsoleMode(action);
            }
        if (!composing && _state == ClientState.Flying && _cameraMode == CameraMode.External && TryOwnPose(out _, out var orientation, out _))
        {
            EnsureExternalCamAligned(CameraMode.External, orientation);
            float Axis(string positive, string negative) => ActionDown(keys, positive) ? 1 : ActionDown(keys, negative) ? -1 : 0;
            // WinTrek.cpp:8601-8616: keyboard rotates the free external orientation.
            ExternalCameras.RotateRates(_externalCam, dt, Axis("PitchUp", "PitchDown"),
                Axis("YawLeft", "YawRight"), Axis("RollLeft", "RollRight"));
        }
        if (!composing && _state == ClientState.Flying && (ActionDown(keys, "ZoomIn") || ActionDown(keys, "ZoomOut"))
            && (_cameraMode == CameraMode.Cockpit || FlightCameraZoom.CanZoomExternal(_cameraMode)))
        {
            _flightZoom.Step(dt, ActionDown(keys, "ZoomIn"), ActionDown(keys, "ZoomOut"), _cameraMode == CameraMode.Cockpit);
            _cockpitCamera.FieldOfViewRadians = _flightZoom.FovRadians;
            _chaseCamera.DistanceOverride = _flightZoom.ExternalDistance;
        }
    }

    private void HandlePlayerPingsKey(KeyboardState keys, bool composing)
    {
        bool Edge(string action) => _playerPingsPressEdges.Observe(action, ActionDown(keys, action));
        // UpdateMainMenu observes its edges earlier in the same frame, even
        // while closed. Report navigation therefore needs its own edge state.
        bool previous = Edge("MenuUp"), next = Edge("MenuDown");
        if (Edge("PlayerPings") && !composing && _state == ClientState.Flying)
        {
            if (_playerPingsVisible) _playerPingsVisible = false;
            else _session.SendPlayerPingsRequest();
        }
        lock (_session.Gate)
        {
            long revision = _session.World.PlayerPingsRevision;
            if (revision != _playerPingsSeenRevision)
            {
                _playerPingsSeenRevision = revision;
                _playerPingsVisible = revision > 0;
                _playerPingsPage = 0;
            }
            if (_playerPingsVisible && !composing)
                _playerPingsPage = Math.Clamp(_playerPingsPage + (next ? 1 : previous ? -1 : 0), 0,
                    Math.Max(0, (_session.World.PlayerPingLines.Count - 1) / PlayerPingsPageSize));
        }
    }

    private IReadOnlyList<TeamPaneLayout.Line> PlayerPingPage()
    {
        var lines = new List<TeamPaneLayout.Line>();
        lock (_session.Gate)
        {
            var report = _session.World.PlayerPingLines;
            int pages = Math.Max(1, (report.Count + PlayerPingsPageSize - 1) / PlayerPingsPageSize);
            _playerPingsPage = Math.Clamp(_playerPingsPage, 0, pages - 1);
            lines.Add(new(TeamPaneLayout.LineKind.Title, $"CONNECTION INFO OF PLAYERS ({_playerPingsPage + 1}/{pages})"));
            lines.AddRange(report.Skip(_playerPingsPage * PlayerPingsPageSize).Take(PlayerPingsPageSize)
                .Select(line => new TeamPaneLayout.Line(TeamPaneLayout.LineKind.Pilot, line)));
        }
        lines.Add(new(TeamPaneLayout.LineKind.Hint,
            $"{KeyOf("MenuUp").Label}/{KeyOf("MenuDown").Label} PAGE | {KeyOf("PlayerPings").Label} CLOSE | F6 BIND CONTROLS"));
        return lines;
    }

    private void ToggleCommandGrid()
    {
        _commandGridVisible = !_commandGridVisible;
        CommandGridPreference.Save(CommandGridPreference.DefaultPath, _commandGridVisible);
        Console.WriteLine($"[p642] command grid={_commandGridVisible}");
    }
    private void ToggleMainMenu()
    {
        _mainMenuVisible = !_mainMenuVisible;
        _mainMenuCursor = 0;
        _menuConsumedFrame = true;
        IsMouseVisible = _mainMenuVisible || _dockedActive || !_mouseFlightEnabled;
        Console.WriteLine($"[p642] main menu={_mainMenuVisible}");
    }

    private string[] CurrentMainMenuItems => _skirmish?.Options.HostLan == true ? MainMenuItems.Concat(new[] { "End LAN game for everyone" }).ToArray()
        : _skirmish is null ? MainMenuItems
        : MainMenuItems.Concat(new[] { _skirmish.Paused ? "Resume skirmish" : "Pause skirmish", "End skirmish", "Join Alpha", "Join Bravo", "Return to spectator" }).ToArray();

    private void UpdateMainMenu(KeyboardState keys)
    {
        foreach (var action in new[] { "MenuUp", "MenuDown", "MenuConfirm" })
        {
            bool edge = ConsoleEdge(keys, action);
            if (!_mainMenuVisible || !edge) continue;
            if (action == "MenuUp") _mainMenuCursor = (_mainMenuCursor + CurrentMainMenuItems.Length - 1) % CurrentMainMenuItems.Length;
            else if (action == "MenuDown") _mainMenuCursor = (_mainMenuCursor + 1) % CurrentMainMenuItems.Length;
            else
            {
                _mainMenuVisible = false;
                switch (_mainMenuCursor)
                {
                    case 0: if (_skirmish?.Paused == true) ToggleSkirmishPause(); break;
                    case 1: _helpVisible = true; break;
                    case 2: if (!_keyBindVisible) ToggleKeyBindings(); break;
                    case 3: _creditsVisible = true; break;
                    case 4: if (_state == ClientState.Flying) ApplyConsoleMode("ConModeGameState"); break;
                    case 5: Exit(); break;
                    case 6: _graphicsSettingsVisible = true; _graphicsRow = 6; _graphicsPrevious = keys; break;
                    case 7: if (_skirmish?.Options.HostLan == true) StopSkirmish(); else ToggleSkirmishPause(); break;
                    case 8: StopSkirmish(); break;
                    case 9: RequestSkirmishSeat(0); break;
                    case 10: RequestSkirmishSeat(1); break;
                    case 11: RequestSkirmishSeat(null); break;
                }
            }
        }
    }

    private void DrawConsolePanes()
    {
        if (_state != ClientState.Flying) return;
        var lines = new List<TeamPaneLayout.Line>();
        if (_playerPingsVisible)
        {
            _teamPaneRenderer.Draw(GraphicsDevice.Viewport, PlayerPingPage());
            return;
        }
        if (_hangarOnly && _dockedActive && _cameraMode != CameraMode.Command)
        {
            lines.Add(new(TeamPaneLayout.LineKind.Title, "HANGAR"));
            lines.Add(new(TeamPaneLayout.LineKind.Hint, $"{KeyOf("ShopLaunch").Label} Launch | {KeyOf("ConModeInventory").Label} Loadout | {KeyOf("ViewCommand").Label} Command"));
        }
        if (_consolePanes.GameState)
        {
            lines.Add(new(TeamPaneLayout.LineKind.Title, "GAME STATE"));
            lines.Add(new(TeamPaneLayout.LineKind.Note, $"MISSION STAGE: {_session.World.Mission?.Stage}  |  MONEY: {_session.World.MyMoney}"));
            // HUD-10 — native WinTrek.cpp:8128-8143 feeds the game-state container
            // the countdown-game remaining time or the elapsed mission time. The
            // standalone CountdownOn/timebmp badge (dialog.mdl:5091-5106) is
            // never fed natively, so no separate clock badge is drawn.
            if (_session.World.Mission is { } mission && _session.World.MissionElapsedSeconds() is { } elapsed)
            {
                int clock = Rendering.MissionClock.DisplaySeconds(mission.GameLengthSeconds, elapsed);
                lines.Add(new(TeamPaneLayout.LineKind.Note,
                    $"{Rendering.MissionClock.Label(mission.GameLengthSeconds)}: {Rendering.MissionClock.Format(clock)}"));
            }
            foreach (var group in _session.World.ScoreRoster.GroupBy(e => e.SideId).OrderBy(g => g.Key))
                lines.Add(new(TeamPaneLayout.LineKind.SideHeader, $"SIDE {group.Key}: {(_session.World.IsSideActive(group.Key) ? "ACTIVE" : "ELIMINATED")}  PILOTS {group.Count()}  KILLS {group.Sum(e => e.Kills)}"));
        }
        // HUD-7 — the centre team-pane "INVENTORY" list is gone: F4 now shows
        // only the native bottom-right equipment pane (the native mask
        // ofInventory, dialog.mdl:5237). The old list duplicated the pane and
        // covered the gauges, so it is no longer drawn here.
        if (lines.Count > 0) _teamPaneRenderer.Draw(GraphicsDevice.Viewport, lines);
    }
    private void DrawMainMenu()
    {
        if (!_mainMenuVisible) return;
        var lines = new List<TeamPaneLayout.Line> { new(TeamPaneLayout.LineKind.Title, "MAIN MENU") };
        for (int i = 0; i < CurrentMainMenuItems.Length; i++) lines.Add(new(TeamPaneLayout.LineKind.Pilot,
            (i == _mainMenuCursor ? "> " : "  ") + CurrentMainMenuItems[i], i == _mainMenuCursor));
        lines.Add(new(TeamPaneLayout.LineKind.Hint, $"{KeyOf("MenuUp").Label}/{KeyOf("MenuDown").Label} select | {KeyOf("MenuConfirm").Label} open | {KeyOf("MainMenu").Label} resume"));
        _teamPaneRenderer.Draw(GraphicsDevice.Viewport, lines);
    }
}
