using System.Globalization;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    internal sealed record DefenseSettings(bool Enabled, double Share, int Cap)
    {
        internal static DefenseSettings Read(Func<string, string?> read)
        {
            double share = double.TryParse(read("ALLEGIANCE_BOT_DEFEND_SHARE"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : .15;
            int cap = int.TryParse(read("ALLEGIANCE_BOT_DEFEND_CAP"), out var c) ? Math.Clamp(c, 0, 200) : 30;
            return new(read("ALLEGIANCE_BOT_DEFEND") is not ("0" or "off"), share, cap);
        }
    }

    internal const double DefenseQuietSeconds = 20;
    internal const double DefenseMinimumSeconds = 30;
    internal const float StationThreatRange = 2500;
    private DefenseSettings? _defenseSettings;
    private DefenseSettings DefenseConfig => _defenseSettings ??= DefenseSettings.Read(Environment.GetEnvironmentVariable);
    private sealed class DefenseIncident(double now)
    {
        public double Started = now;
        public double LastThreat = now;
        public bool Assigned;
    }
    // Decision-thread owned. Incidents are shared only within the same mission and side.
    private Dictionary<DefenseKey, DefenseIncident>? _defenseIncidents;
    private Dictionary<DefenseKey, DefenseIncident> DefenseIncidents => _defenseIncidents ??= new();
    private static DefenseKey DefenseKeyFor(ActiveBot bot, short sector) =>
        // Cookies identify the shared mission; generation counters are local to each client.
        new(bot.Cs!.World.Mission?.Cookie, bot.Cs.World.Mission is null ? bot.Cs.World.MissionGeneration : 0, bot.Side, sector);

    private static bool IsDefenseFighter(ActiveBot bot) =>
        (bot.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase)
         || bot.Role.Equals("interceptor", StringComparison.OrdinalIgnoreCase))
        && CombatTargetSelector.ClassifyTargetId(ActualHullId(bot)) == BotTargetClass.Fighter;

    private static bool DefenseAvailable(ActiveBot bot) => IsParticipating(bot) && !bot.SpawnPending
        && !bot.ObjectivesPending && !bot.InScenario && !bot.Arena && bot.Behavior == "combat"
        && bot.Cs is not null && !bot.FinaleEscort && !bot.IsPod && !bot.PodDocked
        && !Catalog.IsLifepodHull(ActualHullId(bot));

    private void UpdateDefense(ActiveBot[] bots, double now)
    {
        if (!DefenseConfig.Enabled) return;
        var liveKeys = new HashSet<DefenseKey>();
        foreach (var bot in bots)
        {
            if (!IsParticipating(bot) || bot.SpawnPending || bot.InScenario || bot.Arena
                || bot.Behavior != "combat" || bot.Cs is not { } cs) continue;
            lock (cs.Gate)
            {
                foreach (var station in cs.World.Stations.Values.Where(s => s.SideId == bot.Side))
                    liveKeys.Add(DefenseKeyFor(bot, station.ClusterId));
                // Each client consumes each alert exactly once, including bounded log rollover.
                foreach (var chat in cs.World.Events.ChatsAfter(bot.DefenseChatCursor))
                {
                    if (chat.ChatTarget != (int)ChatTarget.Team || chat.SidSender > 0) continue;
                    foreach (var station in cs.World.Stations.Values.Where(s => s.SideId == bot.Side))
                    {
                        if (!cs.World.Clusters.TryGetValue(station.ClusterId, out var cluster)) continue;
                        string prefix = $"{station.Name} in {cluster.Name} ";
                        if (!chat.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string warning = chat.Message[prefix.Length..];
                        if (warning.StartsWith("under attack by ", StringComparison.OrdinalIgnoreCase)
                            || warning.StartsWith("critically damaged and under attack by ", StringComparison.OrdinalIgnoreCase))
                            NoteDefenseThreat(DefenseKeyFor(bot, station.ClusterId), now);
                    }
                }
                bot.DefenseChatCursor = cs.World.Events.ChatSequence;
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me))
                {
                    var threats = VisibleDefenseTargets(bot, cs, me.SectorId, now, alerted: false);
                    if (threats.Count > 0 && cs.World.Stations.Values.Any(s => s.SideId == bot.Side && s.ClusterId == me.SectorId))
                        NoteDefenseThreat(DefenseKeyFor(bot, me.SectorId), now);
                }
            }
        }

        if (Partition is not null)
        {
            ApplyGlobalDefense(bots, now);
            return;
        }

        foreach (var (key, incident) in DefenseIncidents.ToArray())
        {
            if (!liveKeys.Contains(key) || (now - incident.LastThreat >= DefenseQuietSeconds
                && now - incident.Started >= DefenseMinimumSeconds))
            {
                foreach (var bot in bots.Where(b => b.Cs is not null && b.DefenseSector == key.Sector && DefenseKeyFor(b, key.Sector) == key))
                    EndDefense(bot);
                DefenseIncidents.Remove(key);
                continue;
            }
            if (incident.Assigned) continue; // repeated alerts extend this deployment, never recruit a new wing
            var side = bots.Where(b => DefenseAvailable(b) && DefenseKeyFor(b, key.Sector) == key).ToArray();
            if (side.Length == 0) continue; // world/objective sync can finish after the alert
            int budget = Math.Min(DefenseConfig.Cap, (int)Math.Ceiling(side.Length * DefenseConfig.Share));
            budget -= side.Count(b => b.DefenseSector is not null);
            var ranked = side.Where(b => b.DefenseSector is null && IsDefenseFighter(b))
                .Select(b => (Bot: b, Distance: DefenseDistance(b, key.Sector)))
                .Where(b => b.Distance.Hops >= 0)
                .OrderBy(b => b.Distance.Hops).ThenBy(b => b.Distance.Local).ThenBy(b => b.Bot.ShipId)
                .Take(Math.Max(0, budget)).ToArray();
            foreach (var (bot, _) in ranked)
            {
                bot.DefenseSector = key.Sector;
                bot.TargetId = null;
            }
            // A delayed world sync still gets a full minimum deployment. A zero
            // budget is final for this incident, including when another base used it.
            incident.Assigned = ranked.Length > 0 || budget <= 0;
            if (ranked.Length > 0) incident.Started = now;
        }
        // A mission change or side change must never carry an old detour into a new map.
        foreach (var bot in bots.Where(b => b.Cs is not null && b.DefenseSector is not null))
            if (!DefenseAvailable(bot) || !IsDefenseFighter(bot) || !DefenseIncidents.ContainsKey(DefenseKeyFor(bot, bot.DefenseSector!.Value)))
                EndDefense(bot);
    }

    private void NoteDefenseThreat(DefenseKey key, double now)
    {
        if (Partition is not null) _localThreats[key] = now;
        if (!DefenseIncidents.TryGetValue(key, out var incident)) DefenseIncidents[key] = new(now);
        else incident.LastThreat = now;
    }

    private static (int Hops, float Local) DefenseDistance(ActiveBot bot, short sector)
    {
        var cs = bot.Cs!;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } pose || me.StationId >= 0) return (-1, 0);
            var path = cs.World.Graph?.FindShortestPath(me.SectorId, sector);
            int hops = me.SectorId == sector ? 0 : path is { Count: > 0 } ? path.Count - 1 : -1;
            float distance = cs.World.Stations.Values.Where(s => s.SideId == bot.Side && s.ClusterId == sector && s.Position is { Length: 3 })
                .Select(s => (Vec3(s.Position) - pose.Position).LengthSquared()).DefaultIfEmpty(float.MaxValue).Min();
            return (hops, hops == 0 ? distance : 0);
        }
    }

    private static void EndDefense(ActiveBot bot)
    {
        bot.DefenseSector = null;
        bot.DefendingInPlace = false;
        bot.DefenseTargets.Clear();
        bot.TargetId = null;
        // The objective, assigned route, arrival and staging phase were never overwritten.
    }

    // All targets come from this client's visible table and real received hit events.
    private static HashSet<short> VisibleDefenseTargets(ActiveBot bot, ConnectionSession cs, short sector, double now, bool alerted)
    {
        var targets = new HashSet<short>();
        lock (cs.Gate)
        {
            var stations = cs.World.Stations.Values.Where(s => s.SideId == bot.Side && s.ClusterId == sector).ToArray();
            if (stations.Length == 0 && bot.RecentAssetAttackers.Count == 0) return targets;
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.SideId < 0 || ship.SideId == bot.Side || ship.IsNotVisible || ship.IsDead || ship.IsGone
                    || ship.SectorId != sector || ship.Latest is not { } pose) continue;
                var cls = CombatTargetSelector.ClassifyTargetId(ship.HullId);
                if (IgnoreMinerTargets && cls == BotTargetClass.MinerDrone) continue;
                bool assetAttacker = bot.RecentAssetAttackers.TryGetValue(ship.ShipId, out var at) && now - at < ThreatWindowSeconds;
                bool nearStation = stations.Any(s => s.Position is { Length: 3 }
                    && (Vec3(s.Position) - pose.Position).LengthSquared() <= StationThreatRange * StationThreatRange);
                if (assetAttacker || nearStation || (alerted && stations.Length > 0)) targets.Add(ship.ShipId);
            }
        }
        return targets;
    }

    private bool TryDriveDefense(ActiveBot bot, short sector, double now)
    {
        if (!DefenseConfig.Enabled || bot.Arena || bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)
            || CombatTargetSelector.ClassifyTargetId(ActualHullId(bot)) is BotTargetClass.MinerDrone or BotTargetClass.Bomber) return false;
        var cs = bot.Cs!;
        bool alerted = DefenseIncidents.ContainsKey(DefenseKeyFor(bot, sector));
        bot.DefenseTargets = VisibleDefenseTargets(bot, cs, sector, now, alerted);
        bool attacked = HasVisibleAttacker(bot, cs, sector, now);
        if (attacked)
            foreach (var (id, at) in bot.RecentAttackers)
                if (now - at < ThreatWindowSeconds) bot.DefenseTargets.Add(id);
        // Nearby fighters and reinforcements defend before considering any outward warp.
        if (bot.DefenseTargets.Count > 0 || attacked)
        {
            bot.DefendingInPlace = true;
            EngageCombat(bot, now, null); // fresh candidates: this tick may have just drained a hit
            bot.LastCluster = sector;
            return true;
        }
        bot.DefendingInPlace = false;
        if (alerted)
        {
            bot.DefendingInPlace = true;
            LoiterCombat(bot, now);
            bot.LastCluster = sector;
            return true;
        }
        if (bot.DefenseSector is not { } home) return false;
        if (sector == home) LoiterCombat(bot, now);
        else
        {
            // Keep the existing full-throttle exit clearance on a defence warp leg.
            if (bot.ExitClearFrom is { } exit && TryCsPose(cs, bot.ShipId, out var pos, out var orient)
                && !IsClearOfAleph(pos, exit, bot.ExitClearRadius, ExitClearMargin))
            {
                if (TryDodgeGoal(cs, bot, pos, out var dodge)) CombatFlyTo(bot, dodge, 15f, 200f, 0);
                else cs.CommandControls(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(pos, orient,
                    ExitClearGoal(pos, exit, bot.ExitClearRadius, ExitClearMargin)), 0);
                bot.LastCluster = sector;
                return true;
            }
            bot.ExitClearFrom = null;
            List<short>? path;
            lock (cs.Gate) path = cs.World.Graph?.FindShortestPath(sector, home);
            if (path is not { Count: >= 2 } || !TransitWarp(cs, bot, sector, path[1])) LoiterCombat(bot, now);
        }
        bot.LastCluster = sector;
        return true;
    }
}
