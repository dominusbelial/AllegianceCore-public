using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>P864d — native <c>SIDE_TEAMLOBBY</c> (<c>Igc/igc.h:514</c>): the
    /// mission-side id every player parked "not on a team" sits on. Native keeps
    /// it as a real <c>IsideIGC</c> whose <c>DataSideIGC.name</c> is "Team Lobby"
    /// (<c>Igc/missionigc.cpp:3612-3634</c>); the client displays it under
    /// <c>MissionInfo::SideName</c>'s override "Not on a team"
    /// (<c>clintlib/clintlib.h:571</c>). This port models the park as the ship's
    /// <c>ShipEntity.SideId</c> value (P864b server side, <c>MoveToTeamLobby</c>),
    /// so the client already learns WHO is in the lobby from the normal
    /// player-info traffic (<c>PLAYERINFO(10)</c>/<c>EXPORT(26)</c> carry the
    /// side; <c>QUIT_SIDE(126)</c> and <c>JOIN_SIDE(125)</c> move it).</summary>
    public const short SideTeamLobby = -2;

    public readonly record struct PositionRequest(short SideId, short ShipId);
    public readonly record struct PositionRequestResult(short SideId, short ShipId, DelPositionReqReason Reason);

    /// <summary>P864d — one player currently in the mission's team lobby
    /// ("Not on a team": <see cref="ShipEntity.SideId"/> == <see cref="SideTeamLobby"/>).
    /// <see cref="IsSelf"/> marks the local player; <see cref="RequestingSideId"/> is
    /// the side this player has asked to join and is still waiting for (native's
    /// player-list status "Requesting", <c>WinTrek/teamscreen.cpp:495-497</c>).</summary>
    public readonly record struct TeamLobbyMember(short ShipId, string Name, bool IsSelf, short? RequestingSideId);

    private readonly List<TeamLobbyMember> _teamLobbyBuffer = new();

    /// <summary>P864d — native <c>MissionInfo::SideNumPlayers</c>: the RUNNING
    /// per-side human count the client keeps as players join and leave
    /// (<c>clintlib/clintlib.cpp:891-894</c> increments the count in
    /// <c>AddPlayer</c> only for a human on a real side, <c>:926-929</c>
    /// decrements it in <c>RemovePlayer</c>; <c>clintlib/clintlib.h:578</c> reads
    /// it), never a snapshot value. The team screen prints it per team row
    /// (<c>WinTrek/teamscreen.cpp:390-395</c>). Returns <see langword="null"/> when
    /// this client holds no player table at all yet (before the join-sync
    /// PLAYERINFO burst), so the caller can fall back to the SIDE_ROSTER scalar
    /// (P864b) instead of drawing a bogus 0.
    /// <para>Native answers 0 for <c>SIDE_TEAMLOBBY</c> here — that row's count is
    /// the lobby member-list size instead (<c>clintlib.h:578</c>,
    /// <c>teamscreen.cpp:378-386</c>), which this port reads from
    /// <see cref="TeamLobbyMembers"/>.</para></summary>
    public int? SideHumanCount(short sideId)
    {
        if (sideId < 0) return 0;
        if (_ships.Count == 0) return null;
        int n = 0;
        foreach (var ship in _ships.Values)
            if (ship.Player is { IsHuman: true } player && player.ISide == sideId) n++;
        return n;
    }

    /// <summary>P864d — the team lobby's current members, in display order: every
    /// ship the client knows on <see cref="SideTeamLobby"/>, by character name
    /// (ordinal, case-insensitive: native's <c>PlayerNameCompare</c>, the
    /// <c>_stricmp</c> name sort, <c>WinTrek/teamscreen.cpp:2067-2073</c>), each
    /// flagged when it is the local player and carrying its
    /// pending join request when there is one. Native derives the same list from
    /// the lobby side's member list (<c>MissionInfo::GetSideInfo(SIDE_TEAMLOBBY)
    /// -&gt;GetMemberList()</c>, <c>teamscreen.cpp:380</c>), which it fills from the
    /// mission's player set (<c>clintlib/clintlib.cpp:4010-4020</c>
    /// <c>AddPlayerToMission</c>, <c>:4080-4090</c>). Read under the session gate,
    /// like the rest of the roster tables. Drone (non-human) entries are skipped,
    /// matching the team screen's human filter (<c>teamscreen.cpp:1108-1115</c>
    /// <c>IsHuman</c>, applied at <c>:2569</c>).</summary>
    public IReadOnlyList<TeamLobbyMember> TeamLobbyMembers()
    {
        _teamLobbyBuffer.Clear();
        short me = MyShipId ?? JoinedShipId ?? -1;
        foreach (var (shipId, ship) in _ships)
        {
            if (ship.SideId != SideTeamLobby) continue;
            if (ship.Player is { IsHuman: false }) continue;
            string name = !string.IsNullOrWhiteSpace(ship.CharacterName) ? ship.CharacterName
                : ship.Player?.CharacterName ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = $"Pilot {shipId}";
            short? requesting = null;
            foreach (var request in _positionRequests)
                if (request.ShipId == shipId) { requesting = request.SideId; break; }
            _teamLobbyBuffer.Add(new TeamLobbyMember(shipId, name, shipId == me, requesting));
        }
        _teamLobbyBuffer.Sort(static (a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.ShipId.CompareTo(b.ShipId);
        });
        return _teamLobbyBuffer;
    }

    private readonly HashSet<PositionRequest> _positionRequests = new();
    private readonly Dictionary<short, bool> _autoAcceptBySideId = new();
    public IReadOnlySet<PositionRequest> PendingPositionRequests => _positionRequests;
    public IReadOnlyDictionary<short, bool> AutoAcceptBySideId => _autoAcceptBySideId;
    public PositionRequestResult? MyPositionRequestResult { get; private set; }
    public bool LobbyLocked { get; private set; }
    public bool SidesLocked { get; private set; }

    // clintlib/appmsg.cpp:2470-2483; clintlib.cpp:943-954. Membership and
    // duplicate arrivals cannot add a second request, but each arrival has feedback.
    private void Apply(PositionReqBroadcast message)
    {
        if (message.SideId < 0 || message.ShipId < 0) return;
        if (!_ships.TryGetValue(message.ShipId, out var player) || player.SideId != message.SideId)
            _positionRequests.Add(new(message.SideId, message.ShipId));
        if (message.ShipId == (MyShipId ?? JoinedShipId)) MyPositionRequestResult = null;
        if (IsTeamLeader && MySideId == message.SideId)
        {
            string name = !string.IsNullOrWhiteSpace(player?.CharacterName) ? player.CharacterName : $"Pilot {message.ShipId}";
            string text = $"{name} wishes to join your team";
            if (_feedbackLines.Count >= MaxFeedbackLines) _feedbackLines.RemoveAt(0);
            _feedbackLines.Add(new FeedbackLine(text, true, 0));
            PostSystemLine(text);
        }
        if (_soundEvents.Count < MaxSoundEvents)
            _soundEvents.Add(new(ClientSoundEventKind.PlayerWaiting, message.ShipId));
    }

    // appmsg.cpp:2486-2499: notify even when the request/player is absent.
    private void Apply(DelPositionReq message)
    {
        _positionRequests.Remove(new(message.SideId, message.ShipId));
        var result = new PositionRequestResult(message.SideId, message.ShipId, message.Reason);
        if (message.ShipId == (MyShipId ?? JoinedShipId) || (message.SideId == -1
            && message.Reason is DelPositionReqReason.NoMission or DelPositionReqReason.ServerPaused or DelPositionReqReason.BadPassword))
            MyPositionRequestResult = result;
        _events.Record(result);
    }

    // appmsg.cpp:2602-2627: authoritative settings; UI reads these each frame.
    private void Apply(AutoAccept message) => _autoAcceptBySideId[message.SideId] = message.FAutoAccept;
    private void Apply(LockLobby message) => LobbyLocked = message.FLock;
    private void Apply(LockSides message) => SidesLocked = message.FLock;

    private void ResetLobby()
    {
        _positionRequests.Clear();
        _autoAcceptBySideId.Clear();
        MyPositionRequestResult = null;
        LobbyLocked = SidesLocked = false;
        _pendingBallots.Clear();
    }
}
