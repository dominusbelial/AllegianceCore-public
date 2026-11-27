using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Only the sim thread observes or builds these groups. Completing the
    // snapshot drops all mission/sector references, including on rotation.
    private CollisionRoster? _collisionRoster;

    private sealed class CollisionRoster
    {
        private readonly record struct Seat(ConnectionId Conn, Mission? Mission, short? Side, Sector Sector);
        private readonly Seat[] _seats;
        private readonly Dictionary<(Mission? Mission, short Side), Group> _groups = new();
        internal HashSet<ConnectionId>? SpareAudience;

        private sealed class Group
        {
            internal readonly List<ConnectionId> Connections = new();
            internal readonly HashSet<Sector> Sectors = new();
        }

        internal CollisionRoster(Dictionary<ConnectionId, LiveShip> peers)
        {
            _seats = new Seat[peers.Count];
            int index = 0;
            foreach (var (conn, live) in peers)
            {
                var seat = new Seat(conn, live.Ship.Mission, live.Ship.SideId, live.Sector);
                _seats[index++] = seat;
                if (seat.Side is not { } side) continue;
                var key = (seat.Mission, side);
                if (!_groups.TryGetValue(key, out var group)) _groups.Add(key, group = new());
                group.Connections.Add(conn);
                group.Sectors.Add(seat.Sector);
            }
        }

        // Seat changes (including a side change on the same Ship) can happen
        // *inside* Mission.Update. Dictionary count/version alone is insufficient.
        internal bool Matches(Dictionary<ConnectionId, LiveShip> peers)
        {
            if (peers.Count != _seats.Length) return false;
            int index = 0;
            foreach (var (conn, live) in peers)
            {
                ref readonly var seat = ref _seats[index++];
                if (seat.Conn != conn || seat.Side != live.Ship.SideId ||
                    seat.Mission != live.Ship.Mission || seat.Sector != live.Sector) return false;
            }
            return true;
        }

        internal void AddCrossSide(Ship party, short ownSide, Sector sector, HashSet<ConnectionId> audience)
        {
            // No removals. Groups and their members retain first-encounter
            // order from the original per-party dictionary and lists.
            foreach (var ((mission, side), group) in _groups)
            {
                if (mission != party.Mission || side == ownSide || !group.Sectors.Contains(sector)) continue;
                if (party.IsCloaked && !party.SeenBySide(side)) continue;
                foreach (var conn in group.Connections) audience.Add(conn);
            }
        }
    }
}
