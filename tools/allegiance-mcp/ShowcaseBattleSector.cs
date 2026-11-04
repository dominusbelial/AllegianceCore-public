using Allegiance.ClientCore.State;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Showcase rendezvous policy over the public topology. Rank neutral
/// sectors by worst home hop count, home-distance spread, total hops, then id.
/// Shared-home maps without flagged homes fall back to the smallest sector id.
/// Unreachable candidates are excluded; no names or authored ids are baked in.</summary>
internal static class ShowcaseBattleSector
{
    internal static short? Choose(ClusterGraph graph)
    {
        var homes = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).Distinct().ToArray();
        if (homes.Length == 0) return graph.Clusters.OrderBy(c => c.Id).Select(c => (short?)c.Id).FirstOrDefault();
        var candidates = graph.Clusters.Where(c => !c.HomeSector).ToArray();
        if (candidates.Length == 0) candidates = graph.Clusters.ToArray();
        return candidates.Select(c => (c.Id, Distances: homes.Select(h => graph.FindShortestPath(h, c.Id).Count - 1).ToArray()))
            .Where(c => c.Distances.All(d => d >= 0))
            .OrderBy(c => c.Distances.Max()).ThenBy(c => c.Distances.Max() - c.Distances.Min())
            .ThenBy(c => c.Distances.Sum()).ThenBy(c => c.Id)
            .Select(c => (short?)c.Id).FirstOrDefault();
    }
}

internal sealed partial class BotManager
{
    private bool _mapBattleTarget;
    private string? _battleTargetQuery;
    private bool _refreshMapIntel;
    private string? _battleMapSignature;
    private readonly HashSet<(long Generation, short Target)> _battleTargetsLogged = [];

    internal short? ConfigureShowcaseTarget(string? query)
    {
        _mapBattleTarget = true;
        _battleTargetQuery = query;
        ProbeClusters();
        lock (Probe.Gate)
        {
            // Numeric ids are local to this map. Preserve the resolved NAME as
            // operator intent across resets rather than reusing an unrelated id.
            if (query is not null && short.TryParse(query, out short id))
                _battleTargetQuery = Probe.World.Clusters.TryGetValue(id, out var cluster) ? cluster.Name : null;
            return ResolveShowcaseTarget(Probe.World);
        }
    }

    private short? ResolveShowcaseTarget(ClientWorld world)
    {
        if (!_mapBattleTarget || world.Graph is not { } graph) return null;
        short? target = null;
        if (_battleTargetQuery is { } query)
        {
            try { target = Catalog.ResolveSector(query, world.Clusters).Id; }
            catch (ArgumentException) { /* Names from the previous map are optional. */ }
        }
        bool derived = target is null;
        // Preserve the fixed-map, valid-target showcase path used by the live
        // home-swap trial. Activate generation resets only after a map change
        // or a derived target establishes that this fleet needs map adaptation.
        string signature = string.Join(';', graph.Clusters.OrderBy(c => c.Id).Select(c =>
        {
            world.Clusters.TryGetValue(c.Id, out var data);
            return $"{c.Id}:{c.HomeSector}:{data.StarSeed}:{data.Name}";
        })) + "/" + string.Join(';', graph.Warps.OrderBy(w => w.SourceClusterId).ThenBy(w => w.DestClusterId)
            .Select(w => $"{w.SourceClusterId}:{w.DestClusterId}"));
        if (derived || (_battleMapSignature is { } previous && previous != signature)) _refreshMapIntel = true;
        _battleMapSignature = signature;
        target ??= ShowcaseBattleSector.Choose(graph);
        _scenarioTargetOverride = target;
        if (target is { } sector && _battleTargetsLogged.Add((world.MissionGeneration, sector)))
            Console.WriteLine($"[battle-sector] cookie={world.Mission?.Cookie} generation={world.MissionGeneration} sector={sector}"
                + $" name={(world.Clusters.TryGetValue(sector, out var cluster) ? cluster.Name : "unknown")}"
                + $" source={(derived ? "derived" : "requested")} requested={_battleTargetQuery ?? "absent"}");
        return target;
    }
}
