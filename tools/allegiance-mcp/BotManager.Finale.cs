using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private List<FinaleRun>? _finales;

    private sealed class FinaleRun(ShowcaseFinale.Settings settings, double created,
        ActiveBot[] bots, Dictionary<short, FinalePlan> plans)
    {
        internal readonly object Sync = new();
        internal readonly Dictionary<ActiveBot, long> Generations = bots.ToDictionary(b => b, b => b.LifeGeneration);
        internal readonly Dictionary<ActiveBot, long> InitialKills = bots.ToDictionary(b => b, b => b.Kills);
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
        internal bool Rotated, NextRunCreated;
        internal long CohesionSamples, Protected, BombersKilled;
        internal double CohesionDistance;
        // P778a — bomber push telemetry, aggregated across the wing for the
        // per-strike summary (contacts and measured hull damage to both parties).
        internal long PushContacts;
        internal double PushTotalDamage;
    }

    private sealed class FinalePlan(short targetId, short targetSide, short sector,
        short targetTypeId, ShowcaseFinale.StrikeLoadout? loadout)
    {
        internal readonly short TargetId = targetId, TargetSide = targetSide, Sector = sector;
        internal readonly short TargetTypeId = targetTypeId;
        internal ShowcaseFinale.StrikeLoadout? Loadout = loadout;
        internal double? KilledAt;
        internal int BomberBudget, EscortBudget;
        // P778a — first time a live strike bomber of this side is inside its own
        // launch range of the target (the push goal), and whether the strike
        // summary has been logged (launch-range reach or bomber death, one each).
        internal double? LaunchRangeAt;
        internal bool SummaryLogged;
    }

    private sealed record FinaleOriginal(short Hull, string Name, IReadOnlyList<ExpandedPartData>? Parts,
        short? Weapon, short? Launcher, double ReadyTime, short Wingman, short? Target, int RouteIndex, string Role);

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
            var observer = bots.First(b => b.Side == side);
            var loadout = SelectFinaleLoadout(observer, core.StationTypes[target.StationTypeId], stations);
            plans[side] = new(target.StationId, target.SideId, target.ClusterId, target.StationTypeId, loadout);
        }
        var run = new FinaleRun(settings, Now(), bots, plans);
        run.Maturity = plans.Keys.Order().Select(side => new ShowcaseFinale.Maturity(side,
            bots.Count(b => b.Side == side), bots.Count(b => b.Side == side && b.CompletedPodCycles > 0),
            bots.Where(b => b.Side == side).Sum(b => b.Kills - run.InitialKills[b]))).ToArray();
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

    private static ShowcaseFinale.StrikeLoadout? SelectFinaleLoadout(ActiveBot bot,
        DataStationTypeIGC target, IReadOnlyList<DataStationIGC>? observedStations = null)
    {
        var core = Catalog.RepresentativeCore();
        var techs = TechTreeBitMask.Empty;
        bool knownTech = false;
        short? dock = null;
        if (bot.Cs is { } cs)
        lock (cs.Gate)
        {
            var developments = cs.World.DevelopmentTechsFor(bot.Side);
            knownTech = developments is not null;
            techs = developments ?? TechTreeBitMask.Empty;
            observedStations ??= cs.World.Stations.Values.ToArray();
            if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.StationId >= 0) dock = me.StationId;
        }
        int developmentBits = techs.PopCount;
        var owned = (observedStations ?? []).Where(s => s.SideId == bot.Side
            && core.StationTypes.ContainsKey(s.StationTypeId)).ToArray();
        // Side.Techs also includes the effects of all owned buildings.
        foreach (var station in owned)
            techs = techs.Or(TechTreeBitMask.FromBytes(core.StationTypes[station.StationTypeId].TtbmEffects));
        var shop = owned.Where(s => dock is null || s.StationId == dock)
            .OrderByDescending(s => s.StationId == dock)
            .ThenByDescending(s => core.StationTypes[s.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart))
            .ThenBy(s => s.StationId).Select(s => (DataStationTypeIGC?)core.StationTypes[s.StationTypeId]).FirstOrDefault();
        if (shop is null) return Failed("no shop: no owned station type resolves for the current dock/home");
        short civ = core.Civilizations.Keys.Min();
        if (bot.Cs is { } client)
            lock (client.Gate)
            {
                var observedCiv = client.World.SideRoster?.Sides.FirstOrDefault(s => s.SideId == bot.Side).CivId;
                if (observedCiv is { } id && core.Civilizations.ContainsKey(id)) civ = id;
            }
        var selected = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber,
            techs, shop.Value.StationTypeId, target)?.Strike;
        if (selected is not null) return selected;
        if (ResearchFinale(bot))
        {
            long budget = long.MaxValue;
            if (bot.Cs is { } peer) lock (peer.Gate) budget = peer.World.MyMoney ?? 0;
            var fallback = TechLoadoutSelector.SelectAvailableStrike(core, civ, techs, shop.Value.StationTypeId, target, budget);
            if (fallback is not null)
            {
                Console.WriteLine($"[finale] side {bot.Side}, {bot.Name}: {fallback.Reason}");
                return fallback.Strike;
            }
        }
        var bombers = core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
            && Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber).ToArray();
        var buyable = bombers.Where(h => TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, shop.Value.TtbmLocal, techs)).ToArray();
        return Failed(bombers.Length == 0 ? "no player-buyable bomber hull in catalogue"
            : buyable.Length == 0 ? "no bomber hull passes the side + station tech gate"
            : buyable.All(h => SuccessorUpgrade.HullIsObsolete(core, h, shop.Value.TtbmLocal, techs)) ? "all buyable bomber hulls are obsolete"
            : "no fitting, buyable, nonobsolete fixed gun can damage the target within the bomber's energy/ammo budget");

        ShowcaseFinale.StrikeLoadout? Failed(string reason)
        {
            var stationTypes = string.Join(",", (observedStations ?? []).Where(s => s.SideId == bot.Side)
                .Select(s => $"{s.StationId}:{s.StationTypeId}"));
            Console.WriteLine($"[finale] side {bot.Side}, {bot.Name}: no buyable bomber strike loadout: {reason}; "
                + $"developmentTechs={(knownTech ? developmentBits.ToString() : "unknown (no CREATE_BUCKETS/SIDE_TECH_CHANGE)")}, "
                + $"sideTechBits={techs.PopCount}, shopType={shop?.StationTypeId}, dock={dock}, "
                + $"ownedStations(id:type)=[{stationTypes}], targetType={target.StationTypeId}; "
                + (ResearchFinale(bot) ? "research finale uses available combat loadout and bounded server deadline" : "keeping existing bomber loadout"));
            return null;
        }
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
                    usingExistingBomberLoadouts = kv.Value.Loadout is null,
                    reservedBombers = kv.Value.BomberBudget, escortBudget = kv.Value.EscortBudget,
                    requestedBombers = config.Bombers,
                    bomberShortfall = run.Sides.Contains(kv.Key) ? Math.Max(0, config.Bombers - kv.Value.BomberBudget) : 0,
                    assignedBombers = run.Bots.Count(b => b.Side == kv.Key && b.FinaleBomberActive),
                    armedBombers = run.Bots.Count(b => b.Side == kv.Key && b.FinaleBomberActive && b.FinaleGrantVerified),
                    escorts = run.Bots.Count(b => b.Side == kv.Key && b.FinaleEscort),
                    targetKilledAt = Relative(kv.Value.KilledAt),
                    timeToKillSeconds = kv.Value.KilledAt is { } kill && run.StrikeStartedAt is { } start ? (double?)(kill - start) : null,
                    grants = run.Bots.Where(b => b.Side == kv.Key && b.FinaleBomberActive)
                        .Select(b => new { b.Name, requestedHull = b.HullId, requestedHullName = b.HullName,
                            requestedParts = b.Parts, grantedHull = b.Granted.HullId,
                            verified = b.FinaleGrantVerified, isPod = b.IsPod, docked = b.PodDocked, warning = b.GrantWarning }).ToArray(),
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

    // The old run remains available as the finished game's report. Re-arm only
    // after EVERY participating bot has reset and resolved the fresh world; old
    // station ids and the old maturity/kill window must never drive a new strike.
    private void RearmRotatedFinales()
    {
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        foreach (var run in runs)
        {
            lock (run.Sync)
                if (!run.Rotated || run.NextRunCreated) continue;
            var bots = run.Bots.Where(IsParticipating).ToArray();
            if (bots.Length == 0 || bots.Any(b => !b.FinaleResetObserved || b.ObjectivesPending
                || !ReferenceEquals(b.Finale, run) || b.LifeGeneration <= run.Generations[b])) continue;
            DataStationIGC[] stations = [];
            bool ready = true;
            foreach (var bot in bots)
            {
                if (bot.Cs is not { } cs) { ready = false; break; }
                lock (cs.Gate)
                {
                    if (!cs.World.WorldReady || cs.World.GameOverResult is not null
                        || cs.World.Mission?.Stage != MissionStageMessage.StageStarted)
                    { ready = false; break; }
                    if (stations.Length == 0) stations = cs.World.Stations.Values.ToArray();
                }
            }
            if (!ready || bots.Any(b => !stations.Any(s => s.SideId >= 0 && s.SideId != b.Side))) continue;
            RegisterFinale(run.Settings, bots.Select(b => b.Name).ToArray(), stations);
            lock (run.Sync) run.NextRunCreated = true;
            Console.WriteLine($"[finale] re-armed for next mission with {bots.Length} bots and fresh station objectives");
        }
    }

    private void UpdateFinales(double now)
    {
        RearmRotatedFinales();
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
                run.Bots.Where(b => b.Side == side).Sum(b => b.Kills - run.InitialKills[b]))).ToArray();
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
                // P778a — the push goal: the first live strike bomber inside its
                // own launch range of the target logs the per-strike summary once.
                if (bot.FinaleBomberActive && !bot.IsPod && run.TriggeredAt is not null
                    && run.StrikeStartedAt is { } strike
                    && run.Plans.TryGetValue(bot.Side, out var summaryPlan) && !summaryPlan.SummaryLogged)
                {
                    bool reached = false;
                    lock (cs.Gate)
                        if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && !me.IsDead && me.StationId < 0
                            && me.SectorId == summaryPlan.Sector && me.Latest is { } mePose
                            && cs.World.Stations.TryGetValue(summaryPlan.TargetId, out var st)
                            && st.Position is { Length: 3 } tp
                            && FinaleLaunchRange(bot) is > 0 and var range
                            && (new Vector3(tp[0], tp[1], tp[2]) - mePose.Position).Length() <= range)
                        { summaryPlan.LaunchRangeAt ??= now; reached = true; }
                    if (reached)
                    {
                        summaryPlan.SummaryLogged = true;
                        Console.WriteLine($"[push] strike side={bot.Side} contacts={run.PushContacts} "
                            + $"pushDamage={run.PushTotalDamage:F1} timeToLaunchRange={now - strike:F1}s bomberSurvived=true");
                    }
                }
            }
            if (run.TriggeredAt is not null || run.GameOverAt is not null) continue;
            var sides = ShowcaseFinale.TriggerSides(run.Settings, now - run.Created, run.Maturity, run.Kills.Count);
            if (sides.Length == 0) continue;
            run.Sides = sides;
            run.TriggeredAt = now;
            // Arm the server's simulation-time bound once at the actual maturity trigger.
            // This remains effective if no legal starting weapon can damage a station.
            var researchPeer = bots.FirstOrDefault(b => sides.Contains(b.Side) && ResearchFinale(b));
            if (researchPeer?.Cs is { } peer)
                lock (peer.Gate)
                    if (Guid.TryParseExact(peer.World.ResearchBriefing?.Epoch, "N", out var epoch))
                        peer.SendShowcaseFinale(epoch, researchPeer.Side);
            foreach (short side in sides)
            {
                var bombers = bots.Where(b => b.Side == side && !IsSupport(b) && (IsFinaleBomber(b) || ResearchFinale(b)))
                    .OrderByDescending(b => ResearchFinale(b) && b.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(b => b.ShipId).Take(run.Settings.Bombers).ToArray();
                foreach (var bot in bombers) bot.FinaleBomberReserved = true;
                var plan = run.Plans[side];
                plan.BomberBudget = bombers.Length;
                plan.EscortBudget = bombers.Length == 0 ? 0 : run.Settings.EscortBudget(bots.Count(b => b.Side == side && IsFinaleFighter(b)));
            }
        }
    }

    private static bool ResearchFinale(ActiveBot bot)
    {
        if (bot.Cs is not { } cs) return false;
        lock (cs.Gate) return cs.World.ResearchBriefing?.TechMode == "research";
    }

    private static bool IsFinaleBomber(ActiveBot bot) =>
        Catalog.RepresentativeCore().HullTypes.TryGetValue(bot.HullId, out var hull)
        && Showcase.ClassifyHull(hull) == Showcase.HullRole.Bomber;

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
                bot.LauncherPartId, bot.MissileReadyTime, bot.WingmanBehaviour, bot.TargetSector, bot.RouteIndex, bot.Role);
            if (bot.FinaleBomberReserved)
            {
                var loadout = SelectFinaleLoadout(bot, Catalog.RepresentativeCore().StationTypes[plan.TargetTypeId]);
                plan.Loadout = loadout;
                if (loadout is not null)
                {
                    bot.HullId = loadout.HullId; bot.HullName = loadout.HullName; bot.Parts = loadout.Parts;
                    bot.WeaponPartId = Catalog.FirstWeaponPartId(loadout.Parts);
                    bot.LauncherPartId = loadout.Parts.Where(p => Catalog.RepresentativeCore().Launchers.TryGetValue(p.PartId, out var l)
                        && Catalog.RepresentativeCore().MissileTypes.ContainsKey(l.ExpendabletypeId)).Select(p => (short?)p.PartId).FirstOrDefault();
                    bot.MissileReadyTime = bot.LauncherPartId is { } id ? Catalog.LauncherReadyTime(id) : 0;
                    bot.MineDispenserPartId = null;
                    bot.WingmanBehaviour |= Showcase.WbbmUseMissiles;
                }
                bot.Role = "bomber";
                bot.FinaleBomberActive = true;
            }
            EndDefense(bot);
            bot.TargetSector = plan.Sector;
            bot.RoutePath = null; bot.RouteIndex = -1;
            AssignResearchSiegeRoute(bot, run, plan);
        }
    }

    private static void AssignResearchSiegeRoute(ActiveBot bot, FinaleRun run, FinalePlan plan)
    {
        // Research finale duty policy: retain the showcase's existing entry-wing
        // routing instead of funneling every relaunch down one shortest path.
        // Purchases and native movement/warp rules are unchanged.
        if (ResearchFinale(bot) && bot.Cs is { } cs)
            lock (cs.Gate)
                if (cs.World.Graph is { } graph && cs.World.Ships.TryGetValue(bot.ShipId, out var own))
                {
                    var routes = graph.FindDistinctEntryRoutes(own.SectorId, plan.Sector, 3);
                    if (routes.Count > 0)
                    {
                        int route = Enumerable.Range(0, routes.Count)
                            .OrderBy(i => run.Bots.Count(b => b != bot && b.Side == bot.Side
                                && b.FinaleBomberActive && b.RoutePath is { } path && path.SequenceEqual(routes[i])))
                            .ThenBy(i => i).First();
                        bot.RouteIndex = route;
                        bot.RoutePath = routes[route];
                    }
                }
    }

    private static void RestoreFinaleForNextMission(ActiveBot bot, double now, GameOver? over)
    {
        if (bot.Finale is not { } run) return;
        lock (run.Sync)
        {
            run.Rotated = true;
            if (over is not null)
            {
                run.GameOverAt ??= now;
                run.Winner = over.SideWinner;
                run.EndReason = over.GameoverMessage;
            }
            if (bot.FinaleOriginal is { } original)
            {
                bot.HullId = original.Hull; bot.HullName = original.Name; bot.Parts = original.Parts;
                bot.WeaponPartId = original.Weapon; bot.LauncherPartId = original.Launcher;
                bot.MissileReadyTime = original.ReadyTime; bot.WingmanBehaviour = original.Wingman;
                bot.TargetSector = original.Target;
                bot.RouteIndex = original.RouteIndex; bot.Role = original.Role;
                bot.MineDispenserPartId = original.Parts is { } parts ? FindMineDispenser(parts) : null;
            }
            bot.FinaleOriginal = null;
            bot.FinaleBomberReserved = bot.FinaleBomberActive = bot.FinaleEscort = false;
            bot.FinaleRefitting = bot.FinaleRefitLaunchPending = bot.FinaleKeepsPayday = false;
            bot.FinaleMissileLaunchBase = 0;
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
        return TryEscortAnchor(bot, out anchor, out sector, out _);
    }

    private static bool TryEscortAnchor(ActiveBot bot, out Vector3 anchor, out short sector,
        out ShipEntity? leaderShip)
    {
        anchor = default; sector = -1; leaderShip = null;
        if (bot.Finale is not { } run || bot.Cs is not { } cs) return false;
        // P778a — "assigned to that bomber": the wing's job is THE strike bomber
        // en route to the target, so a live bomber already in the plan's target
        // sector outranks a nearer one still at home (P745's nearest-live pick).
        // A bomber in another sector is out of POSE view (IsGone) but its
        // strategic SHIP_STATUS sector still reaches the escort's own world, so
        // it stays an anchor candidate: the escort transits to it and re-acquires
        // the pose on arrival. Only a DEAD bomber stops being a leader.
        short planSector = run.Plans.TryGetValue(bot.Side, out var plan) ? plan.Sector : (short)-1;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } pose) return false;
            var candidates = run.Bots.Where(b => b.Side == bot.Side && b.FinaleBomberActive && !b.IsPod)
                .Select(b => cs.World.Ships.TryGetValue(b.ShipId, out var ship) ? ship : null)
                .Where(s => s is { IsDead: false, Latest: not null })
                .OrderBy(s => s!.SectorId == planSector ? 0 : 1)
                .ThenBy(s => s!.SectorId == own.SectorId ? 0 : 1)
                .ThenBy(s => (s!.Latest!.Position - pose.Position).LengthSquared()).ThenBy(s => s!.ShipId).FirstOrDefault();
            if (candidates?.Latest is not { } leader) return false;
            anchor = leader.Position; sector = candidates.SectorId;
            leaderShip = candidates;
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
        short ownSector = CsSector(cs);
        // P778a — ride with the strike: a committed strike owns the wing's
        // destination, not any single bomber. This MUST run before the anchor
        // pick: TryEscortAnchor needs a LIVE bomber, and in the P778a natural
        // runs every strike ship was podded ~20-40 s after launch ("no buyable
        // bomber strike loadout" fallback fighters), so the anchor went null,
        // the escorts loitered in their HOME sector for the whole strike, and
        // the push path never ran (0 [push] seek / 0 [push] contact; every hold
        // reason wrong-sector-*). Keep transiting to the plan sector while the
        // side's target station lives; the next bomber relaunch then finds its
        // wing already on station. (The previous form also re-gated every tick
        // on a live bomber, so a bomber dying mid-transit aborted the whole
        // multi-hop transit and sent the escort home.)
        if (run.Plans.TryGetValue(bot.Side, out var strikePlan) && ownSector != strikePlan.Sector
            && run.StrikeStartedAt is not null && strikePlan.KilledAt is null)
        {
            List<short>? path;
            lock (cs.Gate) path = cs.World.Graph?.FindShortestPath(ownSector, strikePlan.Sector);
            if (path is not { Count: >= 2 } || !TransitWarp(cs, bot, ownSector, path[1])) LoiterCombat(bot, now);
            return true;
        }
        if (!TryEscortAnchor(bot, out var anchor, out short sector, out var leaderShip))
        {
            // Wait at the launch point for a newly purchased bomber rather than
            // racing an unarmed escort alone to the enemy garrison.
            bot.TargetId = null;
            LoiterCombat(bot, now);
            return true;
        }
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
        if (target is { } id) { HoldPush(bot, now, "engaging"); EngageShipAt(bot, cs, id, now, 0, 0); }
        else if (!TryPushFinaleBomber(bot, cs, run, leaderShip, now))
            CombatFlyTo(bot, anchor, 15, Math.Max(100, run.Settings.EscortRadius * .3f), 0);
        return true;
    }

    /// <summary>P778a — "push the bomb": with no threat to answer (EscortTarget
    /// above keeps priority), up to two in-sector escorts nose into the back of
    /// their own finale bomber to shove it toward the target station faster —
    /// the real-Allegiance escort tactic. The physics is native (friendly rams
    /// DO damage, shipIGC.cpp:1166-1168), so the closing speed is deliberately
    /// small and nanite scouts (P778a bomber-first repair) pay the hull back.
    /// Stop conditions: bomber dead/podded (the anchor pick), in its own launch
    /// range of the target (the push goal is reached), under fire, or not
    /// astern of it on the bomber→target line.</summary>
    private bool TryPushFinaleBomber(ActiveBot bot, ConnectionSession cs, FinaleRun run,
        ShipEntity? leaderShip, double now)
    {
        if (leaderShip?.Latest is not { } bomberPose || leaderShip.IsNotVisible) { HoldPush(bot, now, "no-leader"); return false; }
        if (!run.Plans.TryGetValue(bot.Side, out var plan) || run.StrikeStartedAt is null) return false;
        short sector = CsSector(cs);
        if (sector != plan.Sector) { HoldPush(bot, now, $"wrong-sector-{sector}"); return false; }   // push only in the target sector
        Vector3 target;
        lock (cs.Gate)
        {
            if (!cs.World.Stations.TryGetValue(plan.TargetId, out var station) || station.SideId == bot.Side
                || station.ClusterId != sector || station.Position is not { Length: 3 } sp) return false;
            target = new(sp[0], sp[1], sp[2]);
        }
        var bomberBot = run.Bots.FirstOrDefault(b => b.ShipId == leaderShip.ShipId
            && b.Side == bot.Side && b.FinaleBomberActive && !b.IsPod);
        if (bomberBot is null) return false;
        // Stop pushing once the bomber can loose its own ordnance at the target.
        float launchRange = FinaleLaunchRange(bomberBot);
        if (launchRange > 0 && (target - bomberPose.Position).Length() <= launchRange)
        { HoldPush(bot, now, "in-launch-range"); return false; }

        bool threatened = bomberBot.RecentAttackers.Any(a => now - a.Value < ThreatWindowSeconds);
        if (threatened) HoldPush(bot, now, "bomber-threatened");
        List<short> inSectorEscorts;
        lock (cs.Gate)
            inSectorEscorts = run.Bots.Where(b => b.Side == bot.Side && b.FinaleEscort && !b.IsPod)
                .Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId))
                .Where(s => s is { IsDead: false, IsGone: false, IsNotVisible: false, Latest: not null, StationId: < 0 }
                            && s.SectorId == sector).Select(s => s!.ShipId).ToList();
        if (!ShowcaseFinale.IsBomberPusher(inSectorEscorts, threatened, bot.ShipId))
        { HoldPush(bot, now, "not-selected"); return false; }

        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return false;
        var push = ShowcaseFinale.BomberPush(pos, bomberPose.Position, target,
            HullRadius(leaderShip.HullId), HullRadius(ActualHullId(bot)));
        if (push is not { } pushPlan)
        {
            // P778a — not astern (typically the contact bounced the pusher past
            // the shell). Station-keep BEHIND the bomber on the target line: the
            // ordinary escort flight goes to the bomber's position, which parks
            // the pusher alongside/ahead and it never pushes again (the staged
            // live run saw one contact then no more for the rest of the run).
            var toTarget = target - bomberPose.Position;
            float lineLength = toTarget.Length();
            if (lineLength <= 1e-3f) { HoldPush(bot, now, "not-astern"); return false; }
            var line = toTarget / lineLength;
            float shell = HullRadius(leaderShip.HullId) + HullRadius(ActualHullId(bot)) + ShowcaseFinale.PusherMargin;
            bot.TargetId = null;
            CombatFlyTo(bot, bomberPose.Position - line * (shell + 40f), 15f, 200f, 0);
            HoldPush(bot, now, "not-astern");
            return true;
        }
        bot.TargetId = null;

        // Direct steering: the planned contact IS the goal, so the shared dodge
        // law must not veto it (same exemption pattern as the HabmBoard station
        // approach in TryDodgeGoal — a deliberate boarding/push is not an
        // unplanned impact). Proportional closing law: catch up astern, then
        // taper to the chosen contact speed so each contact's damage stays small.
        var toContact = pushPlan.ContactPoint - pos;
        float gap = toContact.Length();
        var dir = gap > 1e-3f ? toContact / gap : pushPlan.Direction;
        var (yaw, pitch, _) = SteerTowardDirection(orient, dir);
        float closing = (TryCsFreshPose(cs, bot.ShipId, out _, out var myVel, out _) ? myVel : Vector3.Zero)
            * dir - bomberPose.Velocity * dir;
        // Chase at up to PushChaseSpeed, taper to the design closing at the shell
        // (a 90 u/s arrival was measured slamming the bomber off-axis and SLOWING
        // it, 80→50 u/s, in the staged live run; see PushChaseSpeed).
        float desired = Math.Clamp((gap - pushPlan.ContactDistance) * .4f,
            pushPlan.ClosingSpeed, ShowcaseFinale.PushChaseSpeed);
        float throttle = Math.Clamp((desired - closing) * .4f, -1f, 1f);
        cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), 0);
        SeekPush(bot, now, gap, closing);
        TrackPushContact(bot, cs, run, bomberBot, leaderShip, bomberPose, pushPlan, dir, closing, now);
        return true;
    }

    /// <summary>P778a — bounded hold telemetry: why an in-sector escort with a
    /// live wing leader is NOT pushing right now (threat priority, not among
    /// the two selected pushers, not astern of the bomber, engaging, or the
    /// wrong sector). Suppressed repeats are COUNTED and reported with the next
    /// logged line; the line itself is rate-limited to one per escort per 10 s.</summary>
    private void HoldPush(ActiveBot bot, double now, string reason)
    {
        if (now - bot.PushLastHoldAt < 10)
        {
            bot.PushHoldCount++;
            bot.PushLastHoldReason = reason;
            return;
        }
        string counted = bot.PushLastHoldReason == reason && bot.PushHoldCount > 0
            ? $" x{bot.PushHoldCount + 1}" : "";
        bot.PushHoldCount = 0;
        bot.PushLastHoldReason = reason;
        bot.PushLastHoldAt = now;
        Console.WriteLine($"[push] hold ship={bot.ShipId} reason={reason}{counted}");
    }

    /// <summary>P778a — bounded seek telemetry (one line per pushing escort per
    /// 2 s): the gap to the contact shell and the measured closing speed, so a
    /// rig run shows the push actually closing, not just being attempted.</summary>
    private void SeekPush(ActiveBot bot, double now, float gap, float closing)
    {
        if (now - bot.PushLastSeekAt < 2) return;
        bot.PushLastSeekAt = now;
        Console.WriteLine($"[push] seek ship={bot.ShipId} gap={gap:F0} closing={closing:F1}");
    }

    /// <summary>The range at which the bomber's own ordnance reaches the target:
    /// its launcher's missile flight envelope (the accelerating-missile integral
    /// <c>v0·L + a·L²/2</c> over the round's lifespan), falling back to the lead
    /// gun's range; 0 when unarmed (never stop pushing a target the bomber
    /// cannot yet engage).</summary>
    private static float FinaleLaunchRange(ActiveBot bomber)
    {
        var core = Catalog.RepresentativeCore();
        if (bomber.LauncherPartId is { } id && core.Launchers.TryGetValue(id, out var launcher)
            && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile) && missile.Lifespan > 0)
            return missile.InitialSpeed * missile.Lifespan + .5f * missile.Acceleration * missile.Lifespan * missile.Lifespan;
        return bomber.WeaponPartId is { } gun && TryWeaponBallistics(gun, out _, out float range, out _, out _)
            ? range : 0;
    }

    /// <summary>P778a — bounded per-contact telemetry. A contact is a REAL
    /// collision: the pair appears in <see cref="ClientWorld.TakeCollisionEvents"/>
    /// (the client half of the same <c>COLLISION_EVENT(216)</c> the sector
    /// broadcast emits for the native <c>COLLISION_EVENT</c>). The first P778a
    /// cut inferred a bounce from the measured closing speed collapsing; the
    /// staged live run showed that heuristic firing on ordinary throttle changes
    /// (a logged contact with <c>bomberAlong 80.1→80.1</c> and no collision at
    /// all) while MISSING the real collision that followed. Each logged contact
    /// carries the closing speed, the bomber's speed along the target line
    /// before → after, the measured hull damage to both parties (hull-fraction
    /// drop × hull hit points; shield-absorbed portions are not visible on the
    /// wire) and the native-formula expectation. Rate-limited to one contact per
    /// pusher per second and only for fresh (≤ 0.5 s old) pose samples.</summary>
    private void TrackPushContact(ActiveBot bot, ConnectionSession cs, FinaleRun run, ActiveBot bomberBot,
        ShipEntity bomberShip, ServerShipUpdate bomberPose, ShowcaseFinale.BomberPushPlan plan,
        Vector3 dir, float closing, double now)
    {
        // The telemetry's "speed along the target line" is the bomber's velocity
        // projected on the BOMBER→TARGET line (plan.Direction), not on the
        // escort-relative steering vector — the first live staged run logged
        // -45.9 u/s for a bomber doing 80 u/s toward the station because the
        // steering dir flipped once the pusher overshot the contact point.
        float bomberAlong = bomberPose.Velocity * plan.Direction;
        float ownHull = 1f, bomberHull = 1f;
        bool collided = false;
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(bot.ShipId, out var me)) ownHull = me.Latest?.HullFraction ?? 1f;
            bomberHull = bomberShip.Latest?.HullFraction ?? 1f;
            // Headless bot world: draining is safe (the audio pass is the only
            // other consumer and a bot has none).
            foreach (var hit in cs.World.TakeCollisionEvents())
                if ((hit.Party1Id == bot.ShipId && hit.Party2Id == bomberShip.ShipId)
                    || (hit.Party2Id == bot.ShipId && hit.Party1Id == bomberShip.ShipId))
                { collided = true; break; }
        }
        bool fresh = now - bot.PushPrevAt is > 0 and <= 0.5;
        bool bounce = collided && fresh
            && (bot.PushLastContactAt is not { } last || now - last >= 1.0);
        if (bounce)
        {
            bot.PushLastContactAt = now;
            bot.PushContacts++;
            var core = Catalog.RepresentativeCore();
            float bomberHp = core.HullTypes.TryGetValue(bomberShip.HullId, out var bh) ? MathF.Max(1f, bh.HitPoints) : 1f;
            float pusherHp = core.HullTypes.TryGetValue(ActualHullId(bot), out var ph) ? MathF.Max(1f, ph.HitPoints) : 1f;
            float bomberDamage = MathF.Max(0f, (bot.PushPrevBomberHull - bomberHull) * bomberHp);
            float pusherDamage = MathF.Max(0f, (bot.PushPrevOwnHull - ownHull) * pusherHp);
            var (expectedBomber, expectedPusher) = ShowcaseFinale.PushContactDamage(
                core.HullTypes.TryGetValue(bomberShip.HullId, out var bm) ? bm.Mass : 1f,
                core.HullTypes.TryGetValue(ActualHullId(bot), out var pm) ? pm.Mass : 1f,
                plan.ClosingSpeed, pusherHp, bomberHp);
            lock (run.Sync)
            {
                run.PushContacts++;
                run.PushTotalDamage += bomberDamage + pusherDamage;
            }
            Console.WriteLine($"[push] contact pusher={bot.ShipId} bomber={bomberShip.ShipId} closing={bot.PushPrevClosing:F1} "
                + $"bomberAlong={bot.PushPrevBomberAlong:F1}->{bomberAlong:F1} bomberDamage={bomberDamage:F2} pusherDamage={pusherDamage:F2} "
                + $"expected={expectedBomber:F2}/{expectedPusher:F2}");
        }
        bot.PushPrevAt = now;
        bot.PushPrevClosing = closing;
        bot.PushPrevBomberAlong = bomberAlong;
        bot.PushPrevOwnHull = ownHull;
        bot.PushPrevBomberHull = bomberHull;
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
