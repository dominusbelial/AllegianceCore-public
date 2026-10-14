using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private List<FinaleRun>? _finales;

    private sealed class FinaleRun(ShowcaseFinale.Settings settings, double created,
        ActiveBot[] bots, Dictionary<short, FinalePlan> plans)
    {
        internal readonly object Sync = new();
        internal readonly ShowcaseFinale.Settings Settings = settings;
        internal readonly double Created = created;
        internal readonly ActiveBot[] Bots = bots;
        internal readonly Dictionary<short, FinalePlan> Plans = plans;
        internal readonly Queue<(double At, short Side)> Kills = new();
        internal ShowcaseFinale.Maturity[] Maturity = [];
        internal short[] Sides = [];
        internal double? TriggeredAt, StrikeStartedAt, GameOverAt;
        internal short? Winner;
        internal string? EndReason;
        internal double LastUpdate = double.NegativeInfinity;
        internal bool Rotated;
        internal long CohesionSamples, Protected, BombersKilled;
        internal double CohesionDistance;
    }

    private sealed class FinalePlan(short targetId, short targetSide, short sector,
        ShowcaseFinale.StrikeLoadout loadout)
    {
        internal readonly short TargetId = targetId, TargetSide = targetSide, Sector = sector;
        internal readonly ShowcaseFinale.StrikeLoadout Loadout = loadout;
        internal double? KilledAt;
        internal int BomberBudget, EscortBudget;
    }

    private sealed record FinaleOriginal(short Hull, string Name, IReadOnlyList<ExpandedPartData>? Parts,
        short? Weapon, short? Launcher, double ReadyTime, short Wingman, short? Target, int RouteIndex, bool Arena, string Role);

    public object ConfigureFinale(ShowcaseFinale.Settings settings, IReadOnlyCollection<string> names)
    {
        settings.Validate();
        if (!settings.Enabled) return new { enabled = false };
        DataStationIGC[] stations;
        lock (Probe.Gate) stations = Probe.World.Stations.Values.ToArray();
        return RegisterFinale(settings, names, stations);
    }

    internal object RegisterFinale(ShowcaseFinale.Settings settings, IReadOnlyCollection<string> names,
        IReadOnlyList<DataStationIGC> stations)
    {
        settings.Validate();
        if (!settings.Enabled) return new { enabled = false };
        ActiveBot[] bots;
        lock (_gate) bots = _bots.Where(b => names.Contains(b.Name)).ToArray();
        var core = Catalog.RepresentativeCore();
        var plans = new Dictionary<short, FinalePlan>();
        foreach (short side in bots.Select(b => b.Side).Distinct())
        {
            var targets = stations.Where(s => s.SideId >= 0 && s.SideId != side
                && core.StationTypes.ContainsKey(s.StationTypeId))
                .OrderByDescending(s => core.StationTypes[s.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart))
                .ThenBy(s => s.StationId).ToArray();
            if (targets.Length == 0) throw new ArgumentException("spawn_scenario: finale needs an observed enemy home station");
            var target = targets[0];
            plans[side] = new(target.StationId, target.SideId, target.ClusterId,
                ShowcaseFinale.SelectLoadout(core, core.StationTypes[target.StationTypeId]));
        }
        var run = new FinaleRun(settings, Now(), bots, plans);
        run.Maturity = plans.Keys.Order().Select(side => new ShowcaseFinale.Maturity(side,
            bots.Count(b => b.Side == side), bots.Count(b => b.Side == side && b.CompletedPodCycles > 0),
            bots.Where(b => b.Side == side).Sum(b => b.Kills))).ToArray();
        lock (_gate)
        {
            foreach (var bot in bots) bot.Finale = run;
            (_finales ??= new()).Add(run);
        }
        return FinaleView(run, Now());
    }

    public object[] FinaleStatus()
    {
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        return runs.Select(r => FinaleView(r, Now())).ToArray();
    }

    private static object FinaleView(FinaleRun run, double now)
    {
        // Never take a client gate while holding Sync on the status thread:
        // combat sampling records hits/kills in the opposite order.
        int idleBots = run.Bots.Count(b => FinaleBotIdle(b, now));
        lock (run.Sync)
        {
            var config = run.Settings;
            return new
            {
                enabled = true, settings = config,
                state = run.Rotated ? "next mission" : run.GameOverAt is not null ? "game over"
                    : run.TriggeredAt is not null ? "strike" : "armed",
                elapsedSeconds = now - run.Created,
                minimumTimeMet = now - run.Created >= config.AfterMinutes * 60,
                relaunchMature = run.Maturity.Length == 2 && run.Maturity.All(m => m.RelaunchShare >= config.MinRelaunchShare),
                killsLastFiveMinutes = run.Kills.Count(k => now - k.At < 300),
                killsRequired = config.MinKillsLastFiveMinutes,
                combatSustained = run.Kills.Count(k => now - k.At < 300) >= config.MinKillsLastFiveMinutes,
                maturity = run.Maturity.Select(m => new { side = m.Side, combatBots = m.CombatBots,
                    relaunched = m.Relaunched, share = m.RelaunchShare, requiredShare = config.MinRelaunchShare,
                    message = $"finale armed: {m.RelaunchShare:P0} relaunched, need {config.MinRelaunchShare:P0}" }).ToArray(),
                selectedSides = run.Sides,
                triggeredAt = Relative(run.TriggeredAt), strikeStartedAt = Relative(run.StrikeStartedAt),
                gameOverAt = Relative(run.GameOverAt), winner = run.Winner, gameOverReason = run.EndReason,
                wings = run.Plans.Select(kv => new { side = kv.Key, targetStation = kv.Value.TargetId,
                    targetSide = kv.Value.TargetSide, targetSector = kv.Value.Sector, loadout = kv.Value.Loadout,
                    reservedBombers = kv.Value.BomberBudget, escortBudget = kv.Value.EscortBudget,
                    requestedBombers = config.Bombers,
                    bomberShortfall = run.Sides.Contains(kv.Key) ? Math.Max(0, config.Bombers - kv.Value.BomberBudget) : 0,
                    assignedBombers = run.Bots.Count(b => b.Side == kv.Key && b.FinaleBomberActive),
                    armedBombers = run.Bots.Count(b => b.Side == kv.Key && b.FinaleBomberActive && b.FinaleGrantVerified),
                    escorts = run.Bots.Count(b => b.Side == kv.Key && b.FinaleEscort),
                    targetKilledAt = Relative(kv.Value.KilledAt),
                    timeToKillSeconds = kv.Value.KilledAt is { } kill && run.StrikeStartedAt is { } start ? (double?)(kill - start) : null,
                    grants = run.Bots.Where(b => b.Side == kv.Key && b.FinaleBomberActive)
                        .Select(b => new { b.Name, grantedHull = b.Granted.HullId, verified = b.FinaleGrantVerified, isPod = b.IsPod, docked = b.PodDocked, warning = b.GrantWarning }).ToArray(),
                }).ToArray(),
                escortCohesionAverageDistance = run.CohesionSamples > 0 ? (double?)(run.CohesionDistance / run.CohesionSamples) : null,
                escortCohesionSamples = run.CohesionSamples,
                bomberProtected = run.Protected, bomberKilled = run.BombersKilled,
                nextMission = new { observed = run.Rotated,
                    resetBots = run.Bots.Count(b => b.FinaleResetObserved),
                    readyBots = run.Bots.Count(b => b.FinaleResetObserved && b.ReadyShipId == b.ShipId),
                    activeBots = run.Bots.Count(b => b.FinaleResetObserved && IsParticipating(b)
                        && !b.ObjectivesPending && b.Launched && b.HasPos),
                    idleBots,
                    pendingBots = run.Bots.Count(b => !b.FinaleResetObserved || b.ObjectivesPending || b.ReadyShipId != b.ShipId),
                },
            };
            double? Relative(double? at) => at is { } t ? t - run.Created : null;
        }
    }

    private void UpdateFinales(double now)
    {
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        foreach (var run in runs)
        lock (run.Sync)
        {
            if (now - run.LastUpdate < 1) continue;
            run.LastUpdate = now;
            if (run.Rotated) continue;
            while (run.Kills.TryPeek(out var kill) && now - kill.At >= 300) run.Kills.Dequeue();
            var bots = run.Bots.Where(b => IsParticipating(b) && !b.SpawnPending).ToArray();
            run.Maturity = run.Plans.Keys.Order().Select(side => new ShowcaseFinale.Maturity(side,
                bots.Count(b => b.Side == side), bots.Count(b => b.Side == side && b.CompletedPodCycles > 0),
                run.Bots.Where(b => b.Side == side).Sum(b => b.Kills))).ToArray();
            foreach (var bot in bots)
            {
                var cs = bot.Cs!;
                lock (cs.Gate)
                {
                    if (cs.World.GameOverResult is { } over)
                    {
                        run.GameOverAt ??= now;
                        run.Winner = over.SideWinner;
                        run.EndReason = over.GameoverMessage;
                    }
                    if (run.TriggeredAt is not null && run.Plans.TryGetValue(bot.Side, out var plan)
                        && plan.KilledAt is null && cs.World.WorldReady
                        && !cs.World.Stations.ContainsKey(plan.TargetId)) plan.KilledAt = now;
                }
                if (bot.FinaleProtectedSince is { } attacked && now - attacked >= ThreatWindowSeconds)
                {
                    if (!bot.IsPod && bot.FinaleBomberActive) run.Protected++;
                    bot.FinaleProtectedSince = null;
                }
                if (bot.FinaleEscort && TryEscortAnchor(bot, out var center, out var anchorSector)
                    && anchorSector == CsSector(cs) && TryCsPose(cs, bot.ShipId, out var pos, out _))
                { run.CohesionDistance += (center - pos).Length(); run.CohesionSamples++; }
            }
            if (run.TriggeredAt is not null || run.GameOverAt is not null) continue;
            var sides = ShowcaseFinale.TriggerSides(run.Settings, now - run.Created, run.Maturity, run.Kills.Count);
            if (sides.Length == 0) continue;
            run.Sides = sides;
            run.TriggeredAt = now;
            foreach (short side in sides)
            {
                var bombers = bots.Where(b => b.Side == side && IsFinaleBomber(b))
                    .OrderBy(b => b.ShipId).Take(run.Settings.Bombers).ToArray();
                foreach (var bot in bombers) bot.FinaleBomberReserved = true;
                var plan = run.Plans[side];
                plan.BomberBudget = bombers.Length;
                plan.EscortBudget = bombers.Length == 0 ? 0 : run.Settings.EscortBudget(bots.Count(b => b.Side == side && IsFinaleFighter(b)));
            }
        }
    }

    private static bool IsFinaleBomber(ActiveBot bot) => bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)
        || (Catalog.RepresentativeCore().HullTypes.TryGetValue(bot.HullId, out var hull)
            && Showcase.ClassifyHull(hull) == Showcase.HullRole.Bomber);

    private static bool IsFinaleFighter(ActiveBot bot) =>
        (bot.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase) || bot.Role.Equals("interceptor", StringComparison.OrdinalIgnoreCase))
        && CombatTargetSelector.ClassifyTargetId(bot.HullId) == BotTargetClass.Fighter;

    // Called only for a docked pod's first normal launch request; never recalls an engaged ship.
    private static void PrepareFinaleRelaunch(ActiveBot bot, double now)
    {
        if (bot.Finale is not { } run || !bot.PodDocked) return;
        lock (run.Sync)
        {
            if (run.TriggeredAt is null || run.GameOverAt is not null || run.Rotated || !run.Sides.Contains(bot.Side)) return;
            var plan = run.Plans[bot.Side];
            if (bot.FinaleOriginal is not null) return; // subsequent purchases retain the assigned wing/loadout
            if (!bot.FinaleBomberReserved && !bot.FinaleEscort
                && IsFinaleFighter(bot) && run.Bots.Count(b => b.Side == bot.Side && b.FinaleEscort) < plan.EscortBudget)
                bot.FinaleEscort = true;
            if (!bot.FinaleBomberReserved && !bot.FinaleEscort) return;
            bot.FinaleOriginal ??= new(bot.HullId, bot.HullName, bot.Parts, bot.WeaponPartId,
                bot.LauncherPartId, bot.MissileReadyTime, bot.WingmanBehaviour, bot.TargetSector, bot.RouteIndex, bot.Arena, bot.Role);
            if (bot.FinaleBomberReserved)
            {
                var loadout = plan.Loadout;
                bot.HullId = loadout.HullId; bot.HullName = loadout.HullName; bot.Parts = loadout.Parts;
                bot.WeaponPartId = Catalog.FirstWeaponPartId(loadout.Parts);
                bot.LauncherPartId = loadout.Parts.Where(p => Catalog.RepresentativeCore().Launchers.TryGetValue(p.PartId, out var l)
                    && Catalog.RepresentativeCore().MissileTypes.ContainsKey(l.ExpendabletypeId)).Select(p => (short?)p.PartId).FirstOrDefault();
                bot.MissileReadyTime = bot.LauncherPartId is { } id ? Catalog.LauncherReadyTime(id) : 0;
                bot.MineDispenserPartId = null;
                bot.WingmanBehaviour |= Showcase.WbbmUseMissiles;
                bot.Role = "bomber";
                bot.FinaleBomberActive = true;
            }
            EndDefense(bot);
            bot.TargetSector = plan.Sector;
            bot.Arena = false;
            bot.RoutePath = null; bot.RouteIndex = -1;
        }
    }

    private static void RestoreFinaleForNextMission(ActiveBot bot)
    {
        if (bot.Finale is not { } run) return;
        lock (run.Sync)
        {
            run.Rotated = true;
            if (bot.FinaleOriginal is { } original)
            {
                bot.HullId = original.Hull; bot.HullName = original.Name; bot.Parts = original.Parts;
                bot.WeaponPartId = original.Weapon; bot.LauncherPartId = original.Launcher;
                bot.MissileReadyTime = original.ReadyTime; bot.WingmanBehaviour = original.Wingman;
                bot.TargetSector = original.Target;
                bot.RouteIndex = original.RouteIndex; bot.Arena = original.Arena; bot.Role = original.Role;
                bot.MineDispenserPartId = original.Parts is { } parts ? FindMineDispenser(parts) : null;
            }
            bot.FinaleBomberReserved = bot.FinaleBomberActive = bot.FinaleEscort = false;
            bot.FinaleProtectedSince = null;
            bot.FinaleGrantVerified = false;
            bot.CompletedPodCycles = 0;
            bot.FinaleResetObserved = true;
        }
    }

    private static bool FinaleBotIdle(ActiveBot bot, double now)
    {
        if (bot.Cs is not { } cs || !bot.FinaleResetObserved || bot.ObjectivesPending) return false;
        lock (cs.Gate)
        {
            float speed = cs.World.Ships.TryGetValue(bot.ShipId, out var ship) && ship.Latest is { } pose
                ? pose.Velocity.Length() : 0;
            return IsBotIdle(bot.PodDocked, now, bot.LastMoveAt, speed);
        }
    }

    // Position and visibility always come from the escort's own world; sharing wing
    // membership/received bomber hit events grants no unseen enemy coordinates.
    private static bool TryEscortAnchor(ActiveBot bot, out Vector3 anchor, out short sector)
    {
        anchor = default; sector = -1;
        if (bot.Finale is not { } run || bot.Cs is not { } cs) return false;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } pose) return false;
            var candidates = run.Bots.Where(b => b.Side == bot.Side && b.FinaleBomberActive && !b.IsPod)
                .Select(b => cs.World.Ships.TryGetValue(b.ShipId, out var ship) ? ship : null)
                .Where(s => s is { IsDead: false, IsGone: false, IsNotVisible: false, Latest: not null })
                .OrderBy(s => s!.SectorId == own.SectorId ? 0 : 1)
                .ThenBy(s => (s!.Latest!.Position - pose.Position).LengthSquared()).ThenBy(s => s!.ShipId).FirstOrDefault();
            if (candidates?.Latest is not { } leader) return false;
            anchor = leader.Position; sector = candidates.SectorId;
            return true;
        }
    }

    private bool TryDriveFinaleEscort(ActiveBot bot, double now)
    {
        if (!bot.FinaleEscort || bot.Finale is not { Rotated: false } run) return false;
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out _)) return true;
        if (bot.ExitClearFrom is { } exit && !IsClearOfAleph(pos, exit, bot.ExitClearRadius, ExitClearMargin)
            && TryCsPose(cs, bot.ShipId, out _, out var orient))
        {
            if (TryDodgeGoal(cs, bot, pos, out var dodge)) CombatFlyTo(bot, dodge, 15, 200, 0);
            else cs.CommandControls(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(pos, orient,
                ExitClearGoal(pos, exit, bot.ExitClearRadius, ExitClearMargin)), 0);
            return true;
        }
        bot.ExitClearFrom = null;
        if (!TryEscortAnchor(bot, out var anchor, out short sector))
        {
            // Wait at the launch point for a newly purchased bomber rather than
            // racing an unarmed escort alone to the enemy garrison.
            bot.TargetId = null;
            LoiterCombat(bot, now);
            return true;
        }
        short ownSector = CsSector(cs);
        if (sector != ownSector)
        {
            List<short>? path;
            lock (cs.Gate) path = cs.World.Graph?.FindShortestPath(ownSector, sector);
            if (path is not { Count: >= 2 } || !TransitWarp(cs, bot, ownSector, path[1])) LoiterCombat(bot, now);
            return true;
        }
        var enemies = new List<ShowcaseFinale.EscortEnemy>();
        lock (cs.Gate)
            foreach (var enemy in cs.World.Ships.Values)
            {
                if (enemy.SideId < 0 || enemy.SideId == bot.Side || enemy.IsNotVisible || enemy.IsDead || enemy.IsGone
                    || enemy.SectorId != ownSector || enemy.Latest is not { } e || Catalog.IsLifepodHull(enemy.HullId)) continue;
                bool attacker = run.Bots.Any(b => b.Side == bot.Side && b.FinaleBomberActive
                    && b.RecentAttackers.TryGetValue(enemy.ShipId, out var hitAt) && now - hitAt < ThreatWindowSeconds);
                enemies.Add(new(enemy.ShipId, e.Position, e.Velocity, attacker));
            }
        var target = ShowcaseFinale.EscortTarget(pos, anchor, enemies, run.Settings.EscortRadius, run.Settings.Leash);
        bot.TargetId = target;
        if (target is { } id) EngageShipAt(bot, cs, id, now, 0, 0);
        else CombatFlyTo(bot, anchor, 15, Math.Max(100, run.Settings.EscortRadius * .3f), 0);
        return true;
    }

    private static short? FinaleStationTarget(ActiveBot bot, short sector)
    {
        if (!bot.FinaleBomberActive || bot.Finale is not { } run || !run.Plans.TryGetValue(bot.Side, out var plan)) return null;
        var cs = bot.Cs!;
        lock (cs.Gate)
            return cs.World.Stations.TryGetValue(plan.TargetId, out var target) && target.SideId != bot.Side
                && target.ClusterId == sector ? plan.TargetId : null;
    }

    // Duties take precedence in BOTH P622 modes. Ordinary selection remains unchanged.
    private static short? PickDefenseDutyTarget(ActiveBot bot, short sector, Vector3 pos)
    {
        if (!bot.DefendingInPlace) return null;
        var cs = bot.Cs!;
        lock (cs.Gate)
            return cs.World.Ships.Values.Where(s => bot.DefenseTargets.Contains(s.ShipId)
                    && s.SideId >= 0 && s.SideId != bot.Side && s.SectorId == sector
                    && !s.IsNotVisible && !s.IsDead && !s.IsGone && s.Latest is not null)
                .OrderByDescending(s => CombatTargetSelector.IsStationThreatening(CombatTargetSelector.ClassifyTargetId(s.HullId)))
                .ThenBy(s => (s.Latest!.Position - pos).LengthSquared()).ThenBy(s => s.ShipId)
                .Select(s => (short?)s.ShipId).FirstOrDefault();
    }
}
