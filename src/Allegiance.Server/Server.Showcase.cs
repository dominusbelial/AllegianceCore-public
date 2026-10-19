using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly Dictionary<Mission, (double Deadline, short ArmedBy)> _researchFinales = new();

    // Explicit port orchestration. A request is scoped to its seated peer's current mission
    // and epoch; retries cannot postpone the deadline or arm the automatic next match.
    private void ArmResearchFinale(ConnectionId from, ShowcaseFinaleRequest request)
    {
        if (_config.Showcase.TechMode != "research" || !_peerShips.TryGetValue(from, out var live)
            || live.Ship.Mission is not { Stage: MissionStage.Started } mission
            || live.Ship.SideId != request.Winner || !mission.Sides.Any(s => s.Id == request.Winner && s.Active)
            || request.Epoch.ToString("N") != mission.ResearchEpoch || _researchFinales.ContainsKey(mission)) return;
        double deadline = _simNow + _config.Showcase.FinaleTimeoutSeconds;
        _researchFinales[mission] = (deadline, request.Winner);
        Console.WriteLine(FormattableString.Invariant($"[finale] research showcase epoch={mission.ResearchEpoch} armedBy={request.Winner}: bounded finale armed; deadline={deadline:0.###}; timeout={_config.Showcase.FinaleTimeoutSeconds}s; legal loadouts only, native station damage may be unavailable"));
    }

    private void AdvanceResearchFinales(double now)
    {
        foreach (var (mission, finale) in _researchFinales.ToArray())
        {
            if (!_missions.Contains(mission) || mission.Stage != MissionStage.Started)
            { _researchFinales.Remove(mission); continue; }
            if (now < finale.Deadline) continue;
            _researchFinales.Remove(mission);
            var result = mission.EvaluateCountdown(now, deadlineReached: true);
            string criterion = result.Winner is null ? "draw" : result.Criterion;
            Console.WriteLine($"[finale] deadline winner side={result.Winner?.Id ?? GameOver.NA} by={criterion} values={mission.CountdownValues(result.Criterion)} epoch={mission.ResearchEpoch} armedBy={finale.ArmedBy}");
            string reason = result.Winner is { } winner
                ? $"The clock has stopped. The winner: {winner.Name} by {result.Criterion}"
                : "The clock has stopped. The game is a draw.";
            DeclareGameOver(mission, result.Winner?.Id ?? GameOver.NA, reason);
        }
    }

    /// <summary>Showcase-only map content. Supply missing branch rocks from the selected
    /// plans; constructors must still be funded, fly and build normally. Capability
    /// matching is native (Igc/shipIGC.cpp:2814-2815); this resource layout is a port extension.</summary>
    private static MissionMap PrepareResearchMap(StaticCore core, MissionMap map,
        IReadOnlyDictionary<short, ResearchPlanCatalog.Selection> plans)
    {
        var stations = map.Stations.ToArray();
        // File maps can bake a different civilization's home type. Research plans
        // assume the selected civ's initial station (native generated homes:
        // Igc/mapmakerigc.cpp:926-932). Align showcase map content with that plan.
        foreach (var (side, selection) in plans.OrderBy(kv => kv.Key))
        {
            int home = Array.FindIndex(stations, st => st.SideId == side
                && core.StationTypes.TryGetValue(st.StationTypeId, out var type)
                && type.HasCapability(DataStationTypeIGC.SabmRestart));
            if (home < 0) continue;
            short initial = core.Civilizations[selection.Plan.CivId].InitialStationTypeId;
            if (stations[home].StationTypeId == initial) continue;
            Console.WriteLine($"[research-map] side={side} homeType={stations[home].StationTypeId}->{initial}: selected civilization starting station");
            stations[home] = stations[home] with { StationTypeId = initial };
        }
        var rocks = map.Asteroids.ToList();
        var usedIds = rocks.Select(r => r.AsteroidId).ToHashSet();
        int nextId = 0;
        foreach (var (side, selection) in plans.OrderBy(kv => kv.Key))
        {
            var homes = stations.Where(st => st.SideId == side).ToArray();
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
        return MissionMap.FromLists(map.Clusters, map.Warps, stations, rocks);
    }
}
