namespace Allegiance.Tools.McpSpawner;

internal sealed record StageKey(short Side, short Goal, int Route, uint? Cookie);
internal sealed record StageTally(StageKey Key, int Alive, int Staged, double HoldSeconds, int Hops);
internal readonly record struct DefenseKey(uint? Cookie, long Generation, short Side, short Sector);
internal sealed record DefenseCandidate(DefenseKey Key, string Name, short ShipId, int Hops, float Distance);
internal sealed record DefensePopulation(uint? Cookie, long Generation, short Side, int Count);
internal sealed record DefenseThreat(DefenseKey Key, double AgeSeconds);
internal sealed record DefenseDeployment(DefenseKey Key, string[] Names);
internal sealed record FlagCandidate(uint Cookie, short Side, string Name, short ShipId, bool Scout,
    bool Carrying, short? Thief, double Distance, bool CanCarry = true);
internal sealed record FlagDeployment(uint Cookie, short Side, string Name, BotManager.FlagDuty Duty, short? Anchor = null, int Post = 0);
internal sealed record CoordinationState(StageTally[] Stages, DefenseKey[] Bases, DefensePopulation[] Population,
    DefenseCandidate[] Candidates, DefenseThreat[] Threats, long Epoch = 0, long PublishedAtMs = 0,
    IntelDigest[]? Intel = null, FlagCandidate[]? Flags = null)
{
    internal static readonly CoordinationState Empty = new([], [], [], [], []);
}
internal sealed record CoordinationDecisions(StageKey[] Releases, DefenseDeployment[] Defense, long Epoch = 0,
    IntelDigest[]? Intel = null, FlagDeployment[]? Flags = null)
{
    internal static readonly CoordinationDecisions Empty = new([], []);
}

/// <summary>Fleet barriers and recall quotas live here, on the supervisor only.</summary>
internal sealed class ScenarioCoordinator(BotManager.DefenseSettings settings, bool syncWings)
{
    private sealed class Incident(double started)
    {
        internal double Started = started, LastThreat = started;
        internal string[]? Names;
    }
    private readonly Dictionary<DefenseKey, Incident> _incidents = new();

    internal CoordinationDecisions Decide(IEnumerable<CoordinationState> states, double now, bool spawning)
    {
        var all = states.ToArray();
        var groups = all.SelectMany(s => s.Stages).GroupBy(s => s.Key).Select(g => new StageTally(g.Key,
            g.Sum(s => s.Alive), g.Sum(s => s.Staged), g.Max(s => s.HoldSeconds), g.Max(s => s.Hops))).ToArray();
        var releases = spawning ? [] : groups.Where(g =>
        {
            var side = groups.Where(o => o.Key.Side == g.Key.Side && o.Key.Goal == g.Key.Goal && o.Key.Cookie == g.Key.Cookie).ToArray();
            bool assembled = g.Staged >= g.Alive && (!syncWings || side.All(o => o.Staged >= o.Alive));
            return assembled || g.HoldSeconds >= BotManager.StagingHoldSeconds
                + BotManager.SyncOffsetSeconds(g.Key.Route, side.Max(o => o.Hops), g.Hops);
        }).Select(g => g.Key).ToArray();
        // P798 — sibling workers on the same side share their intel digests.
        // This is a union of OWN-side knowledge per (cookie, side); no worker can
        // inject another side's data because each digest is built from that
        // worker's own bots' worlds.
        var intel = all.SelectMany(s => s.Intel ?? []).ToArray();
        var flags = BotManager.PlanFlagDuties(all.SelectMany(s => s.Flags ?? []));
        if (!settings.Enabled) return new(releases, [], 0, intel, flags);

        var bases = all.SelectMany(s => s.Bases).ToHashSet();
        foreach (var threat in all.SelectMany(s => s.Threats))
        {
            if (!bases.Contains(threat.Key) || threat.AgeSeconds >= BotManager.DefenseQuietSeconds) continue;
            double at = now - threat.AgeSeconds;
            if (!_incidents.TryGetValue(threat.Key, out var incident)) _incidents[threat.Key] = incident = new(at);
            incident.LastThreat = Math.Max(incident.LastThreat, at);
        }
        foreach (var (key, incident) in _incidents.ToArray())
            if (!bases.Contains(key) || (now - incident.LastThreat >= BotManager.DefenseQuietSeconds
                && now - incident.Started >= BotManager.DefenseMinimumSeconds)) _incidents.Remove(key);

        var candidates = all.SelectMany(s => s.Candidates).ToArray();
        var population = all.SelectMany(s => s.Population).ToArray();
        foreach (var (key, incident) in _incidents.OrderBy(kv => kv.Key.Side).ThenBy(kv => kv.Key.Sector))
        {
            if (incident.Names is not null) continue;
            int count = population.Where(p => p.Cookie == key.Cookie && p.Generation == key.Generation && p.Side == key.Side).Sum(p => p.Count);
            if (count == 0) continue;
            var used = _incidents.Where(kv => kv.Key.Cookie == key.Cookie && kv.Key.Generation == key.Generation && kv.Key.Side == key.Side)
                .SelectMany(kv => kv.Value.Names ?? []).ToHashSet();
            int budget = BotManager.DefenseBudgetFor(count, used.Count, settings.Cap, settings.Share);
            var names = candidates.Where(c => c.Key == key && !used.Contains(c.Name) && c.Hops >= 0)
                .OrderBy(c => c.Hops).ThenBy(c => c.Distance).ThenBy(c => c.ShipId).Take(budget).Select(c => c.Name).ToArray();
            if (names.Length > 0 || budget == 0) { incident.Names = names; incident.Started = now; }
        }
        return new(releases, _incidents.Select(kv => new DefenseDeployment(kv.Key, kv.Value.Names ?? [])).ToArray(), 0, intel, flags);
    }
}
