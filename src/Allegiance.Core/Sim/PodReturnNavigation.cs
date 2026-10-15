using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Pod routing from actual paired alephs. Native seeds FindPath with
/// mouths ordered by distance to the ship, then expands the frontier
/// (Igc/common.cpp:917-1008). With a destination position, minimize actual flight
/// distance including intermediate crossings and the exit-to-station leg. This
/// is a port routing improvement; native only sorts the initial mouths.
/// No oxygen or transit rule changes.</summary>
public static class PodReturnNavigation
{
    public static DataWarpIGC? NextWarp(IReadOnlyDictionary<short, DataWarpIGC> warps,
        short current, short home, Vector3 position, Vector3? homePosition = null)
    {
        if (current == home) return null;
        var incoming = new Dictionary<short, List<short>>();
        foreach (var w in warps.Values)
        {
            if (!warps.TryGetValue(w.DestinationId, out var exit)) continue;
            if (!incoming.TryGetValue(exit.ClusterId, out var list)) incoming[exit.ClusterId] = list = [];
            list.Add(w.ClusterId);
        }
        var hops = new Dictionary<short, int> { [home] = 0 };
        var queue = new Queue<short>(); queue.Enqueue(home);
        while (queue.TryDequeue(out var sector))
            if (incoming.TryGetValue(sector, out var sources))
                foreach (var source in sources)
                    if (hops.TryAdd(source, hops[sector] + 1)) queue.Enqueue(source);
        if (homePosition is { } destination)
        {
            // Dijkstra over arrival mouths, not sector centers. Transit itself
            // has no travel distance (FedSrv/fsship.cpp:126-134 relocates the
            // ship). Cooldown is enforced by the existing server gate, not here.
            var costs = new Dictionary<short, float>();
            var first = new Dictionary<short, DataWarpIGC>();
            var frontier = new PriorityQueue<short, float>();
            foreach (var mouth in warps.Values)
                if (mouth.ClusterId == current && warps.TryGetValue(mouth.DestinationId, out var exit)
                    && hops.ContainsKey(exit.ClusterId))
                {
                    var p = new Vector3(mouth.Position[0], mouth.Position[1], mouth.Position[2]);
                    float cost = (p - position).Length();
                    if (!costs.TryGetValue(exit.WarpId, out float prior) || cost < prior)
                    { costs[exit.WarpId] = cost; first[exit.WarpId] = mouth; frontier.Enqueue(exit.WarpId, cost); }
                }
            DataWarpIGC? answer = null;
            float bestCost = float.PositiveInfinity;
            while (frontier.TryDequeue(out short id, out float cost))
            {
                if (cost != costs[id] || cost >= bestCost) continue;
                var exit = warps[id];
                var p = new Vector3(exit.Position[0], exit.Position[1], exit.Position[2]);
                if (exit.ClusterId == home)
                {
                    float total = cost + (p - destination).Length();
                    if (total < bestCost) { bestCost = total; answer = first[id]; }
                    continue;
                }
                foreach (var mouth in warps.Values)
                    if (mouth.ClusterId == exit.ClusterId && mouth.WarpId != id
                        && warps.TryGetValue(mouth.DestinationId, out var next) && hops.ContainsKey(next.ClusterId))
                    {
                        var q = new Vector3(mouth.Position[0], mouth.Position[1], mouth.Position[2]);
                        float total = cost + (q - p).Length();
                        if (!costs.TryGetValue(next.WarpId, out float prior) || total < prior)
                        {
                            costs[next.WarpId] = total; first[next.WarpId] = first[id];
                            frontier.Enqueue(next.WarpId, total);
                        }
                    }
            }
            return answer;
        }
        DataWarpIGC? best = null;
        int bestHops = int.MaxValue;
        float bestDistance = float.MaxValue;
        foreach (var w in warps.Values)
        {
            if (w.ClusterId != current || !warps.TryGetValue(w.DestinationId, out var exit)
                || !hops.TryGetValue(exit.ClusterId, out int remaining)) continue;
            var p = new Vector3(w.Position[0], w.Position[1], w.Position[2]);
            float distance = (p - position).Length();
            if (remaining < bestHops || (remaining == bestHops && (distance < bestDistance
                || (distance == bestDistance && w.WarpId < best!.Value.WarpId))))
            { best = w; bestHops = remaining; bestDistance = distance; }
        }
        return best;
    }
}
