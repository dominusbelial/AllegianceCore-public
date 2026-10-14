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
    private readonly ActionPressEdges _consolePressEdges = new();
    private readonly FlightCameraZoom _flightZoom = new();
    private static readonly string[] ConsoleActions = ["ConModeCombat", "ConModeGameState", "ConModeNav",
        "ConModeInventory", "TeamPane", "ConModeMiniTeam", "ViewExternal", "ToggleGrid"];
    private static readonly string[] MainMenuItems = ["Resume", "Help / quick reference", "Controls", "Credits / licences", "Game state", "Quit"];

    private bool ConsoleEdge(KeyboardState keys, string action)
    {
        return _consolePressEdges.Observe(action, ActionDown(keys, action));
    }

    private void ApplyConsoleMode(string action)
    {
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
        IsMouseVisible = _mainMenuVisible || CommandCameraActive || _minimapExpanded || _dockedActive || !_mouseFlightEnabled;
        Console.WriteLine($"[p642] console {action}: {_consolePanes}, hangar={_hangarOnly}");
    }

    private void HandleConsoleKeys(KeyboardState keys, bool composing, float dt)
    {
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

    private void UpdateMainMenu(KeyboardState keys)
    {
        foreach (var action in new[] { "MenuUp", "MenuDown", "MenuConfirm" })
        {
            bool edge = ConsoleEdge(keys, action);
            if (!_mainMenuVisible || !edge) continue;
            if (action == "MenuUp") _mainMenuCursor = (_mainMenuCursor + MainMenuItems.Length - 1) % MainMenuItems.Length;
            else if (action == "MenuDown") _mainMenuCursor = (_mainMenuCursor + 1) % MainMenuItems.Length;
            else
            {
                _mainMenuVisible = false;
                switch (_mainMenuCursor)
                {
                    case 1: _helpVisible = true; break;
                    case 2: if (!_keyBindVisible) ToggleKeyBindings(); break;
                    case 3: _creditsVisible = true; break;
                    case 4: if (_state == ClientState.Flying) ApplyConsoleMode("ConModeGameState"); break;
                    case 5: Exit(); break;
                }
            }
        }
    }

    private void DrawConsolePanes()
    {
        if (_state != ClientState.Flying) return;
        var lines = new List<TeamPaneLayout.Line>();
        if (_hangarOnly && _dockedActive && _cameraMode != CameraMode.Command)
        {
            lines.Add(new(TeamPaneLayout.LineKind.Title, "HANGAR"));
            lines.Add(new(TeamPaneLayout.LineKind.Hint, $"{KeyOf("ShopLaunch").Label} Launch | {KeyOf("ConModeInventory").Label} Loadout | {KeyOf("ViewCommand").Label} Command"));
        }
        if (_consolePanes.GameState)
        {
            lines.Add(new(TeamPaneLayout.LineKind.Title, "GAME STATE"));
            lines.Add(new(TeamPaneLayout.LineKind.Note, $"MISSION STAGE: {_session.World.Mission?.Stage}  |  MONEY: {_session.World.MyMoney}"));
            foreach (var group in _session.World.ScoreRoster.GroupBy(e => e.SideId).OrderBy(g => g.Key))
                lines.Add(new(TeamPaneLayout.LineKind.SideHeader, $"SIDE {group.Key}: {(_session.World.IsSideActive(group.Key) ? "ACTIVE" : "ELIMINATED")}  PILOTS {group.Count()}  KILLS {group.Sum(e => e.Kills)}"));
        }
        if (_consolePanes.Inventory && !_dockedActive)
        {
            lines.Add(new(TeamPaneLayout.LineKind.Title, "INVENTORY"));
            foreach (var part in OwnInventory()) lines.Add(new(TeamPaneLayout.LineKind.Pilot,
                $"{ResolveFlightPart(part.PartId)?.Name ?? $"part {part.PartId}"}  MOUNT {unchecked((sbyte)part.MountId)}  x{part.Amount}", _inventorySelection == part));
        }
        if (lines.Count > 0) _teamPaneRenderer.Draw(GraphicsDevice.Viewport, lines);
    }
    private void DrawMainMenu()
    {
        if (!_mainMenuVisible) return;
        var lines = new List<TeamPaneLayout.Line> { new(TeamPaneLayout.LineKind.Title, "MAIN MENU") };
        for (int i = 0; i < MainMenuItems.Length; i++) lines.Add(new(TeamPaneLayout.LineKind.Pilot,
            (i == _mainMenuCursor ? "> " : "  ") + MainMenuItems[i], i == _mainMenuCursor));
        lines.Add(new(TeamPaneLayout.LineKind.Hint, $"{KeyOf("MenuUp").Label}/{KeyOf("MenuDown").Label} select | {KeyOf("MenuConfirm").Label} open | {KeyOf("MainMenu").Label} resume"));
        _teamPaneRenderer.Draw(GraphicsDevice.Viewport, lines);
    }
}
