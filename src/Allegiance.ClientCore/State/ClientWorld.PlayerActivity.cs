namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private bool _ownLeaderGameOverPending;

    // Capture at GAME_OVER receipt, before the port's MISSION_STAGE Over reset
    // retires the old ship/leader maps. A frame or a background page timer can
    // run after the complete next-match burst. Native GameOverScreen uses the
    // outgoing player's role (WinTrek/gameoverscreen.cpp:410-420).
    private void RecordOwnLeaderGameOver()
    {
        short? own = MyShipId ?? JoinedShipId;
        _ownLeaderGameOverPending |= own is { } id && MySideId is { } side
            && _leaderShipIdBySideId.TryGetValue(side, out short leader) && leader == id;
    }

    /// <summary>Owned client activity consumes this once, under the session gate.
    /// Ordinary match/scoreboard resets leave it pending; full connection Reset
    /// retires it along with the old player.</summary>
    public bool TakeOwnLeaderGameOver()
    {
        bool pending = _ownLeaderGameOverPending;
        _ownLeaderGameOverPending = false;
        return pending;
    }
}
