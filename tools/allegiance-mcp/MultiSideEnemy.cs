using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Showcase commander policy, not a native AI rule. Native wingmen
/// choose nearby visible hostile models (Igc/shipIGC.cpp:2030-2055); they do not
/// rank factions by hops/strength. This policy uses only a side's observations.</summary>
internal static class MultiSideEnemy
{
    internal sealed record Candidate(short Side, bool Active, bool Allied, short[] Sectors, long Kills, int Stations);

    internal readonly record struct RankedCandidate(short Side, bool Active, bool Allied,
        long Kills, int Stations, int Hops);

    /// <summary>Pure faction-ranking core; topology lookup is the only live input adapter.</summary>
    internal static short? ChooseRanked(short own, IEnumerable<RankedCandidate> candidates) =>
        candidates.Where(c => c.Side >= 0 && c.Side != own && c.Active && !c.Allied && c.Hops >= 0)
            .OrderBy(c => c.Hops).ThenByDescending(c => c.Kills).ThenByDescending(c => c.Stations)
            .ThenBy(c => c.Side).Select(c => (short?)c.Side).FirstOrDefault();

    internal static short? Choose(short own, short origin, ClusterGraph? graph, IEnumerable<Candidate> candidates)
    {
        var ranked = candidates.Where(c => c.Side >= 0 && c.Side != own && c.Active && !c.Allied).Select(c =>
        {
            int hops = c.Sectors.Select(s => graph is null ? (s == origin ? 0 : -1)
                : ExplorationPlanner.Hops(graph, origin, s)).Where(h => h >= 0).DefaultIfEmpty(-1).Min();
            return new RankedCandidate(c.Side, c.Active, c.Allied, c.Kills, c.Stations, hops);
        });
        short? selected = ChooseRanked(own, ranked);
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("MultiSideEnemy", "Choose", new { own, origin, candidates = ranked.ToArray() }, new { selected });
        return selected;
    }

    internal static short? Choose(ClientWorld world, short own, IEnumerable<DataStationIGC>? observed = null)
    {
        var stations = (observed ?? world.Stations.Values).ToArray();
        var core = Catalog.RepresentativeCore();
        var home = world.Stations.Values.Where(s => s.SideId == own)
            .OrderByDescending(s => core.StationTypes.TryGetValue(s.StationTypeId, out var t) && t.HasCapability(DataStationTypeIGC.SabmRestart))
            .ThenBy(s => s.StationId).Select(s => (short?)s.ClusterId).FirstOrDefault();
        if (home is null) return null;
        return Choose(own, home.Value, world.Graph, stations.GroupBy(s => s.SideId).Select(g =>
            new Candidate(g.Key, world.IsSideActive(g.Key), world.AreAllied(own, g.Key), g.Select(s => s.ClusterId).Distinct().ToArray(),
                world.ScoreRoster.Where(p => p.SideId == g.Key).Sum(p => (long)p.Kills), g.Count())));
    }
}

internal sealed partial class BotManager
{
    private bool HasMultipleEnemies => Probe?.World.SideRoster?.Sides.Length > 2;

    internal short? ScenarioEnemyHome(short own, short sideA, short sideB)
    {
        if (!HasMultipleEnemies) return FindSideHomeSector(own == sideA ? sideB : sideA);
        var peer = SideObservationPeer(own);
        if (peer is null) return null;
        lock (peer.Gate)
        {
            short? enemy = MultiSideEnemy.Choose(peer.World, own);
            return enemy is { } side ? peer.World.Stations.Values.Where(s => s.SideId == side)
                .OrderBy(s => s.StationId).Select(s => (short?)s.ClusterId).FirstOrDefault() : null;
        }
    }

    private Allegiance.ClientCore.Session.ConnectionSession? SideObservationPeer(short side)
    {
        lock (_gate)
            return _bots.FirstOrDefault(b => b.Side == side && b.Cs is not null && IsParticipating(b))?.Cs
                ?? (ProbeSide == side ? Probe : null);
    }

    internal short? SelectScenarioEnemy(short side)
    {
        var peer = SideObservationPeer(side);
        if (peer is null) return null;
        lock (peer.Gate) return MultiSideEnemy.Choose(peer.World, side);
    }

    private bool MultiSideActive(short side)
    {
        lock (Probe.Gate) return Probe.World.IsSideActive(side);
    }
}
