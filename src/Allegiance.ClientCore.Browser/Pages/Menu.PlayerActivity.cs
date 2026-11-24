using Allegiance.ClientCore.State;
using Microsoft.JSInterop;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Menu
{
    private PlayerActivity _playerActivity = new();
    private bool _ownReady;

    // Runs from a page timer as well as input: a hidden tab's paused RAF must
    // not pause the native wall-clock AFK deadline. No visibility/focus event
    // synthesizes activity (WinTrek/WinTrek.cpp:8182-8214).
    [JSInvokable]
    public void PlayerActivityTick(bool activity)
    {
        if (activity) _playerActivity.RecordInput();
        if (_session is not { Connected: true, Lost: false, Reconnecting: false } session
            || session.Ack is not { FValidated: true } ack) return;
        var world = session.World;
        short id = world.MyShipId ?? world.JoinedShipId ?? ack.ShipID;
        if (world.TakeOwnLeaderGameOver()) _playerActivity.RecordLeaderGameOver();
        if (_playerActivity.Update(Environment.TickCount64 / 1000.0, world.MySideId is null or < 0,
            _ownReady, activity: false) is { } ready)
        {
            _ownReady = ready;
            session.SendPlayerReady(ready, id);
            StateHasChanged();
        }
    }

    private void TogglePlayerAfk()
    {
        if (_session is not { Connected: true, Lost: false, Reconnecting: false } session
            || session.Ack is not { FValidated: true } ack) return;
        _playerActivity.ToggleManualAfk();
        _ownReady = !_playerActivity.ManualAfk;
        session.SendPlayerReady(_ownReady, session.World.MyShipId ?? session.World.JoinedShipId ?? ack.ShipID);
    }
}
