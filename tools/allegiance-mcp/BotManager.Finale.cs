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
        internal double LastReadinessLog = double.NegativeInfinity;
        internal double LastRearmBlockedLog = double.NegativeInfinity;
        internal double? RotatedAt;
        internal bool Rotated, NextRunCreated, GraceLogged, FogGraceLogged;
        // P831 — per-bot "blocked since" so the bounded grace excludes only a bot
        // that has been stuck continuously, and never one that just relaunched.
        internal readonly Dictionary<ActiveBot, double> RearmBlockedSince = new();
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
        // P896 — the target is no longer fixed at registration: a side that has
        // observed no enemy station yet is armed with TargetId = -1 ("no target
        // yet") and re-picks the moment one of ITS OWN worlds reports one. Target
        // fields stay mutable for exactly that re-pick.
        internal const short NoTarget = -1;
        internal short TargetId = targetId, TargetSide = targetSide, Sector = sector;
        internal short TargetTypeId = targetTypeId;
        internal ShowcaseFinale.StrikeLoadout? Loadout = loadout;
        // P896 — this side really observed its target (so its later disappearance
        // is evidence); whether the no-target line was logged; wave sizing inputs.
        internal bool ObservedTarget = targetId >= 0, NoTargetLogged;
        // P896 — some world of this side still held the target at this second's
        // resolve pass. Exports reach a side's bots at different moments, so one
        // world lacking a station proves nothing while a teammate's still holds it.
        internal bool TargetHeldBySide;
        internal string? KilledEvidence;
        internal int WaveLosses, DefenderCount;
        // P896 — the survival part of this wave's requirement (0 with the margin off).
        internal int SurvivalBombers;
        internal double? KilledAt;
        internal int BomberBudget, EscortBudget;
        // P778a — first time a live strike bomber of this side is inside its own
        // launch range of the target (the push goal), and whether the strike
        // summary has been logged (launch-range reach or bomber death, one each).
        internal double? LaunchRangeAt;
        internal bool SummaryLogged;
        // P801 — massed strike state. Bombers gather at a standoff point in the
        // target sector and release on one go time once the computed volley is
        // assembled; relaunches wait for the NEXT wave instead of attacking alone.
        internal int RequiredBombers = 1;
        internal int AssignedLoneWolves;
        internal double? StagingSince;
        internal double? GoTime;
        internal readonly HashSet<short> GoMembers = new();
        internal bool MassLogged, OutcomeLogged;
        internal double LastSampleAt = double.NegativeInfinity;
        internal double LastStagingLogAt = double.NegativeInfinity;
        // P896 — bounded cadence of the "no station-damaging loadout" hold line.
        internal double LastNoStrikeLogAt = double.NegativeInfinity;
        internal float HullAtGo = -1, ShieldAtGo = -1, HullNow = -1, ShieldNow = -1;
        internal int HitsInWindow;
    }

    private sealed record FinaleOriginal(short Hull, string Name, IReadOnlyList<ExpandedPartData>? Parts,
        short? Weapon, short? Launcher, double ReadyTime, short Wingman, short? Target, int RouteIndex, string Role);

    // P797 follow-up — with native map discovery a side does not know where the
    // enemy base is until one of its ships has seen it (fsmission.cpp:4169-4250),
    // so the finale is registered once every side has observed an enemy station,
    // instead of failing the whole spawn when nothing is known yet.
    private List<(ShowcaseFinale.Settings Settings, string[] Names)>? _pendingFinales;

    public object ConfigureFinale(ShowcaseFinale.Settings settings, IReadOnlyCollection<string> names)
    {
        // A selected objective match is decided by objective play, including when the
        // skirmish driver uses its ordinary showcase fleet preset.
        if (Probe?.World.Mission is { } mission && (mission.Goals.IsFlagsGame || mission.Goals.IsArtifactsGame || mission.Goals.IsConquestGame || mission.Goals.IsDeathMatchGame)) settings = settings with { AfterMinutes = 0 };
        settings.Validate();
        if (!settings.Enabled) return new { enabled = false };
        var stations = StationsSeenByAnEnemy(names);
        if (stations is null)
        {
            lock (_gate) (_pendingFinales ??= []).Add((settings, names.ToArray()));
            Console.WriteLine($"[finale] armed for {names.Count} bots; waiting until every side has observed an enemy station (map discovery)");
            return new { enabled = true, pending = "waiting until every side has observed an enemy station" };
        }
        return RegisterFinale(settings, names, stations);
    }

    /// <summary>Stations an opposing side has actually seen (each side's own bot
    /// worlds), or null while some side of <paramref name="names"/> has not yet
    /// seen any enemy station. Never hands a side knowledge it does not have.</summary>
    private DataStationIGC[]? StationsSeenByAnEnemy(IReadOnlyCollection<string> names)
    {
        ActiveBot[] bots;
        lock (_gate) bots = _bots.Where(b => names.Contains(b.Name)).ToArray();
        var seen = new Dictionary<int, DataStationIGC>();
        var sides = bots.Select(b => b.Side).Distinct().ToArray();
        if (sides.Length == 0) return null;
        var sidesWithTarget = new HashSet<short>();
        foreach (var bot in bots)
        {
            if (bot.Cs is not { } cs) continue;
            lock (cs.Gate)
                foreach (var st in cs.World.Stations.Values)
                    if (st.SideId >= 0 && st.SideId != bot.Side)
                    {
                        seen[st.StationId] = st;
                        sidesWithTarget.Add(bot.Side);
                    }
        }
        return sides.All(sidesWithTarget.Contains) ? seen.Values.ToArray() : null;
    }

    /// <summary>A finale is armed but still waiting for one of these bots' side to
    /// observe an enemy station (<see cref="ConfigureFinale"/>).</summary>
    private bool FinalePendingFor(IEnumerable<ActiveBot> group)
    {
        lock (_gate)
        {
            if (_pendingFinales is not { Count: > 0 } pending) return false;
            foreach (var bot in group)
                foreach (var p in pending)
                    if (p.Names.Contains(bot.Name)) return true;
        }
        return false;
    }

    /// <summary>P837 — a finished match's finale is rotated and still waiting for
    /// its re-arm (<see cref="RearmRotatedFinales"/>), which needs every
    /// participating side to have observed an enemy station in the NEW mission.
    /// A small side that the clash assignment leaves alone (below
    /// <c>MinFleetForExploration</c>) then has no objective at all, never flies and
    /// never obtains that sighting — the re-arm can only wait out P831's whole
    /// grace. Treating this exactly like a PENDING finale (the P798b small-fleet
    /// recon exception) hands the side its recon/clash duty again after a
    /// rotation. The fresh objectives are unchanged: only what this side has seen
    /// is ever used.</summary>
    private bool FinaleAwaitingRearm(IEnumerable<ActiveBot> group)
    {
        ActiveBot[] members = group as ActiveBot[] ?? group.ToArray();
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        foreach (var run in runs)
        {
            bool waiting;
            lock (run.Sync) waiting = run.Rotated && !run.NextRunCreated;
            if (waiting && members.Any(run.Bots.Contains)) return true;
        }
        return false;
    }

    private void RegisterPendingFinales()
    {
        (ShowcaseFinale.Settings Settings, string[] Names)[] pending;
        lock (_gate) pending = _pendingFinales?.ToArray() ?? [];
        foreach (var p in pending)
        {
            if (StationsSeenByAnEnemy(p.Names) is not { } stations) continue;
            lock (_gate) _pendingFinales?.Remove(p);
            RegisterFinale(p.Settings, p.Names, stations);
            Console.WriteLine($"[finale] registered: every side has observed an enemy station");
        }
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
            var sideBots = bots.Where(b => b.Side == side).ToArray();
            // P896 — P797 honesty: a side only ever receives a target from its OWN
            // worlds. `stations` is the fleet-wide union used for discovery
            // bookkeeping, and handing it to every side is what let the P831 grace
            // give side A a station only side B's world held — which the strike-end
            // check then read as "destroyed" (false station-killed).
            var observed = ObservedFinaleStations(side, sideBots, stations);
            var target = PickFinaleTarget(core, side, observed);
            if (target is not { } home)
            {
                // The grace can still arm a side that has seen nothing: that is a
                // legal "no target yet" state, not a target it has not observed.
                plans[side] = new(FinalePlan.NoTarget, FinalePlan.NoTarget, FinalePlan.NoTarget, FinalePlan.NoTarget, null);
                Console.WriteLine($"[finale] side {side}: no enemy station observed yet — armed with NO target; "
                    + "it will pick one as soon as its own worlds report one (P896)");
                continue;
            }
            var loadout = SelectFinaleLoadout(sideBots[0], core.StationTypes[home.StationTypeId], stations);
            plans[side] = new(home.StationId, home.SideId, home.ClusterId, home.StationTypeId, loadout);
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

    /// <summary>P896 — the enemy stations THIS side's own bot worlds hold.
    /// <paramref name="fallback"/> is used only when no bot of the side has a
    /// readable world at all (a fixture or driver that supplies observations); it
    /// never tops up a readable but blind side, which is exactly the P797 honesty
    /// rule the finale needs.</summary>
    private static DataStationIGC[] ObservedFinaleStations(short side, ActiveBot[] bots,
        IReadOnlyList<DataStationIGC> fallback)
    {
        var seen = new Dictionary<short, DataStationIGC>();
        bool readable = false;
        foreach (var bot in bots)
        {
            if (bot.Cs is not { } cs) continue;
            lock (cs.Gate)
            {
                if (!cs.World.WorldReady) continue;
                readable = true;
                foreach (var station in cs.World.Stations.Values)
                    if (station.SideId >= 0 && station.SideId != side) seen[station.StationId] = station;
            }
        }
        return readable ? seen.Values.ToArray()
            : fallback.Where(s => s.SideId >= 0 && s.SideId != side).ToArray();
    }

    /// <summary>P896 — the finale's target preference (the P831 order): a
    /// restart-capable enemy station first, then the lowest station id.</summary>
    private static DataStationIGC? PickFinaleTarget(StaticCore core, short side,
        IReadOnlyList<DataStationIGC> observed) =>
        observed.Where(s => s.SideId >= 0 && s.SideId != side && core.StationTypes.ContainsKey(s.StationTypeId))
            .OrderByDescending(s => core.StationTypes[s.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart))
            .ThenBy(s => s.StationId)
            .Select(s => (DataStationIGC?)s)
            .FirstOrDefault();

    /// <summary>P896 — resolve a side's "no target yet" state once one of its OWN
    /// worlds reports an enemy station, and record that the side really observed
    /// the target it holds (the only way that target's later disappearance is
    /// evidence of anything). Called once a second under the run lock, before the
    /// strike tick and before the trigger, so a side armed blind never keeps a
    /// target it has not seen and picks one up as soon as it has.</summary>
    private void ResolveFinaleTargets(FinaleRun run, ActiveBot[] bots)
    {
        var core = Catalog.RepresentativeCore();
        foreach (var (side, plan) in run.Plans.OrderBy(kv => kv.Key))
        {
            var sideBots = run.Bots.Where(b => b.Side == side).ToArray();
            var observed = ObservedFinaleStations(side, sideBots, []);
            if (plan.TargetId != FinalePlan.NoTarget)
            {
                plan.TargetHeldBySide = observed.Any(s => s.StationId == plan.TargetId);
                if (plan.TargetHeldBySide) plan.ObservedTarget = true;
                continue;
            }
            if (PickFinaleTarget(core, side, observed) is not { } home)
            {
                if (!plan.NoTargetLogged)
                {
                    plan.NoTargetLogged = true;
                    Console.WriteLine($"[finale] side={side} waiting for an observed enemy station before it can strike (P896)");
                }
                continue;
            }
            plan.TargetId = home.StationId;
            plan.TargetSide = home.SideId;
            plan.Sector = home.ClusterId;
            plan.TargetTypeId = home.StationTypeId;
            plan.ObservedTarget = plan.TargetHeldBySide = true;
            // No station list: `observed` holds enemy stations only, and the loadout
            // needs the side's OWN stations for its shop and tech.
            plan.Loadout = SelectFinaleLoadout(sideBots[0], core.StationTypes[home.StationTypeId]);
            Console.WriteLine($"[finale] side={side} observed enemy station#{home.StationId} in sector {home.ClusterId}: target assigned (P896)");
            // A match whose deadline was already armed (the P831 fog grace) now gets
            // its strike, armed exactly as the ordinary trigger would.
            if (run.TriggeredAt is not null && run.Sides.Contains(side)) ArmFinaleSide(run, side, bots);
        }
    }

    /// <summary>P896 — commit one side to the strike: reserve its bomber wing and
    /// reset this strike's mass state. Extracted from the trigger so a side whose
    /// target is observed only AFTER the trigger arms identically.</summary>
    private void ArmFinaleSide(FinaleRun run, short side, ActiveBot[] bots)
    {
        var plan = run.Plans[side];
        var bombers = bots.Where(b => b.Side == side && !IsSupport(b) && (IsFinaleBomber(b) || ResearchFinale(b)))
            .OrderByDescending(b => ResearchFinale(b) && b.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase))
            .ThenBy(b => b.ShipId).Take(run.Settings.Bombers).ToArray();
        foreach (var bot in bombers) bot.FinaleBomberReserved = true;
        plan.BomberBudget = bombers.Length;
        plan.EscortBudget = bombers.Length == 0 ? 0 : run.Settings.EscortBudget(bots.Count(b => b.Side == side && IsFinaleFighter(b)));
        // P801 — fresh mass for this strike. Every relaunch joins the wave
        // forming now; none of them flies at the station alone.
        plan.StagingSince = null;   // starts at the first arrival (FinaleStrikeMassTick)
        plan.GoTime = null;
        plan.GoMembers.Clear();
        plan.MassLogged = plan.OutcomeLogged = false;
        plan.HitsInWindow = 0;
        plan.WaveLosses = 0;        // P896 — wave sizing measures THIS strike's losses
        plan.HullAtGo = plan.ShieldAtGo = plan.HullNow = plan.ShieldNow = -1;
    }

    public object[] FinaleStatus()
    {
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        return runs.Select(r => FinaleView(r, Now())).ToArray();
    }

    private static ShowcaseFinale.StrikeLoadout? SelectFinaleLoadout(ActiveBot bot,
        DataStationTypeIGC target, IReadOnlyList<DataStationIGC>? observedStations = null,
        bool preferTurretCarrier = false)
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
            techs, shop.Value.StationTypeId, target, strikePref: preferTurretCarrier
                ? TechLoadoutSelector.StrikeHullPref.TurretCarrier : TechLoadoutSelector.StrikeHullPref.Best)?.Strike;
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
                + (ResearchFinale(bot) ? ConquestGame(bot.Cs!) ? "conquest awaits a legal strike loadout" : "research finale uses available combat loadout and bounded server deadline" : "keeping existing bomber loadout"));
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
    //
    // P831 — "EVERY" is bounded in TIME, never forever. The re-arm is the only
    // thing that arms the next match's finale, and the finale is the showcase's
    // only deadline: one class of stuck bot (rig batches 34/36: requested-Bomber
    // bots on a P821 fallback hull) therefore left a 200v200 match without a
    // deadline for hours. Bots still blocked when the bounded grace expires are
    // EXCLUDED (named in the log) and the re-arm proceeds with the rest. The grace
    // never relaxes any other rule: a bot inside it still blocks, and the fresh
    // station/objective derivation is unchanged.
    internal static readonly double RearmGraceSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P831_REARM_GRACE"), out double grace)
            && grace >= 0 ? grace : 300;

    /// <summary>P837 — the grace is a RIG-scale number and the deadline release it
    /// bounds must scale with the fleet. Measured on master: after a rotation a
    /// one-bomber-per-side finale under native fog needed ~150-210 s just to fly to
    /// and observe the other side's base, so P831's 300 s release arrived AFTER a
    /// 240 s window and the next match never got a deadline (live test P682b
    /// `match 2 automatic start and fresh finale timed out`). The grace is the rig
    /// figure scaled by the participating bots, floored at 120 s (a real pod
    /// relaunch plus one sector of flight) and never above the rig figure, so the
    /// 200v200 rig keeps 300 s and a two-bot finale gets its deadline in time.</summary>
    internal const int RigScaleRearmBots = 48;
    internal const double MinRearmGraceSeconds = 120;
    internal static double RearmGraceSecondsFor(int participating) =>
        Math.Clamp(RearmGraceSeconds * Math.Max(1, participating) / RigScaleRearmBots,
            Math.Min(MinRearmGraceSeconds, RearmGraceSeconds), RearmGraceSeconds);

    private void RearmRotatedFinales(double now)
    {
        FinaleRun[] runs;
        lock (_gate) runs = _finales?.ToArray() ?? [];
        foreach (var run in runs)
        {
            lock (run.Sync)
                if (!run.Rotated || run.NextRunCreated) continue;
            var bots = run.Bots.Where(IsParticipating).ToArray();
            if (bots.Length == 0)
            {
                LogRearmBlocked(run, bots, now);
                continue;
            }
            double graceSeconds = RearmGraceSecondsFor(bots.Length);
            bool grace = run.RotatedAt is { } rotatedAt && now - rotatedAt >= graceSeconds;
            // The grace is PER BOT and counts from when it was FIRST seen blocked: a
            // bot that only becomes blocked as the grace expires (a normal relaunch
            // in flight) is never excluded for it.
            var blocked = BlocksRearmSince(run, bots, now);
            if (blocked.Count > 0)
            {
                var stuck = blocked.Where(e => now - e.Since >= graceSeconds).Select(e => e.Bot).ToArray();
                if (!grace || stuck.Length == 0)
                {
                    LogRearmBlocked(run, bots, now);
                    continue;
                }
                var kept = bots.Where(b => !stuck.Contains(b)).ToArray();
                LogRearmGrace(run, now, stuck, kept.Length);
                bots = kept;
                if (bots.Length == 0)
                {
                    LogRearmBlocked(run, bots, now, "every participating bot is still blocked past the P831 grace");
                    continue;
                }
            }
            // Fresh station objectives from the participants' OWN worlds (never one
            // bot's window for everybody): the union of what the fleet has observed.
            var stations = new Dictionary<short, DataStationIGC>();
            string? gate = null;
            foreach (var bot in bots)
            {
                if (bot.Cs is not { } cs) { gate = "world not ready/started"; break; }
                lock (cs.Gate)
                {
                    if (!cs.World.WorldReady || cs.World.GameOverResult is not null
                        || cs.World.Mission?.Stage != MissionStageMessage.StageStarted)
                    { gate = "world not ready/started"; break; }
                    foreach (var station in cs.World.Stations.Values) stations[station.StationId] = station;
                }
            }
            if (gate is null)
            {
                // P797 honesty: a side may only receive a target IT has observed, so
                // every side must have seen an enemy station in one of its own worlds.
                var blind = bots.GroupBy(b => b.Side).Where(g => !SideObservedEnemyStation(g.ToArray()))
                    .Select(g => g.Key).Order().ToArray();
                if (blind.Length > 0)
                {
                    gate = $"no enemy station observed by side(s) [{string.Join(",", blind)}]";
                    if (grace)
                    {
                        // The deadline must exist; the strike itself still only targets
                        // stations somebody observed (logged above by P827/P831).
                        LogRearmGrace(run, now, [], bots.Length, gate);
                        gate = null;
                    }
                }
            }
            if (gate is not null)
            {
                LogRearmBlocked(run, bots, now, gate);
                continue;
            }
            RegisterFinale(run.Settings, bots.Select(b => b.Name).ToArray(), stations.Values.ToArray());
            lock (run.Sync) run.NextRunCreated = true;
            Console.WriteLine($"[finale] re-armed for next mission with {bots.Length} bots and fresh station objectives");
        }
    }

    private static bool BlocksRearm(ActiveBot bot, FinaleRun run) => !bot.FinaleResetObserved
        || bot.ObjectivesPending || !ReferenceEquals(bot.Finale, run)
        || bot.LifeGeneration <= run.Generations[bot];

    /// <summary>P831 — the bots blocked right now, stamping when each was FIRST seen
    /// blocked in this rotated run (the per-bot grace clock).</summary>
    private static List<(ActiveBot Bot, double Since)> BlocksRearmSince(FinaleRun run, ActiveBot[] bots, double now)
    {
        var blocked = new List<(ActiveBot, double)>();
        lock (run.Sync)
        {
            var since = run.RearmBlockedSince;
            foreach (var bot in bots)
            {
                if (!BlocksRearm(bot, run)) { since.Remove(bot); continue; }
                if (!since.TryGetValue(bot, out double first)) since[bot] = first = now;
                blocked.Add((bot, first));
            }
            foreach (var bot in since.Keys.Where(b => !bots.Contains(b)).ToArray()) since.Remove(bot);
        }
        return blocked;
    }

    private static bool SideObservedEnemyStation(ActiveBot[] bots)
    {
        foreach (var bot in bots)
        {
            if (bot.Cs is not { } cs) continue;
            lock (cs.Gate)
                if (cs.World.Stations.Values.Any(s => s.SideId >= 0 && s.SideId != bot.Side)) return true;
        }
        return false;
    }

    /// <summary>P831 — the bounded-grace line: name the bots excluded from the new
    /// run, once per run, so an excluded bot is never silent.</summary>
    private static void LogRearmGrace(FinaleRun run, double now, ActiveBot[] stuck, int kept, string? gate = null)
    {
        lock (run.Sync)
        {
            // One line EACH per run for the two kinds of relaxation: the excluded
            // bots and the fog-gate release.
            if (gate is null ? run.GraceLogged : run.FogGraceLogged) return;
            if (gate is null) run.GraceLogged = true; else run.FogGraceLogged = true;
        }
        string elapsed = run.RotatedAt is { } rotatedAt ? $"{now - rotatedAt:F0}s" : "n/a";
        if (gate is not null)
        {
            Console.WriteLine($"[finale] re-arm grace ({elapsed} past the rotation): {gate}; arming the next match's finale anyway "
                + $"with {kept} bots — the showcase must always have a deadline");
            return;
        }
        var reasons = new (string Reason, Func<ActiveBot, bool> Blocks)[]
        {
            ("reset-not-observed", b => !b.FinaleResetObserved),
            ("objectives-pending", b => b.ObjectivesPending),
            ("other-run", b => !ReferenceEquals(b.Finale, run)),
            ("same-generation", b => b.LifeGeneration <= run.Generations[b]),
        };
        var parts = reasons.Select(r => (r.Reason, Bots: stuck.Where(r.Blocks).ToArray())).Where(r => r.Bots.Length > 0)
            .Select(r => $"{r.Reason}={r.Bots.Length} [{string.Join(",", r.Bots.Take(8).Select(b => Reason(b, run)))}]");
        Console.WriteLine($"[finale] re-arm grace ({elapsed} past the rotation): excluding {stuck.Length} still-stuck bot(s) "
            + $"from the next finale run ({string.Join("; ", parts)}); re-arming with {kept} bots");

        static string Reason(ActiveBot b, FinaleRun run) => $"{b.Name}/side{b.Side}/{b.Role}"
            + (b.IsPod ? "/pod" : "")
            + (b.TargetSector is { } t ? $"/target{t}" : "")
            + (b.ObjectivesPending ? "/pending" : "")
            + (!b.FinaleResetObserved ? "/noreset" : "")
            + (b.LifeGeneration <= run.Generations[b] ? "/samegen" : "");
    }

    // P827 — the re-arm waits for EVERY participating bot; one stuck bot held
    // batch 34's second match without a deadline for two hours, silently. Name
    // the blocking condition and bots once a minute while it waits.
    private static void LogRearmBlocked(FinaleRun run, ActiveBot[] bots, double now, string? gate = null)
    {
        lock (run.Sync)
        {
            if (now - run.LastRearmBlockedLog < 60) return;
            run.LastRearmBlockedLog = now;
        }
        if (gate is not null)
        {
            Console.WriteLine($"[finale] re-arm blocked: {gate} ({bots.Length} participating bots)");
            return;
        }
        var reasons = new (string Reason, Func<ActiveBot, bool> Blocks)[]
        {
            ("reset-not-observed", b => !b.FinaleResetObserved),
            ("objectives-pending", b => b.ObjectivesPending),
            ("other-run", b => !ReferenceEquals(b.Finale, run)),
            ("same-generation", b => b.LifeGeneration <= run.Generations[b]),
        };
        var parts = reasons.Select(r => (r.Reason, Bots: bots.Where(r.Blocks).ToArray())).Where(r => r.Bots.Length > 0)
            .Select(r => $"{r.Reason}={r.Bots.Length} [{string.Join(",", r.Bots.Take(5).Select(b =>
                $"{b.Name}/side{b.Side}/{b.Role}{(b.IsPod ? "/pod" : "")}{(b.TargetSector is { } t ? $"/target{t}" : "")}"))}]");
        Console.WriteLine($"[finale] re-arm blocked: {bots.Length} participating bots; {string.Join("; ", parts)}");
    }

    private void UpdateFinales(double now)
    {
        RegisterPendingFinales();
        RearmRotatedFinales(now);
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
            // P896 — a side armed with no target (P831 fog grace) picks one up from its
            // OWN observations as soon as it has any; a side that already holds a
            // target records that it really observed it.
            ResolveFinaleTargets(run, bots);
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
                        && plan.KilledAt is null && plan.TargetId != FinalePlan.NoTarget
                        && cs.World.WorldReady && !cs.World.Stations.ContainsKey(plan.TargetId))
                    {
                        // P896 — a station is DEAD only on real evidence: the
                        // WHOLE-MISSION STATION_DESTROYED(59) this world received, or a
                        // station this side really observed and then lost. Absence alone
                        // means "not observed" under P797 fog, which is not death — the
                        // b59 M5 run reported `outcome=station-killed` for both sides
                        // right after the trigger while neither garrison had been hit.
                        plan.KilledEvidence = cs.World.StationWasDestroyed(plan.TargetId) ? "destroyed"
                            : plan.ObservedTarget && !plan.TargetHeldBySide ? "observed-lost" : null;
                        if (plan.KilledEvidence is not null) plan.KilledAt = now;
                    }
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
            // P801 — the finale feed: compute the volley requirement, watch the
            // group assemble at the standoff point, and set one shared go time.
            FinaleStrikeMassTick(run, bots, now);
            if (run.TriggeredAt is not null || run.GameOverAt is not null) continue;
            var sides = ShowcaseFinale.TriggerSides(run.Settings, now - run.Created, run.Maturity, run.Kills.Count);
            if (sides.Length == 0)
            {
                // P798b — bounded readiness telemetry: why the finale has not
                // triggered yet (age, per-side combat bots, relaunch share and
                // kills inside the window). One line per 30 s.
                if (now - run.Created >= 30 && now - run.LastReadinessLog >= 30)
                {
                    run.LastReadinessLog = now;
                    Console.WriteLine($"[finale] waiting t={now - run.Created:F0}s: "
                        + string.Join("; ", run.Maturity.Select(m =>
                            $"side {m.Side} combat={m.CombatBots} relaunched={m.Relaunched} share={m.RelaunchShare:F2} kills={m.Kills}"))
                        + $" recentKills={run.Kills.Count} need age>={run.Settings.AfterMinutes * 60:F0}s share>={run.Settings.MinRelaunchShare:F2} kills>={run.Settings.MinKillsLastFiveMinutes}");
                }
                continue;
            }
            run.Sides = sides;
            run.TriggeredAt = now;
            Console.WriteLine($"[finale] trigger sides=[{string.Join(",", sides)}] at t={now - run.Created:F0}s: "
                + string.Join("; ", run.Maturity.Select(m =>
                    $"side {m.Side} combat={m.CombatBots} relaunched={m.Relaunched} share={m.RelaunchShare:F2} kills={m.Kills}"))
                + $" recentKills={run.Kills.Count}");
            // Arm the server's simulation-time bound once at the actual maturity trigger.
            // This remains effective if no legal starting weapon can damage a station.
            var researchPeer = bots.FirstOrDefault(b => sides.Contains(b.Side) && ResearchFinale(b));
            if (researchPeer?.Cs is { } peer)
                lock (peer.Gate)
                    if (Guid.TryParseExact(peer.World.ResearchBriefing?.Epoch, "N", out var epoch))
                        peer.SendShowcaseFinale(epoch, researchPeer.Side);
            foreach (short side in sides)
            {
                // P896 — a side that has not observed an enemy station gets no strike:
                // there is nothing it is allowed to target. The deadline is already
                // armed above, so the match still ends; the strike follows the sighting.
                if (run.Plans[side].TargetId == FinalePlan.NoTarget)
                {
                    Console.WriteLine($"[finale] side={side} finale armed but no enemy station observed yet — "
                        + "no strike committed (P896)");
                    continue;
                }
                ArmFinaleSide(run, side, bots);
            }
        }
    }

    private static bool ResearchFinale(ActiveBot bot)
    {
        if (bot.Cs is not { } cs) return false;
        // Conquest uses legal strike refits in either tech mode, without a deadline.
        lock (cs.Gate) return cs.World.ResearchBriefing?.TechMode == "research" || ConquestGame(cs);
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
            if (plan.TargetId == FinalePlan.NoTarget) return;   // P896 — nothing legal to strike yet
            if (!bot.FinaleBomberReserved && !bot.FinaleEscort
                && IsFinaleFighter(bot) && run.Bots.Count(b => b.Side == bot.Side && b.FinaleEscort) < plan.EscortBudget)
                bot.FinaleEscort = true;
            if (!bot.FinaleBomberReserved && !bot.FinaleEscort) return;
            bot.FinaleOriginal ??= new(bot.HullId, bot.HullName, bot.Parts, bot.WeaponPartId,
                bot.LauncherPartId, bot.MissileReadyTime, bot.WingmanBehaviour, bot.TargetSector, bot.RouteIndex, bot.Role);
            if (bot.FinaleBomberReserved)
            {
                // P814b scope 1 — split the strike BEFORE picking the loadout so
                // the P801 lone wolves keep the cloak (Best) hull and the visible
                // element flies a TURRET carrier (a seat a bot gunner can board).
                // The first MaxLoneWolves cloak-capable bombers are the wolves.
                bool wolfBudget = LiveLoneWolves(run, bot.Side) < ResearchTactics.MaxLoneWolves;
                var loadout = SelectFinaleLoadout(bot, Catalog.RepresentativeCore().StationTypes[plan.TargetTypeId],
                    preferTurretCarrier: !wolfBudget && GunnerPolicy.Effective.Enable);
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
                AssignFinaleLoneWolf(bot, run, bot.HullId, bot.Parts);
            }
            EndDefense(bot);
            bot.TargetSector = plan.Sector;
            bot.RoutePath = null; bot.RouteIndex = -1;
            AssignResearchSiegeRoute(bot, run, plan);
        }
    }

    /// <summary>P801 follow-up (rig 221 batch 26 staging telemetry: armed finale bombers spread over three
    /// entry routes, 3-4 per side parked in detour sectors, at most 2 ever reached the target sector, 4-5 of
    /// 12 were pods). The visible element must arrive together, so every non-wolf bomber takes the SHORTEST
    /// entry route (routes are ranked by hop count, ClusterGraph.FindDistinctEntryRoutes); only a cloaked lone
    /// wolf takes the least-used ALTERNATIVE entry (its own lane, P801 3b), falling back to the shortest when
    /// there is no alternative.</summary>
    internal static int FinaleRouteIndex(bool loneWolf, IReadOnlyList<int> usagePerRoute)
    {
        if (!loneWolf || usagePerRoute.Count <= 1) return 0;
        int best = 1;
        for (int i = 2; i < usagePerRoute.Count; i++)
            if (usagePerRoute[i] < usagePerRoute[best]) best = i;
        return best;
    }

    private static void AssignResearchSiegeRoute(ActiveBot bot, FinaleRun run, FinalePlan plan)
    {
        if (!ResearchFinale(bot)) return;
        // P819 — station duty supersedes the previous autonomous rally. Merely
        // clearing OrderedTargetId leaves OrderedCommand's CidGoto(Warp) alive:
        // DriveCommanderOrder runs before finale transit and holds at that aleph
        // forever. This is showcase policy, not a native order/movement change.
        // Future orders are still accepted normally by HandleOrders.
        if (bot.OrderedCommand is { } old)
            Console.WriteLine($"[strike-route] side={bot.Side} ship={bot.ShipId} supersede={old.CommandId}:{old.TargetType}#{old.TargetId} target=station#{plan.TargetId} sector={plan.Sector}");
        bot.OrderedCommand = null;
        bot.OrderedWarpOrigin = null;
        bot.OrderedTargetId = bot.TargetId = null;
        // Keep the visible element on the shortest entry; wolves use another
        // lane. All route geometry still comes from this client's observations.
        if (bot.Cs is { } cs)
            lock (cs.Gate)
                if (cs.World.Graph is { } graph && cs.World.Ships.TryGetValue(bot.ShipId, out var own))
                {
                    var routes = graph.FindDistinctEntryRoutes(own.SectorId, plan.Sector, 3);
                    if (routes.Count > 0)
                    {
                        var usage = Enumerable.Range(0, routes.Count)
                            .Select(i => run.Bots.Count(b => b != bot && b.Side == bot.Side
                                && b.FinaleBomberActive && b.RoutePath is { } path && path.SequenceEqual(routes[i])))
                            .ToArray();
                        int route = FinaleRouteIndex(bot.FinaleLoneWolf, usage);
                        bot.RouteIndex = route;
                        bot.RoutePath = routes[route];
                    }
                }
    }

    private static void RestoreFinaleForNextMission(ActiveBot bot, double now, GameOver? over)
    {
        if (bot.Finale is not { } run) return;
        // P832g: conquest uses an unregistered strike context, not a re-arming
        // timed finale. Drop its old station plan when migrating to a new world.
        bool conquestStrike = bot.ConquestStrike;
        lock (run.Sync)
        {
            run.Rotated = true;
            run.RotatedAt ??= now;
            if (over is not null)
            {
                run.GameOverAt ??= now;
                run.Winner = over.SideWinner;
                run.EndReason = over.GameoverMessage;
            }
            RestoreFinaleLoadout(bot);
            bot.FinaleBomberReserved = bot.FinaleBomberActive = bot.FinaleEscort = false;
            bot.FinaleLoneWolf = bot.FinaleReleaseLogged = bot.FinaleStrikeSpent = false;
            bot.FinaleReleaseAt = null;
            bot.FinaleRefitting = bot.FinaleRefitLaunchPending = bot.FinaleKeepsPayday = false;
            bot.FinaleMissileLaunchBase = 0;
            bot.FinaleProtectedSince = null;
            bot.FinaleGrantVerified = false;
            bot.CompletedPodCycles = 0;
            bot.FinaleResetObserved = true;
            bot.ConquestStrike = false;
        }
        if (conquestStrike) bot.Finale = null;
    }

    private static void RestoreFinaleLoadout(ActiveBot bot, bool currentHardware = false)
    {
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
        // A conquest detour can end during the same life after a real refit.
        // Restore its ordinary role/route, but steer/fire using the hardware
        // actually granted, until the next ordinary purchase picks a loadout.
        if (currentHardware && bot.Granted.Parts is { } mounted
            && Catalog.RepresentativeCore().HullTypes.TryGetValue(bot.Granted.HullId, out var hull))
        {
            var core = Catalog.RepresentativeCore();
            bot.HullId = hull.HullId; bot.HullName = hull.Name; bot.Parts = mounted;
            bot.WeaponPartId = Catalog.FirstWeaponPartId(mounted);
            bot.LauncherPartId = mounted.Where(p => core.Launchers.TryGetValue(p.PartId, out var launcher)
                    && core.MissileTypes.ContainsKey(launcher.ExpendabletypeId)).Select(p => (short?)p.PartId).FirstOrDefault();
            bot.MissileReadyTime = bot.LauncherPartId is { } id ? Catalog.LauncherReadyTime(id) : 0;
            bot.MineDispenserPartId = FindMineDispenser(mounted);
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
        if (run.Plans.TryGetValue(bot.Side, out var strikePlan) && strikePlan.TargetId != FinalePlan.NoTarget
            && ownSector != strikePlan.Sector
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
