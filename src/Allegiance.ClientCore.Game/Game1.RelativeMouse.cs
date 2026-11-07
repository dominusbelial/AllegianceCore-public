using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly RelativeMouse _relativeMouse = new(SdlRelativeMouse.TryCreate, Console.WriteLine);
    private bool _mouseCaptureExiting;

    private void SyncMouseCapture(bool composing = false)
    {
        var owner = MouseCapturePolicy.Choose(IsActive, _state == ClientState.Flying,
            _dockedActive, _mouseCaptureExiting || _proofEnabled || composing || _chatActive || _qcMenuOpen
                || LiveComposeState is NativeComposeState.Command or NativeComposeState.MouseCommand
                || _mainMenuVisible || _keyBindVisible || _graphicsSettingsVisible
                || _helpVisible || _creditsVisible || _playerPingsVisible || _pilotProfile.Editing
                || IsBriefingVisible(out _)
                || _investActive || _teleportActive || _skirmish?.Paused == true,
            _mouseFlightEnabled, _minimapExpanded, CommandCameraActive, SpectatorCameraActive, _freeCamActive,
            _menuConsumedFrame);
        if (_relativeMouse.SetOwner(owner))
        {
            _specMouseInit = false;
            if (owner == MouseCaptureOwner.Flight) _mouseFlight.Enable();
        }
        IsMouseVisible = owner == MouseCaptureOwner.None;
    }

    protected override void OnDeactivated(object sender, EventArgs args)
    {
        _relativeMouse.SetOwner(MouseCaptureOwner.None);
        IsMouseVisible = true;
        base.OnDeactivated(sender, args);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        base.OnExiting(sender, args);
        _mouseCaptureExiting = !args.Cancel;
        _relativeMouse.SetOwner(MouseCaptureOwner.None);
        IsMouseVisible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _relativeMouse.Dispose();
        base.Dispose(disposing);
    }
}
