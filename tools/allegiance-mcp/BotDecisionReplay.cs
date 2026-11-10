using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P987 — feed one recorded input snapshot back through the SAME pure decision
/// core used by the live bot. No match order, socket, peer or wall clock is
/// replayed. Every branch returns the exact JSON shape emitted by its recorder.
/// </summary>
internal static class BotDecisionReplay
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal static JsonNode Replay(string family, string decision, JsonObject inputs)
    {
        string text = inputs.ToJsonString(BotDecisionRecorder.Compact);
        T In<T>() => JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new InvalidDataException($"Null {typeof(T).Name} input for {family}.{decision}");
        object? output = (family, decision) switch
        {
            ("CombatTarget", "Select") => Combat(In<CombatInput>()),
            ("Loadout", "Select") => Loadout(In<LoadoutInput>()),
            ("Loadout", "SelectSupport") => SupportLoadout(In<SupportLoadoutInput>()),
            ("Medevac", "PickMedevacScouts") => MedevacScouts(In<MedevacInput>()),
            ("Medevac", "DropGate") => MedevacDrop(In<DropInput>()),
            ("Defense", "Budget") => DefenseBudget(In<DefenseBudgetInput>()),
            ("Defense", "Assignment") => DefenseAssignment(In<DefenseAssignmentInput>()),
            ("Route", "Enumeration") => Routes(In<RouteInput>()),
            ("Route", "LeastLoaded") => LeastLoaded(In<LeastLoadedInput>()),
            ("Route", "NextHop") => NextHop(In<NextHopInput>()),
            ("Command", "ScenarioTarget") => ScenarioTarget(In<ScenarioTargetInput>()),
            ("Command", "DefenseTarget") => CommanderDefense(In<CommanderDefenseInput>()),
            ("Support", "Slot") => SupportSlot(In<SupportSlotInput>()),
            ("Support", "Phase") => SupportPhase(In<SupportPhaseInput>()),
            ("Support", "Destination") => SupportDestination(In<SupportDestinationInput>()),
            ("Support", "RepairHold") => RepairHold(In<RepairHoldInput>()),
            ("ResearchCommander", "ConstructionRock") => ConstructionRock(In<ConstructionRockInput>()),
            ("ResearchCommander", "ConstructorOrder") => ConstructorOrder(In<ConstructorOrderInput>()),
            ("ResearchCommander", "Donation") => Donation(In<DonationInput>()),
            ("Finale", "TriggerSides") => FinaleTrigger(In<FinaleTriggerInput>()),
            ("Finale", "EscortTarget") => EscortTarget(In<EscortInput>()),
            ("Finale", "VolleyRequirement") => Volley(In<VolleyInput>()),
            ("Finale", "ShouldRelease") => Release(In<ReleaseInput>()),
            ("Finale", "WingTarget") => WingTarget(In<WingTargetInput>()),
            ("Finale", "CommitReason") => CommitReason(In<CommitInput>()),
            ("MultiSideEnemy", "Choose") => MultiSide(In<MultiSideInput>()),
            ("Exploration", "Frontiers") => Frontiers(In<FrontiersInput>()),
            ("Exploration", "Assign") => ExplorationAssign(In<ExplorationAssignInput>()),
            ("Exploration", "ReconScout") => ReconScout(In<ReconInput>()),
            ("Exploration", "Clash") => Clash(In<ClashInput>()),
            ("ResearchTactics", "StagingReady") => StagingReady(In<StagingInput>()),
            ("ResearchTactics", "StrikeStation") => StrikeStation(In<StrikeStationInput>()),
            ("ResearchTactics", "Rally") => Rally(In<RallyInput>()),
            ("Lifecycle", "PodTransition") => PodTransition(In<PodInput>()),
            ("Lifecycle", "MissionRefresh") => MissionRefresh(In<MissionRefreshInput>()),
            _ => throw new NotSupportedException($"No replay adapter for recorded decision {family}.{decision}"),
        };
        return BotDecisionRecorder.ToJson(output);
    }

    private static object Combat(CombatInput i)
    {
        var candidates = i.Candidates.Select(c => new BotTargetCandidate(c.Id, default, c.Class, c.IsThreat)
        {
            Distance = c.Distance,
        }).ToArray();
        var choice = CombatTargetSelector.Select(candidates,
            new BotSelectionContext(i.SelfShipId, i.MyCluster, i.TeammatesInCluster, i.EngagerCounts));
        return new { choice.TargetId, choice.Hold, choice.ChosenClass };
    }

    private static object LoadoutResult(TechLoadoutSelector.Selection? result) => new
    {
        hullId = result?.HullId,
        hullName = result?.HullName,
        price = result?.Price,
        partCount = result?.Parts.Count,
        parts = result?.Parts.Select(p => new { p.PartId, p.Amount, p.MountId }).ToArray(),
        reason = result?.Reason,
    };

    private static object Loadout(LoadoutInput i)
    {
        var core = Catalog.RepresentativeCore();
        DataStationTypeIGC? target = i.TargetStationTypeId is { } id ? core.StationTypes[id] : null;
        return LoadoutResult(TechLoadoutSelector.Select(core, i.CivId, i.Role,
            TechTreeBitMask.FromBytes(Convert.FromBase64String(i.TechMask)), i.DockTypeId, target, i.Budget,
            i.PreferMines, i.RescueProbe, i.StrikePref, i.Boarding, i.AlephFieldMines));
    }

    private static object SupportLoadout(SupportLoadoutInput i) => LoadoutResult(TechLoadoutSelector.SelectSupport(
        Catalog.RepresentativeCore(), i.CivId, TechTreeBitMask.FromBytes(Convert.FromBase64String(i.TechMask)),
        i.DockTypeId, i.Budget));

    private static object MedevacScouts(MedevacInput i)
    {
        var chosen = BotManager.PickMedevacScouts(i.Candidates);
        return new { chosen = chosen.OrderBy(id => id).ToArray() };
    }

    private static object MedevacDrop(DropInput i) => new
    {
        drop = BotManager.ShouldDropMedevacProbe(i.OwnStationInSector, i.OwnPodsInSector,
            i.RecentOwnEjects, i.OwnRescueProbeNearby, i.Now, i.LastRescueProbeAt, i.LoadTime),
    };

    private static object DefenseBudget(DefenseBudgetInput i) => new
    {
        budget = BotManager.DefenseBudgetFor(i.Population, i.AlreadyAssigned, i.Cap, i.Share),
    };

    private static object DefenseAssignment(DefenseAssignmentInput i)
    {
        int budget = BotManager.DefenseBudgetFor(i.Population, i.AlreadyAssigned, i.Cap, i.Share);
        return new { budget, selected = BotManager.RankDefenseAssignments(i.Candidates, budget) };
    }

    private static object Routes(RouteInput i)
    {
        var warps = i.Warps.ToDictionary(w => w.WarpId, w => new DataWarpIGC(w.WarpId, w.DestinationId,
            100, "", "", "", [0f, 0f, 0f], [0f, 0f, 0f], [0f, 0f, 0f], 0f, w.ClusterId));
        var routes = BotManager.RouteEnumeration(warps, i.From, i.To, i.MaxRoutes);
        return new { routes = routes.Select(r => r.ToArray()).ToArray() };
    }

    private static object LeastLoaded(LeastLoadedInput i) => new { pick = BotManager.PickLeastLoadedRoute(i.Counts) };

    private static object NextHop(NextHopInput i) => new { next = BotManager.NextHopInPath(i.Path, i.Current) };

    private static object ScenarioTarget(ScenarioTargetInput i) => new
    {
        target = BotManager.ResolveScenarioTarget(i.Role, i.Rendezvous, i.EnemyHome),
    };

    private static object CommanderDefense(CommanderDefenseInput i)
    {
        var selected = BotManager.PickCommanderDefenseTarget(i.Enemies.Select(e => (e.Id, Vec(e.Position))), Vec(i.Charge), i.Current);
        return new { selected };
    }

    private static object SupportSlot(SupportSlotInput i) => new { support = BotManager.SupportSlot(i.Ordinal, i.Count, i.Share) };

    private static object SupportPhase(SupportPhaseInput i)
    {
        var phase = BotManager.SupportPhaseFrom(i.FinaleTriggered, i.FinaleSector, i.StrikeWaveActive,
            i.StrikeWaveSector, i.FighterRendezvous.Select(c => (c.Sector, c.Count)).ToArray(),
            i.FightersPresent.Select(c => (c.Sector, c.Count)).ToArray());
        return new { kind = phase.Kind, anchorSector = phase.AnchorSector };
    }

    private static object SupportDestination(SupportDestinationInput i)
    {
        var destination = BotManager.PickSupportDestination(i.DamagedStationSector, i.Phase, i.FallbackSector);
        return new { sector = destination.Sector, phaseDriven = destination.PhaseDriven };
    }

    private static object RepairHold(RepairHoldInput i) => new
    {
        decision = BotManager.EvaluateRepairHold(i.Now, i.StartedAt, i.BestFraction, i.ObservedFraction,
            i.ObservedAgeSeconds, i.LastImproveAt, i.MaxAgeSeconds, i.NoImproveSeconds, i.LeashSeconds),
    };

    private static object ConstructionRock(ConstructionRockInput i) => new
    {
        rockId = ResearchCommander.PickConstructionRock(i.Rocks, i.AsteroidClass, i.Reserved)?.Id,
    };

    private static object ConstructorOrder(ConstructorOrderInput i) => new
    {
        kind = ResearchCommander.ConstructorOrderKind(i.RockSector, i.ShipSector),
    };

    private static object Donation(DonationInput i) => new { amount = ResearchCommander.DonationAmount(i.Wallet, i.BucketPrice, i.Funded) };

    private static object FinaleTrigger(FinaleTriggerInput i)
    {
        var settings = new ShowcaseFinale.Settings(AfterMinutes: i.AfterMinutes, Side: i.SideSelection,
            MinRelaunchShare: i.MinRelaunchShare, MinKillsLastFiveMinutes: i.MinKillsLastFiveMinutes);
        return new { selected = ShowcaseFinale.TriggerSides(settings, i.ElapsedSeconds, i.Sides, i.RecentKills) };
    }

    private static object EscortTarget(EscortInput i)
    {
        var enemies = i.Enemies.Select(e => new ShowcaseFinale.EscortEnemy(e.Id, Vec(e.Position), Vec(e.Velocity), e.AttackingBomber));
        return new { target = ShowcaseFinale.EscortTarget(Vec(i.Escort), Vec(i.Bomber), enemies, i.Radius, i.Leash) };
    }

    private static object Volley(VolleyInput i) => StrikeVolley.Compute(i.Station, i.Loadouts, i.HullsAvailable,
        i.Window, i.SurvivalMargin, i.Losses, i.Defenders, i.LandingShare);

    private static object Release(ReleaseInput i) => new
    {
        release = StrikeVolley.ShouldRelease(i.Now, i.GoTime, i.InCommittedWave, i.AlreadyReleased),
    };

    private static object WingTarget(WingTargetInput i) => new { target = BotManager.FinaleWingTarget(i.Required, i.Reserved, i.Cap) };

    private static object CommitReason(CommitInput i) => new
    {
        reason = BotManager.FinaleCommitReason(i.Assembled, i.Required, i.StagingSince, i.Now,
            i.DeadlineAt, i.Window, i.Lead),
    };

    private static object MultiSide(MultiSideInput i) => new { selected = MultiSideEnemy.ChooseRanked(i.Own, i.Candidates) };

    private static object Frontiers(FrontiersInput i)
    {
        var graph = Graph(i.Clusters, i.Warps);
        var lanes = ExplorationPlanner.FindFrontiers(graph, i.KnownSectors,
            i.WarpKnowledge.ToDictionary(w => w.WarpId), i.OwnHome, i.DoneLanes?.ToHashSet(), i.SweptSectors?.ToHashSet());
        return new { lanes };
    }

    private static object ExplorationAssign(ExplorationAssignInput i)
    {
        var hops = i.Hops.GroupBy(h => (h.From, h.To)).ToDictionary(g => g.Key, g => g.First().Hops);
        var current = i.Current?.ToDictionary(p => p.Key, p => p.Value);
        var result = ExplorationPlanner.Assign(i.Lanes, i.Scouts,
            (from, to) => hops.GetValueOrDefault((from, to), -1), current);
        return new { assignments = result.OrderBy(kv => kv.Key).Select(kv => new { scoutId = kv.Key, lane = kv.Value }).ToArray() };
    }

    private static object ReconScout(ReconInput i)
    {
        var hops = i.Hops.GroupBy(h => h.Sector).ToDictionary(g => g.Key, g => g.First().Hops);
        return new { selected = ExplorationPlanner.PickReconScout(i.Scouts,
            (from, _) => hops.GetValueOrDefault(from, -1), i.Target) };
    }

    private static object Clash(ClashInput i)
    {
        var choice = ClashScoring.Choose(Graph(i.Clusters, i.Warps), i.OwnHome, i.EnemyHome,
            i.KnownSectors, i.Rocks, i.OwnMiners, i.EnemyMiners, i.EnemyStations, i.TopologyFallback);
        return new { choice };
    }

    private static object StagingReady(StagingInput i) => new
    {
        ready = ResearchTactics.StagingReady(i.AtRally, i.VisibleRequired, i.WolvesReady,
            i.WolvesAssigned, i.Required, i.WaitedSeconds),
    };

    private static object StrikeStation(StrikeStationInput i)
    {
        var stations = i.Stations.Select(s => new ResearchTactics.Target(s.Id, IgcObjectType.Station,
            s.Sector, Vec(s.Position), false, s.Damaged, Garrison: s.Garrison)).ToArray();
        var pilots = i.Pilots.Select(p => new ResearchTactics.Pilot(p.Id, p.Sector, Vec(p.Position), false, true)).ToArray();
        var picked = ResearchTactics.PickStrikeStation(stations, pilots, i.Time, i.Abandoned);
        return new { targetId = picked?.Id };
    }

    private static object Rally(RallyInput i)
    {
        var friendly = i.FriendlyStations.Select(t => new ResearchTactics.Target(t.Id, t.Type, t.Sector,
            default, t.Friendly)).ToArray();
        var targets = i.Targets.Select(t => new ResearchTactics.Target(t.Id, t.Type, t.Sector,
            default, t.Friendly, DestinationSector: t.DestinationSector)).ToArray();
        var rally = ResearchTactics.ChooseRally(friendly, targets, i.StrikeSector);
        return new { targetId = rally?.Id, targetType = rally?.Type, sector = rally?.Sector };
    }

    private static object PodTransition(PodInput i) => new
    {
        transition = BotManager.ClassifyLifeTransition(i.WasPod, i.PodDocked, i.IsPod, i.StationId),
    };

    private static object MissionRefresh(MissionRefreshInput i) => new
    {
        reset = true,
        reinforcement = BotManager.ReinforcementLife(i.WasPod, i.MissionChanged),
    };

    private static ClusterGraph Graph(ClusterInfo.ClusterEntry[] clusters, ClusterInfo.WarpEdge[] warps) => new()
    {
        Clusters = clusters,
        Warps = warps,
    };

    private static Vector3 Vec(float[] v) => v.Length == 3 ? new(v[0], v[1], v[2])
        : throw new InvalidDataException("A recorded vector must have exactly three components");

    // Input DTOs. They deliberately contain only the values read by each pure
    // core; extra raw-record fields (source/cookie/telemetry) are ignored.
    private sealed record CombatCandidate(short Id, BotTargetClass Class, bool IsThreat, float Distance);
    private sealed record CombatInput(short SelfShipId, short MyCluster, int TeammatesInCluster,
        Dictionary<short, int> EngagerCounts, CombatCandidate[] Candidates);
    private sealed record LoadoutInput(short CivId, Showcase.HullRole Role, string TechMask, short DockTypeId,
        short? TargetStationTypeId, long Budget, bool PreferMines, bool RescueProbe,
        TechLoadoutSelector.StrikeHullPref StrikePref, bool Boarding, bool AlephFieldMines);
    private sealed record SupportLoadoutInput(short CivId, string TechMask, short DockTypeId, long Budget);
    private sealed record MedevacInput(BotManager.MedevacCandidate[] Candidates);
    private sealed record DropInput(bool OwnStationInSector, int OwnPodsInSector, int RecentOwnEjects,
        bool OwnRescueProbeNearby, double Now, double LastRescueProbeAt, double LoadTime);
    private sealed record DefenseBudgetInput(int Population, int AlreadyAssigned, int Cap, double Share);
    private sealed record DefenseAssignmentInput(int Population, int AlreadyAssigned, int Cap, double Share,
        BotManager.DefenseAssignmentCandidate[] Candidates);
    private sealed record RouteWarp(short WarpId, short DestinationId, short ClusterId, short PartnerClusterId);
    private sealed record RouteInput(short From, short To, int MaxRoutes, RouteWarp[] Warps);
    private sealed record LeastLoadedInput(int[] Counts);
    private sealed record NextHopInput(short Current, short[] Path);
    private sealed record ScenarioTargetInput(Showcase.HullRole Role, short? Rendezvous, short EnemyHome);
    private sealed record PositionedEnemy(short Id, float[] Position);
    private sealed record CommanderDefenseInput(float[] Charge, short? Current, PositionedEnemy[] Enemies);
    private sealed record SupportSlotInput(int Ordinal, int Count, double Share);
    private sealed record SectorCount(short Sector, int Count);
    private sealed record SupportPhaseInput(bool FinaleTriggered, short? FinaleSector, bool StrikeWaveActive,
        short StrikeWaveSector, SectorCount[] FighterRendezvous, SectorCount[] FightersPresent);
    private sealed record SupportDestinationInput(short? DamagedStationSector, BotManager.SupportPhaseSignal Phase,
        short? FallbackSector);
    private sealed record RepairHoldInput(double Now, double StartedAt, float BestFraction, float ObservedFraction,
        double ObservedAgeSeconds, double LastImproveAt, double MaxAgeSeconds, double NoImproveSeconds, double LeashSeconds);
    private sealed record ConstructionRockInput(short AsteroidClass, short[] Reserved, ResearchCommander.Rock[] Rocks);
    private sealed record ConstructorOrderInput(short RockSector, short ShipSector);
    private sealed record DonationInput(int Wallet, long BucketPrice, long Funded);
    private sealed record FinaleTriggerInput(double AfterMinutes, string SideSelection, double MinRelaunchShare,
        int MinKillsLastFiveMinutes, double ElapsedSeconds, ShowcaseFinale.Maturity[] Sides, int RecentKills);
    private sealed record EscortEnemyInput(short Id, float[] Position, float[] Velocity, bool AttackingBomber);
    private sealed record EscortInput(float[] Escort, float[] Bomber, EscortEnemyInput[] Enemies, float Radius, float Leash);
    private sealed record VolleyInput(StrikeVolley.StationProfile Station, StrikeVolley.Loadout?[] Loadouts,
        int HullsAvailable, double Window, double SurvivalMargin, int Losses, int Defenders, double LandingShare);
    private sealed record ReleaseInput(double Now, double? GoTime, bool InCommittedWave, bool AlreadyReleased);
    private sealed record WingTargetInput(int Required, int Reserved, int Cap);
    private sealed record CommitInput(int Assembled, int Required, double? StagingSince, double Now,
        double? DeadlineAt, double Window, double Lead);
    private sealed record MultiSideInput(short Own, short Origin, MultiSideEnemy.RankedCandidate[] Candidates);
    private sealed record FrontiersInput(ClusterInfo.ClusterEntry[] Clusters, ClusterInfo.WarpEdge[] Warps,
        short[] KnownSectors, WarpKnowledge[] WarpKnowledge, short OwnHome, LaneKey[]? DoneLanes, short[]? SweptSectors);
    private sealed record Pair<T>(int Key, T Value);
    private sealed record Hop(short From, short To, int Hops);
    private sealed record ExplorationAssignInput(FrontierLane[] Lanes, ScoutPost[] Scouts,
        Pair<FrontierLane>[]? Current, Hop[] Hops);
    private sealed record ReconHop(short Sector, int Hops);
    private sealed record ReconInput(ScoutPost[] Scouts, short Target, ReconHop[] Hops);
    private sealed record ClashInput(ClusterInfo.ClusterEntry[] Clusters, ClusterInfo.WarpEdge[] Warps,
        short OwnHome, short EnemyHome, short[] KnownSectors, RockSighting[] Rocks, MinerSighting[] OwnMiners,
        MinerSighting[] EnemyMiners, StationSighting[] EnemyStations, bool TopologyFallback);
    private sealed record StagingInput(int AtRally, int VisibleRequired, int WolvesReady, int WolvesAssigned,
        int Required, double WaitedSeconds);
    private sealed record StrikeStationDTO(short Id, bool Damaged, bool Garrison, short Sector, float[] Position);
    private sealed record StrikePilotDTO(short Id, short Sector, float[] Position);
    private sealed record StrikeStationInput(double Time, Dictionary<short, double>? Abandoned,
        StrikeStationDTO[] Stations, StrikePilotDTO[] Pilots);
    private sealed record RallyTarget(short Id, IgcObjectType Type, short Sector, short DestinationSector, bool Friendly);
    private sealed record RallyInput(short StrikeSector, RallyTarget[] FriendlyStations, RallyTarget[] Targets);
    private sealed record PodInput(bool WasPod, bool PodDocked, bool IsPod, short StationId);
    private sealed record MissionRefreshInput(bool ShipChanged, bool SessionChanged, bool GenerationChanged,
        bool CookieChanged, bool MissionChanged, bool WasPod);
}
