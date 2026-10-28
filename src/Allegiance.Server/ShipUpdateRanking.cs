using Allegiance.Core.Sim;

namespace Allegiance.Server;

// Native FedSrv/FedSrv.CPP:3923,3937-3950: only the served prefix needs
// deviation order. Skipped ships each get the same independent +1.00 update.
// Insertion ordinal breaks ties exactly like the existing stable LINQ sort.
internal sealed class ShipUpdateRanking
{
    private readonly PriorityQueue<(Ship Ship, int Ordinal), (float Deviation, int Tie)> _heap = new();
    private readonly List<(Ship Ship, int Ordinal)> _selected = new();
    private bool[] _served = [];
    internal readonly List<Ship> Candidates = new();
    internal IEnumerable<Ship> Rank(int limit)
    {
        _heap.Clear(); _selected.Clear();
        if (_served.Length < Candidates.Count) Array.Resize(ref _served, Candidates.Count);
        Array.Clear(_served);
        for (int i = 0; i < Candidates.Count; i++)
        {
            var ship = Candidates[i];
            if (_heap.Count < limit) _heap.Enqueue((ship, i), (ship.Deviation, -i));
            else _heap.EnqueueDequeue((ship, i), (ship.Deviation, -i));
        }
        while (_heap.TryDequeue(out var entry, out _)) _selected.Add(entry);
        _selected.Sort(static (a, b) =>
        {
            int d = b.Ship.Deviation.CompareTo(a.Ship.Deviation);
            return d != 0 ? d : a.Ordinal.CompareTo(b.Ordinal);
        });
        foreach (var entry in _selected) { _served[entry.Ordinal] = true; yield return entry.Ship; }
        for (int i = 0; i < Candidates.Count; i++) if (!_served[i]) yield return Candidates[i];
    }
}
