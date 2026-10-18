using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>Configured before connect; resolve immutable, shareable static data.
    /// All access/mutation follows ConnectionSession.Gate, like Apply.</summary>
    public Func<string, StaticCore?> MissionCoreResolver { get; set; } = ClientMissionCoreCache.Resolve;
    public StaticCore? MissionCore { get; private set; }
    /// <summary>JOINED_MISSION's player identity, available before SET_CLUSTER.
    /// MyShipId retains its launch-assignment contract for pose consumers.</summary>
    public short? JoinedShipId { get; private set; }
    public uint? JoinedMissionCookie { get; private set; }
    public string? JoinError { get; private set; }

    // clintlib/appmsg.cpp:2150-2214: select static data, establish own player
    // before the player list/launch burst. dtime and cookie are not consumed there.
    private void Apply(JoinedMission message)
    {
        if (_mission is null) return;
        StaticCore? core;
        try { core = MissionCoreResolver(_mission.StaticCoreFile); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        { core = null; }
        if (core is null)
        {
            JoinError = $"Unable to load mission core '{_mission.StaticCoreFile}'.";
            return;
        }
        MissionCore = core;
        JoinError = null;
        JoinedMissionCookie = message.DwCookie;
        if (MyShipId is { } old && _ships.TryGetValue(old, out var oldShip)) oldShip.IsMine = false;
        JoinedShipId = message.ShipId;
        MyShipId = null; // the joined player exists; a launch pose has not been assigned yet
        MySideId = null;
        var own = Upsert(message.ShipId);
        own.IsMine = true;
        own.SideId = -1;
        own.CharacterName = "<error>";
        own.Player = new PlayerInfo { ShipId = message.ShipId, ISide = -1, CharacterName = "<error>" };
        _roster.Clear();
        MyAcceptedCommand = null;
        _myQueuedCommand = null;
        _moneyRequest = null;
        MarkSnapshotDirty();
    }

    // appmsg.cpp:3223-3318: discovery is text/feedback, not a visibility mutation.
    private void Apply(ObjectSpotted message)
    {
        if (MyShipId is not { } me || MySideId is not { } side || side < 0) return;
        string spotter;
        switch ((IgcObjectType)message.OtSpotter)
        {
            case IgcObjectType.Ship when _ships.TryGetValue(message.OidSpotter, out var ship):
                spotter = ship.ShipId == me ? "You've" : ship.CharacterName + (ship.SideId == side ? " has" : " (Ally) has");
                break;
            case IgcObjectType.Station when _stations.TryGetValue(message.OidSpotter, out var station):
                spotter = (station.SideId == side ? "Your " : "Allied ") + station.Name + " has";
                break;
            case IgcObjectType.Probe when _probes.TryGetValue(message.OidSpotter, out var probe):
                spotter = probe.SideId == side ? "One of your team's probes has" : "One of your ally's probes has";
                break;
            default: return; // tolerate late/deleted objects rather than native's null dereference
        }
        string text;
        bool critical = _ships.TryGetValue(me, out var own) && own.WingId == 0;
        switch ((IgcObjectType)message.OtObject)
        {
            case IgcObjectType.Station when _stations.TryGetValue(message.OidObject, out var station)
                && _clusters.TryGetValue(station.ClusterId, out var sector):
                bool allied = AreAllied(side, station.SideId);
                text = $"{spotter} discovered an {(allied ? "allied" : "enemy")} {station.Name} in sector {sector.Name}";
                critical &= !allied;
                break;
            case IgcObjectType.Asteroid when _asteroids.TryGetValue(message.OidObject, out var asteroid)
                && _clusters.TryGetValue(asteroid.ClusterId, out var sector):
                text = $"{spotter} discovered a {SpottedAsteroidType(asteroid.AabmCapabilities)} asteroid in sector {sector.Name}";
                break;
            case IgcObjectType.Warp when _warps.TryGetValue(message.OidObject, out var warp)
                && _warps.TryGetValue(warp.DestinationId, out var destination)
                && _clusters.TryGetValue(warp.ClusterId, out var from)
                && _clusters.TryGetValue(destination.ClusterId, out var to):
                text = $"{spotter} discovered an aleph from sector {from.Name} to sector {to.Name}";
                break;
            default: return;
        }
        if (_feedbackLines.Count >= MaxFeedbackLines) _feedbackLines.RemoveAt(0);
        _feedbackLines.Add(new FeedbackLine(text, critical, 0));
        PostSystemLine(text);
    }

    // Igc/asteroidigc.cpp:126-166: exact capability row match, generic fallback.
    private static string SpottedAsteroidType(short capabilities) => capabilities switch
    {
        DataAsteroidIGC.AabmMineHe3 => "Helium 3",
        0x10 => "Uranium", 0x20 => "Silicon", 0x40 => "Carbonaceous",
        _ => "asteroid",
    };

    // clintlib/appmsg.cpp:2129-2147 (SET_WINGID).
    private void Apply(SetWingId wing)
    {
        if (wing.WingId is < 0 or >= SetWingId.WidMax ||
            !_ships.TryGetValue(wing.ShipID, out var ship) || ship.SideId != MySideId) return;
        // Unlike native's optimistic self assignment, the port also accepts the
        // uncommanded self echo: SendWingId waits for server confirmation.
        ship.WingId = wing.WingId;
        if (wing.BCommanded && wing.ShipID == (MyShipId ?? JoinedShipId))
        {
            string text = $"You have been assigned to wing {WingNames.For(wing.WingId)}";
            if (_feedbackLines.Count >= MaxFeedbackLines) _feedbackLines.RemoveAt(0);
            _feedbackLines.Add(new FeedbackLine(text, true, 0));
            PostSystemLine(text);
        }
    }
}
