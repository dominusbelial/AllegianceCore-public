using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P1087RankingTests
{
    // The pre-P1087 algorithm, including the separate sort and per-occurrence tail.
    private static IEnumerable<Ship> OldRank(List<Ship> candidates, int limit)
    {
        var heap = new PriorityQueue<(Ship Ship, int Ordinal), (float, int)>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var ship = candidates[i];
            if (heap.Count < limit) heap.Enqueue((ship, i), (ship.Deviation, -i));
            else heap.EnqueueDequeue((ship, i), (ship.Deviation, -i));
        }
        var selected = new List<(Ship Ship, int Ordinal)>();
        while (heap.TryDequeue(out var entry, out _)) selected.Add(entry);
        selected.Sort(static (a, b) =>
        {
            int d = b.Ship.Deviation.CompareTo(a.Ship.Deviation);
            return d != 0 ? d : a.Ordinal.CompareTo(b.Ordinal);
        });
        var served = new bool[candidates.Count];
        foreach (var entry in selected)
        {
            served[entry.Ordinal] = true;
            yield return entry.Ship;
        }
        for (int i = 0; i < candidates.Count; i++)
            if (!served[i]) yield return candidates[i];
    }

    private static Ship NewShip(int id, float deviation) =>
        new((short)id, default, Vector3.Zero, Vector3.Zero, Orientation.Identity) { Deviation = deviation };

    private static short[] Serve(IEnumerable<Ship> ranked, int limit)
    {
        var ids = new List<short>();
        foreach (var ship in ranked)
        {
            if (ids.Count < limit - 2) ship.Deviation = 0;
            else ship.Deviation += ids.Count < limit ? .70f : 1f;
            ids.Add(ship.Id);
        }
        return ids.ToArray();
    }

    [Fact]
    public void Consecutive_viewers_match_the_sort_oracle_with_ties_and_repeated_ships()
    {
        float[] values = [0f, -0f, 1f, 1f, -1f, float.NaN, BitConverter.Int32BitsToSingle(0x7fc00001),
            float.NegativeInfinity, float.PositiveInfinity];
        var ranking = new ShipUpdateRanking();
        for (int seed = 0; seed < 100; seed++)
        {
            var rng = new Random(seed);
            int count = rng.Next(9, 65);
            var initial = Enumerable.Range(0, count).Select(i => values[i % values.Length]).ToArray();
            rng.Shuffle(initial);
            foreach (int limit in new[] { 0, 1, 2, rng.Next(1, count), count + 1, count + 10 })
            {
                var oldShips = initial.Select((d, i) => NewShip(i, d)).ToArray();
                var newShips = initial.Select((d, i) => NewShip(i, d)).ToArray();
                for (int viewer = 0; viewer < 3; viewer++)
                {
                    // Draw a new candidate order for this viewer, including repeated references.
                    var order = Enumerable.Range(0, count).Append(rng.Next(count)).ToArray();
                    rng.Shuffle(order);
                    var candidates = order.Select(i => oldShips[i]).ToList();
                    ranking.Candidates.Clear();
                    ranking.Candidates.AddRange(order.Select(i => newShips[i]));
                    Assert.Equal(Serve(OldRank(candidates, limit), limit), Serve(ranking.Rank(limit), limit));
                    Assert.Equal(oldShips.Select(s => BitConverter.SingleToInt32Bits(s.Deviation)),
                        newShips.Select(s => BitConverter.SingleToInt32Bits(s.Deviation)));
                }
            }
        }
    }
}
