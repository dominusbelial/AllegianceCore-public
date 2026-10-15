using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    public readonly record struct PositionRequest(short SideId, short ShipId);
    public readonly record struct PositionRequestResult(short SideId, short ShipId, DelPositionReqReason Reason);

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
        if (message.ShipId == (MyShipId ?? JoinedShipId)) MyPositionRequestResult = result;
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
