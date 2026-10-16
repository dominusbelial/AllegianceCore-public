namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private CoordinationState _coordinationState = CoordinationState.Empty;
    private CoordinationDecisions _coordinationDecisions = CoordinationDecisions.Empty;
    private long _coordinationReceived;
    private long _coordinationEpoch;
    private int _coordinationHolding;
    private double _lastCoordinationPublish = double.NegativeInfinity;
    private readonly Dictionary<DefenseKey, double> _localThreats = new();
    internal long CoordinationEpoch => Interlocked.Read(ref _coordinationEpoch);
    internal WorkerState WorkerSnapshot(System.Text.Json.JsonElement bots)
    {
        // State, timestamp and epoch are one immutable publication.
        var snapshot = Volatile.Read(ref _coordinationState);
        bool started = Volatile.Read(ref _startedLoop);
        return new(snapshot, bots, started ? Environment.TickCount64 - snapshot.PublishedAtMs : 0,
            started ? snapshot.Epoch : CoordinationEpoch, CoordinationEpoch);
    }
    internal bool CoordinationHolding => Volatile.Read(ref _coordinationHolding) != 0;
    internal void HoldCoordination(bool hold)
    {
        // Fence pre-spawn decisions and snapshots, including requests in flight.
        Interlocked.Increment(ref _coordinationEpoch);
        Volatile.Write(ref _coordinationHolding, hold ? 1 : 0);
    }
    internal void SetCoordination(CoordinationDecisions decisions)
    {
        if (decisions.Epoch != CoordinationEpoch) return;
        Volatile.Write(ref _coordinationDecisions, decisions);
        Interlocked.Exchange(ref _coordinationReceived, Environment.TickCount64);
    }
    private CoordinationDecisions? FreshCoordination
    {
        get
        {
            var decisions = Volatile.Read(ref _coordinationDecisions);
            return Environment.TickCount64 - Interlocked.Read(ref _coordinationReceived) < 2000 ? decisions : null;
        }
    }

    private void ApplyGlobalDefense(ActiveBot[] bots, double now, HashSet<DefenseKey> liveKeys)
    {
        var defense = (FreshCoordination?.Defense ?? []).Where(d => liveKeys.Contains(d.Key)).ToArray();
        var keys = defense.Select(d => d.Key).ToHashSet();
        foreach (var key in DefenseIncidents.Keys.Where(k => !keys.Contains(k)).ToArray()) DefenseIncidents.Remove(key);
        foreach (var deployment in defense)
        {
            if (!DefenseIncidents.ContainsKey(deployment.Key)) DefenseIncidents[deployment.Key] = new(now);
            var names = deployment.Names.ToHashSet();
            foreach (var bot in bots.Where(b => DefenseAvailable(b) && IsDefenseFighter(b) && names.Contains(b.Name)
                && DefenseKeyFor(b, deployment.Key.Sector) == deployment.Key)) bot.DefenseSector = deployment.Key.Sector;
        }
        foreach (var bot in bots.Where(b => b.DefenseSector is not null))
            if (!DefenseAvailable(bot) || !defense.Any(d => d.Key == DefenseKeyFor(bot, bot.DefenseSector!.Value) && d.Names.Contains(bot.Name))) EndDefense(bot);
    }

    private void PublishCoordination(ActiveBot[] bots, double now, long epoch)
    {
        if (now - _lastCoordinationPublish < .2) return;
        _lastCoordinationPublish = now;
        var stages = bots.Where(b => IsParticipating(b) && !b.SpawnPending && b.Cs is not null
            && b.Behavior == "combat" && b.UseStaging && b.TargetSector is not null && !b.IsPod && !b.PodDocked)
            .GroupBy(b => new StageKey(b.Side, b.TargetSector!.Value, b.RouteIndex, b.Cs!.World.Mission?.Cookie))
            .Select(g => new StageTally(g.Key, g.Count(), g.Count(b => b.StagePhase >= StageHoldPhase),
                g.Where(b => b.FirstStagingAt is not null).Select(b => now - b.FirstStagingAt!.Value).DefaultIfEmpty(0).Max(), g.Max(RouteHopsOf))).ToArray();
        var bases = new HashSet<DefenseKey>();
        foreach (var bot in bots.Where(DefenseAvailable))
            lock (bot.Cs!.Gate)
                foreach (var station in bot.Cs.World.Stations.Values.Where(s => s.SideId == bot.Side)) bases.Add(DefenseKeyFor(bot, station.ClusterId));
        var population = bots.Where(DefenseAvailable).GroupBy(b => DefenseKeyFor(b, 0))
            .Select(g => new DefensePopulation(g.Key.Cookie, g.Key.Generation, g.Key.Side, g.Count())).ToArray();
        var candidates = new List<DefenseCandidate>();
        foreach (var key in bases)
            foreach (var bot in bots.Where(b => DefenseAvailable(b) && IsDefenseFighter(b) && DefenseKeyFor(b, key.Sector) == key))
            {
                var distance = DefenseDistance(bot, key.Sector);
                candidates.Add(new(key, bot.Name, bot.ShipId, distance.Hops, distance.Local));
            }
        foreach (var key in _localThreats.Where(kv => now - kv.Value > DefenseMinimumSeconds || !bases.Contains(kv.Key)).Select(kv => kv.Key).ToArray()) _localThreats.Remove(key);
        Volatile.Write(ref _coordinationState, new(stages, bases.ToArray(), population, candidates.ToArray(),
            _localThreats.Select(kv => new DefenseThreat(kv.Key, now - kv.Value)).ToArray(), epoch, Environment.TickCount64));
    }
}
