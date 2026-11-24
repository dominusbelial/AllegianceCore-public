namespace Allegiance.ClientCore.State;

/// <summary>Native AFK policy, shared by desktop and browser. Times are monotonic
/// wall seconds, independent of simulation speed. WinTrek/WinTrek.cpp:8182-8214.
/// Keep this instance across side changes and match resets: clintlib/clintlib.cpp:
/// 3903,4050,4110 and FedSrv/fsmission.cpp:649,1144,3468-3481 preserve AFK.</summary>
public sealed class PlayerActivity
{
    private double? _lastActivity;
    private bool _leaderGameOverPending;
    private bool _inputPending;
    public bool ManualAfk { get; private set; }

    /// <summary>Key activity can precede logon completion. Native records it in
    /// g_bActivity (WinTrek.cpp:807) and consumes it only while logged on (:8183-8188).
    /// A focus/visibility event must never call this.</summary>
    public void RecordInput() => _inputPending = true;

    /// <summary>Record only the outgoing leader's game-over event. Native:
    /// WinTrek/gameoverscreen.cpp:410-420. Preserve it through a full rotation
    /// burst until the host's next frame/timer consumes it.</summary>
    public void RecordLeaderGameOver() => _leaderGameOverPending = true;

    /// <summary>The explicit ready/AFK toggle owns the automatic suppression
    /// latch (native g_bAFKToggled, WinTrek/WinTrek.cpp:8189,8203).</summary>
    public void ToggleManualAfk() => ManualAfk = !ManualAfk;

    /// <summary>Explicit Join and commander Team Ready release manual AFK:
    /// WinTrek/teamscreen.cpp:2783-2790,2915-2916.</summary>
    public void ClearManualAfk() => ManualAfk = false;

    /// <returns>A ready transition to send, or null when unchanged. Call only
    /// while logged on; mouse movement, keys and buttons supply activity.</returns>
    public bool? Update(double now, bool teamLobby, bool ready, bool activity)
    {
        // Native starts with g_bActivity=true (WinTrek.cpp:44).
        activity |= _inputPending || _lastActivity is null;
        _inputPending = false;
        if (activity) _lastActivity = now;
        bool leaderGameOver = _leaderGameOverPending;
        _leaderGameOverPending = false;
        // GameOverScreen's constructor sets a ready leader AFK once, regardless
        // of manual suppression (WinTrek/gameoverscreen.cpp:410-420).
        if (leaderGameOver && ready) return false;
        if (ManualAfk) return null;
        if (activity) return ready ? null : true;
        // Native uses strictly greater than 90000 / 180000 milliseconds.
        return ready && now - _lastActivity!.Value > (teamLobby ? 90 : 180) ? false : null;
    }
}
