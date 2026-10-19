using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly HangarLaunchSequence _hangarLaunch = new();
    private long _hangarBoardFailure;
    private HangarAction _hangarHover;
    private bool _hangarMouseDown;
    private bool _hangarQuitConfirm;
    private readonly ActionPressEdges _hangarEdges = new();
    private bool HangarDevelopments => _session.World.ResearchBriefing?.Settings.AllowDevelopments ?? true;

    private void LaunchFromHangar(ShipEntity ship)
    {
        if (_launchSent || _hangarLaunch.AwaitingDisembark) return;
        if (ship.ParentId >= 0)
        {
            _hangarBoardFailure = _session.World.BoardFailureRevision;
            _hangarLaunch.Begin(ship.ParentId);
            _session.SendBoardShip(-1);
            return;
        }
        if (_shopModel is not { } shop || !shop.TryLaunch()) return;
        _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
        _launchSent = true;
        _throttle = Flight.FlightInput.DefaultThrottle;
    }

    private void ContinueHangarLaunch(ShipEntity ship)
    {
        // BOARD_NACK ends native teleport lockdown (clintlib/appmsg.cpp:1899-1911).
        if (_hangarLaunch.AwaitingDisembark && _session.World.BoardFailureRevision != _hangarBoardFailure)
            _hangarLaunch.Cancel();
        else if (_hangarLaunch.Acknowledged(ship.ParentId, ship.StationId >= 0))
            LaunchFromHangar(ship);
    }

    // This dispatcher is shared by the real pointer buttons and headless transition tests.
    private void ActivateHangar(HangarAction action)
    {
        if (_launchSent || _hangarLaunch.AwaitingDisembark || _hangarQuitConfirm) return;
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var ship) || ship.StationId < 0) return;
        switch (action)
        {
            case HangarAction.Launch: LaunchFromHangar(ship); break;
            case HangarAction.Command:
                if (!world.Stations.TryGetValue(ship.StationId, out var station)) break;
                ApplyConsoleMode("ViewCommand", updatePointer: false);
                _session.SendViewClusterRequest(station.ClusterId);
                _cameraMode = CameraMode.Command;
                _commandCentered = false;
                _directorActive = _freeCamActive = _orbitCamActive = false;
                break;
            case HangarAction.Loadout:
                _hangarOnly = false; _investActive = _teleportActive = _teamPaneVisible = false;
                _helpVisible = false; _cameraMode = CameraMode.Cockpit;
                break;
            case HangarAction.Invest:
                if (!HangarDevelopments) break;
                _investActive = !_investActive; _teleportActive = _teamPaneVisible = false;
                break;
            case HangarAction.Teleport:
                _teleportActive = !_teleportActive; _investActive = _teamPaneVisible = false;
                if (_teleportActive) _teleportModel = BuildTeleportModel(world, ship);
                break;
            case HangarAction.Team:
                ApplyConsoleMode("TeamPane", updatePointer: false); _hangarOnly = true; break;
            case HangarAction.Help: _helpVisible = true; break;
            case HangarAction.Exit: _hangarQuitConfirm = true; break;
        }
    }

    private void ConfirmHangarQuit(bool confirm)
    {
        if (!_hangarQuitConfirm) return;
        _hangarQuitConfirm = false;
        if (!confirm) return;
        _hangarLaunch.Cancel();
        if (_session.World.MyShipId is { } me) _session.SendQuitMission(me, QuitSideReason.Quit);
        // The normal authoritative OwnMissionDeparture path returns to the browser.
    }

    private bool HandleHangarQuitInput(KeyboardState keys, bool composing)
    {
        bool confirm = _hangarEdges.Observe("confirm", ActionDown(keys, "MenuConfirm"));
        bool cancel = _hangarEdges.Observe("cancel", ActionDown(keys, "MenuBack") || ActionDown(keys, "MainMenu"));
        if (!_hangarQuitConfirm) return false;
        if (!composing && (confirm || cancel)) ConfirmHangarQuit(confirm && !cancel);
        return true;
    }

    private bool UpdateHangarControls(KeyboardState keys, bool composing)
    {
        var mouse = Mouse.GetState();
        var vp = GraphicsDevice.Viewport;
        bool down = mouse.LeftButton == ButtonState.Pressed;
        bool click = down && !_hangarMouseDown;
        _hangarMouseDown = down;
        bool allowed = !composing && !_mainMenuVisible && !_helpVisible && !_keyBindVisible;
        _hangarHover = allowed ? HangarControls.Hit(vp.Width, vp.Height, mouse.X, mouse.Y, HangarDevelopments) : HangarAction.None;
        if (_hangarQuitConfirm) return true;
        if (allowed && click && _hangarHover != HangarAction.None)
        {
            ActivateHangar(_hangarHover);
            return true;
        }
        return _hangarQuitConfirm || _state != ClientState.Flying;
    }

    private void DrawHangarQuitConfirmation()
    {
        if (!_hangarQuitConfirm || !_dockedActive || _state != ClientState.Flying) return;
        var vp = GraphicsDevice.Viewport;
        var panel = new Rectangle(vp.Width / 2 - 280, vp.Height / 2 - 70, 560, 140);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        _spriteBatch.Draw(_whitePixel, panel, new Color(12, 30, 40));
        _spriteBatch.DrawString(_hudBig, "QUIT CURRENT MISSION?", new Vector2(panel.X + 20, panel.Y + 20), Color.LightCyan);
        _spriteBatch.DrawString(_hudSmall, $"{KeyOf("MenuConfirm").Label} confirm    {KeyOf("MainMenu").Label} cancel",
            new Vector2(panel.X + 20, panel.Y + 88), Color.White);
        _spriteBatch.End();
    }

    private void DrawHangarControls()
    {
        if (!_dockedActive || _state != ClientState.Flying || _cameraMode == CameraMode.Command) return;
        var vp = GraphicsDevice.Viewport;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        foreach (var action in HangarControls.Actions(HangarDevelopments))
        {
            var rect = HangarControls.Bounds(vp.Width, vp.Height, action);
            _spriteBatch.Draw(_whitePixel, rect, action == _hangarHover ? new Color(30, 100, 105) : new Color(12, 30, 40));
            string text = action == HangarAction.Exit ? "Quit mission" : action.ToString();
            float scale = MathF.Min(1f, (rect.Width - 8) / _hudSmall.MeasureString(text).X);
            _spriteBatch.DrawString(_hudSmall, text, new Vector2(rect.X + 4, rect.Y + 5), Color.White,
                0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
        string feedback = _hangarQuitConfirm ? $"Leave this mission? {KeyOf("MenuConfirm").Label} confirm / {KeyOf("MainMenu").Label} cancel" : HangarControls.Feedback(_hangarHover);
        _spriteBatch.Draw(_whitePixel, new Rectangle(16, vp.Height - 38, vp.Width - 32, 26), new Color(8, 18, 26));
        _spriteBatch.DrawString(_hudSmall, feedback, new Vector2(22, vp.Height - 34), Color.LightCyan);
        _spriteBatch.End();
    }
}
