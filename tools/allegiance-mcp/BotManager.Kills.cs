using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private static bool KillsGame(ConnectionSession cs) => cs.World.Mission?.Goals.IsDeathMatchGame == true;

    // GAME_STATE has NO kill field (Inc/Messages.h:854-859). Observe mission-wide
    // KILL_SHIP instead: FedSrv.CPP:11811-11814,11867-11887,11914-11915.
    // PLAYERINFO seeds visible player counters (appmsg.cpp:2256). A late join
    // cannot reconstruct earlier non-player credits; these are observed scores,
    // never a query of the server or another side's private connection.
    internal sealed class KillScore
    {
        private long _generation = -1, _cursor;
        private readonly Dictionary<short, int> _counts = new();
        private readonly Dictionary<short, int> _observedShipCredits = new();
        private readonly HashSet<string> _seededPlayers = new(StringComparer.Ordinal);
        internal int For(short side) => _counts.GetValueOrDefault(side);
        internal void Observe(ClientWorld world)
        {
            if (_generation != world.MissionGeneration)
            {
                _generation = world.MissionGeneration;
                _counts.Clear();
                _observedShipCredits.Clear();
                _seededPlayers.Clear();
                _cursor = world.Events.KillSequence - world.Events.Kills.Count;
            }
            foreach (var kill in world.Events.KillsAfter(_cursor))
                if (kill.BKillCredit && kill.SideIdKiller >= 0)
                {
                    _counts[kill.SideIdKiller] = For(kill.SideIdKiller) + 1;
                    if (kill.TypeCredit == (short)IgcObjectType.Ship)
                        _observedShipCredits[kill.IdCredit] = _observedShipCredits.GetValueOrDefault(kill.IdCredit) + 1;
                }
            _cursor = Math.Max(_cursor, world.Events.KillSequence);
            // Seed each identity once, subtracting credits already seen on the
            // public stream. Repeated PLAYERINFOs or a known pilot changing
            // sides must not transfer/re-add that pilot's old kills.
            foreach (var ship in world.Ships.Values.Where(s => s.Player is not null && s.SideId >= 0))
                if (_seededPlayers.Add(ship.Player!.CharacterName))
                    _counts[ship.SideId] = For(ship.SideId)
                        + Math.Max(0, ship.Player.NKills - _observedShipCredits.GetValueOrDefault(ship.ShipId));
        }
    }

    private sealed partial class ActiveBot
    {
        public KillScore? KillScores;
        public bool KillRecovering;
        public long KillGeneration = -1;
        public string KillTrace = "";
    }

    // Bot policy, not native constants: preserve a half-hull escape margin;
    // with <=20% of the goal left, leave at 70% to deny the decisive kill.
    // Resume at 95% (well above either entry bar, allowing wire quantization).
    internal static bool KillDanger(int enemy, int goal) => goal > 0 && goal - enemy <= Math.Max(1, (int)Math.Ceiling(goal * .2));
    internal static bool KillWithdraw(float hull, bool recovering, int enemy, int goal) =>
        hull < (recovering ? .95f : KillDanger(enemy, goal) ? .7f : .5f);

    private static bool KillTarget(ClientWorld world, ShipEntity ship, short side) => ship.SideId >= 0
        && ship.SideId != side && !world.AreAllied(side, ship.SideId) && FlagVisible(world, ship)
        && !Catalog.IsLifepodHull(ship.HullId);

    private void ReleaseUnscorableKillOrder(ActiveBot bot, ConnectionSession cs)
    {
        if (!KillsGame(cs) || HumanCommands(bot.Side)) return;
        lock (cs.Gate)
        {
            short? id = bot.OrderedCommand is { TargetType: IgcObjectType.Ship } order ? order.TargetId : bot.OrderedTargetId;
            if (id is { } target && cs.World.Ships.TryGetValue(target, out var ship)
                && (Catalog.IsLifepodHull(ship.HullId) || ship.SideId == bot.Side || cs.World.AreAllied(ship.SideId, bot.Side)))
            { bot.OrderedCommand = null; bot.OrderedTargetId = null; bot.TargetId = null; }
        }
    }

    private void TraceKills(ActiveBot bot, FlagDuty duty, short? anchor, double now)
    {
        TraceFlag(bot, duty, anchor, now);
        var score = ObserveKills(bot);
        int goal = bot.Cs!.World.Mission!.Goals.TeamKills;
        string trace = $"{duty}:{anchor}:{score.Ours}:{score.Enemy}:{goal}";
        if (bot.KillTrace == trace) return;
        bot.KillTrace = trace;
        Console.WriteLine($"[kills] t={now:F1} side={bot.Side} bot={bot.Name} duty={duty} anchor={anchor} score={score.Ours}:{score.Enemy} goal={goal}");
    }

    private static (float Hull, int Ours, int Enemy) ObserveKills(ActiveBot bot)
    {
        var cs = bot.Cs!;
        if (!KillsGame(cs)) return (1, 0, 0);
        lock (cs.Gate)
        {
            if (bot.KillGeneration != cs.World.MissionGeneration)
            { bot.KillGeneration = cs.World.MissionGeneration; bot.KillRecovering = false; }
            var scores = bot.KillScores ??= new();
            scores.Observe(cs.World);
            int enemy = Enumerable.Range(0, GameState.MaxSides).Select(s => (short)s)
                .Where(s => s != bot.Side && !cs.World.AreAllied(bot.Side, s)).Select(scores.For).DefaultIfEmpty().Max();
            float hull = cs.World.Ships.GetValueOrDefault(bot.ShipId)?.Latest?.HullFraction ?? 1;
            if (bot.KillRecovering && hull < .95f) hull = 0; // keep the shared planner's recovery slot until repaired
            return (hull, scores.For(bot.Side), enemy);
        }
    }

    private static KillContact[]? KillContacts(ActiveBot bot, short? targetId = null)
    {
        var cs = bot.Cs!;
        if (!KillsGame(cs)) return null;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is null) return [];
            var ships = cs.World.Ships.Values.Where(s => s.SideId >= 0 && FlagVisible(cs.World, s)
                && !Catalog.IsLifepodHull(s.HullId)).ToArray();
            bool Friendly(ShipEntity s) => s.SideId == bot.Side || cs.World.AreAllied(bot.Side, s.SideId);
            // Count each sector once, rather than rescanning every ship for
            // every enemy on every hunter's control tick at 200v200.
            var strength = ships.GroupBy(s => s.SectorId).ToDictionary(g => g.Key,
                g => (Friends: g.Count(Friendly), Enemies: g.Count(s => !Friendly(s))));
            return ships.Where(s => !Friendly(s) && (targetId is null || s.ShipId == targetId)).Select(s => new KillContact(s.ShipId, s.SectorId,
                FlagDistance(bot, s.SectorId, s.Latest!.Position), s.Latest!.HullFraction,
                strength[s.SectorId].Friends, strength[s.SectorId].Enemies, CombatTargetSelector.ClassifyTargetId(s.HullId)))
                .Where(c => c.Distance < double.MaxValue).ToArray();
        }
    }

    // Extend the SAME side budget, once on the supervisor or locally. Existing
    // objective duties get first refusal; kills expands total duty share to 60%
    // so a majority hunts rather than leaving deathmatch to incidental clashes.
    // Emergency returns may use the whole fleet; each name has ONE assignment.
    internal static FlagDeployment[] PlanKillsDuties(List<FlagCandidate> pilots)
    {
        var result = new List<FlagDeployment>();
        int enemy = pilots.Max(c => c.EnemyKills); // union of this side's observers, including older connections
        var returning = pilots.Where(c => c.Carrying).ToArray();
        var recovering = pilots.Where(c => c.Kills && !c.Carrying && KillWithdraw(c.Hull, false, enemy, c.KillGoal)).ToArray();
        int budget = Math.Min(pilots.Count, Math.Max((int)Math.Ceiling(pilots.Count * .6), returning.Length + recovering.Length));
        foreach (var c in returning) result.Add(new(c.Cookie, c.Side, c.Name, FlagDuty.Return));
        foreach (var c in recovering) result.Add(new(c.Cookie, c.Side, c.Name, FlagDuty.Recover, Post: enemy));
        var available = pilots.Where(c => !returning.Contains(c) && !recovering.Contains(c)).ToList();
        var other = available.Where(c => !c.Support && (c.Flags || c.Artifacts || c.Conquest)).Select(c => c with { Kills = false }).ToArray();
        foreach (var d in PlanFlagDuties(other).Take(Math.Max(0, budget - result.Count))) result.Add(d);
        var used = result.Select(d => d.Name).ToHashSet();
        var focused = new Dictionary<short, int>();
        foreach (var c in available.Where(c => c.Kills && !c.Support && !used.Contains(c.Name)).Take(Math.Max(0, budget - result.Count)))
        {
            // Equal or favorable observed numbers; under endgame pressure require
            // a spare friendly hull. Damaged enemies first, then short pursuits.
            var target = (c.KillTargets ?? []).Where(t => t.Friends >= t.Enemies + (KillDanger(enemy, c.KillGoal) ? 1 : 0)
                    && focused.GetValueOrDefault(t.Id) < CombatTargetSelector.CapFor(t.Class))
                .OrderBy(t => t.Hull).ThenBy(t => t.Distance).ThenBy(t => t.Id).FirstOrDefault();
            if (target is not null) focused[target.Id] = focused.GetValueOrDefault(target.Id) + 1;
            result.Add(new(c.Cookie, c.Side, c.Name, FlagDuty.Hunt, target?.Id, enemy));
        }
        return result.ToArray();
    }

    private bool TryDriveKills(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!KillsGame(cs)
            || HumanCommands(bot.Side) && bot.OrderedCommand is not null) return false;
        // A station can repair before the next planner pass clears Recover.
        // Complete the sortie even when that pass has already reassigned Hunt.
        lock (cs.Gate)
            if (bot.KillRecovering && cs.World.Ships.GetValueOrDefault(bot.ShipId) is { StationId: >= 0 } docked)
            {
                bot.KillRecovering = false;
                bot.MissionLaunchPending = true;
                TraceKills(bot, FlagDuty.Recover, docked.StationId, now);
                return true;
            }
        if (bot.FlagTask is not (FlagDuty.Hunt or FlagDuty.Recover)) return false;
        var score = ObserveKills(bot);
        int enemyScore = Math.Max(score.Enemy, bot.FlagPost);
        bot.TargetId = null;
        if (bot.FlagTask == FlagDuty.Recover)
        {
            bot.KillRecovering = true;
            DataStationIGC[] homes;
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.StationId >= 0)
                {
                    bot.KillRecovering = false;
                    bot.MissionLaunchPending = true;
                    TraceKills(bot, FlagDuty.Recover, me.StationId, now);
                    return true;
                }
                var hull = Catalog.RepresentativeCore().HullTypes[ActualHullId(bot)];
                short need = hull.HasCapability(DataHullTypeIGC.HabmFighter) ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand;
                homes = cs.World.Stations.Values.Where(s => s.SideId == bot.Side && s.Position is { Length: 3 }
                    && Catalog.RepresentativeCore().StationTypes.TryGetValue(s.StationTypeId, out var type) && type.HasCapability(need)).ToArray();
            }
            var home = homes.OrderBy(s => FlagDistance(bot, s.ClusterId, Vec3(s.Position))).FirstOrDefault();
            TraceKills(bot, FlagDuty.Recover, home.Position is null ? null : home.StationId, now);
            if (home.Position is null)
            {
                // A lost or incompatible base must not send a damaged pilot
                // back into the ordinary attack path. Separate from the nearest
                // observed threat while the ordinary support fleet can repair us.
                lock (cs.Gate)
                    if (cs.World.Ships.GetValueOrDefault(bot.ShipId)?.Latest is { } me)
                    {
                        var enemy = cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side
                            && !cs.World.AreAllied(bot.Side, s.SideId) && s.SectorId == CsSector(cs) && FlagVisible(cs.World, s))
                            .OrderBy(s => (s.Latest!.Position - me.Position).LengthSquared()).FirstOrDefault();
                        var away = enemy is null ? new Vector3(0, 1, 0) : (me.Position - enemy.Latest!.Position).Normalize();
                        CombatFlyTo(bot, me.Position + away * 1000, 50, 100, 0);
                    }
                return true;
            }
            if (FlagTravel(bot, cs, home.ClusterId, now)) return true;
            if (TryDockApproachControl(cs, bot, home.StationId, out var controls, out int movement)) cs.CommandControls(controls, movement);
            else CombatFlyTo(bot, Vec3(home.Position), 0, 100, 0);
            return true;
        }
        bot.KillRecovering = false;
        var targets = KillContacts(bot, bot.FlagAnchor) ?? [];
        var target = targets.FirstOrDefault(t => t.Id == bot.FlagAnchor
            && t.Friends >= t.Enemies + (KillDanger(enemyScore, cs.World.Mission!.Goals.TeamKills) ? 1 : 0));
        if (target is null)
        {
            if (bot.FlagAnchor is not null) targets = KillContacts(bot) ?? [];
            // Keep the normal exploration/clash march when no contact exists.
            // With unfavorable local odds, regroup at our observed home instead
            // of falling through into an unsafe ordinary nearest-target fight.
            short cur = CsSector(cs);
            if (targets.Any(t => t.Sector == cur && t.Friends < t.Enemies + (KillDanger(enemyScore, cs.World.Mission!.Goals.TeamKills) ? 1 : 0)))
            {
                DataStationIGC home;
                lock (cs.Gate) home = cs.World.Stations.Values.Where(s => s.SideId == bot.Side && s.Position is { Length: 3 })
                    .OrderBy(s => FlagDistance(bot, s.ClusterId, Vec3(s.Position))).FirstOrDefault();
                if (home.Position is not null)
                {
                    TraceKills(bot, FlagDuty.Hunt, null, now);
                    if (!FlagTravel(bot, cs, home.ClusterId, now)) CombatFlyTo(bot, Vec3(home.Position) + new Vector3(800, 0, 0), 150, 400, 0);
                    return true;
                }
            }
            return false;
        }
        TraceKills(bot, FlagDuty.Hunt, target.Id, now);
        if (FlagTravel(bot, cs, target.Sector, now)) return true;
        bot.TargetId = target.Id;
        int count = _tickEngagers.GetValueOrDefault(target.Id) + 1;
        _tickEngagers[target.Id] = count;
        EngageShipAt(bot, cs, target.Id, now, count, count);
        return true;
    }
}
