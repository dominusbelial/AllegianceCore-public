using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly State.PlayerActivity _playerActivity = new();
    private MouseState _activityMouse;
    private KeyboardState _activityKeys;

    private void UpdatePlayerActivity()
    {
        var mouse = Mouse.GetState();
        var keys = Keyboard.GetState();
        bool motion = _relativeMouse.TakeActivity();
        bool activity = IsActive && (mouse != _activityMouse || motion || keys.GetPressedKeyCount() > 0
            || keys != _activityKeys);
        _activityMouse = mouse; _activityKeys = keys;
        if (activity) _playerActivity.RecordInput();
        if (!_session.Connected || _session.Lost || _session.Reconnecting || _session.Ack is not { FValidated: true } ack) return;
        lock (_session.Gate)
        {
            var world = _session.World;
            short id = world.MyShipId ?? world.JoinedShipId ?? ack.ShipID;
            if (world.TakeOwnLeaderGameOver()) _playerActivity.RecordLeaderGameOver();
            if (_playerActivity.Update(Environment.TickCount64 / 1000.0, world.MySideId is null or < 0,
                _ownReady, activity: false) is { } ready)
            {
                _ownReady = ready;
                _session.SendPlayerReady(ready, id);
            }
        }
    }

    private void SetManualPlayerReady(bool ready, short shipId)
    {
        _playerActivity.ToggleManualAfk();
        _ownReady = ready;
        _session.SendPlayerReady(ready, shipId);
    }
}
