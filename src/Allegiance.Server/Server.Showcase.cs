using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>Showcase-only map content. Supply missing branch rocks from the selected
    /// plans; constructors must still be funded, fly and build normally. Capability
    /// matching is native (Igc/shipIGC.cpp:2814-2815); this resource layout is a port extension.</summary>
    private static MissionMap PrepareResearchMap(MissionMap map,
        IReadOnlyDictionary<short, ResearchPlanCatalog.Selection> plans)
    {
        var rocks = map.Asteroids.ToList();
        var usedIds = rocks.Select(r => r.AsteroidId).ToHashSet();
        int nextId = 0;
        foreach (var (side, selection) in plans.OrderBy(kv => kv.Key))
        {
            var homes = map.Stations.Where(st => st.SideId == side).ToArray();
            if (homes.Length == 0)
                throw new InvalidDataException($"showcase.techMode=research: side {side} has no home station for research map resources");
            var home = homes[0];
            int slot = 0;
            foreach (var requirement in selection.Plan.Steps.Where(s => s.Action == "station")
                .GroupBy(s => s.AsteroidClass!.Value).OrderBy(g => g.Key))
            {
                short mask = requirement.Key;
                // One resource per planned construction plus a replacement reserve.
                int needed = requirement.Count() + 1;
                int present = rocks.Count(r => r.ClusterId == home.ClusterId && (r.AabmCapabilities & mask) != 0);
                int added = 0;
                for (int i = present; i < needed; i++)
                {
                    while (nextId <= short.MaxValue && usedIds.Contains((short)nextId)) nextId++;
                    if (nextId > short.MaxValue)
                        throw new InvalidDataException("showcase.techMode=research: no free asteroid IDs for research map resources");
                    short id = (short)nextId++; usedIds.Add(id);
                    double angle = slot * 2.3999632297;
                    float radius = 3500 + slot / 12 * 700;
                    float z = home.Position[2] + (slot % 3 - 1) * 800;
                    slot++;
                    rocks.Add(new(1, [home.Position[0] + radius * (float)Math.Cos(angle),
                        home.Position[1] + radius * (float)Math.Sin(angle), z],
                        [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 0, 0, mask, id,
                        1000, 60, "rock_build", "", "", home.ClusterId, "Research map resource", 255));
                    added++;
                }
                Console.WriteLine($"[research-map] side={side} capability={mask} available={present + added} planned={requirement.Count()} added={added}");
            }
        }
        return MissionMap.FromLists(map.Clusters, map.Warps, map.Stations, rocks);
    }
}
