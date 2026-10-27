using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

/// <summary>P703 socket-free controller. Policy is a port extension; purchases obey
/// sideigc.h:237-280, wallet donations FedSrv.CPP:7137-7208, and constructor orders
/// FedSrv.CPP:11343-11380. Completion always comes from observed world state.</summary>
internal sealed partial class ResearchCommander
{
    // Native mission default (igc.h:1331), also used by the current client investment pane.
    private const int MaxDronesPerType = 4;
    internal sealed record Unit(short Id, short DroneType, short StationType, bool Alive, short Sector = -1);
    internal sealed record Rock(short Id, short Capabilities, bool Available, bool Building = false, short Sector = -1, float Ore = 1);
    internal sealed record Funding(long Money, long Milliseconds);
    internal sealed record Snapshot(double Time, bool Started, int Wallet, TechTreeBitMask Developments,
        short[] Stations, HashSet<short> CompletedDevelopments, Unit[] Units, Rock[] Rocks,
        IReadOnlyDictionary<short, Funding> FundingByBucket, MissionGoals? Goals = null)
    {
        internal Snapshot(double time, bool started, int wallet, TechTreeBitMask developments,
            short[] stations, HashSet<short> completedDevelopments, Unit[] units, Rock[] rocks,
            IReadOnlyDictionary<short, Funding> fundingByBucket)
            : this(time, started, wallet, developments, stations, completedDevelopments, units, rocks, fundingByBucket, null) { }
    }
    internal sealed record Command(string Kind, short Id = -1, int Amount = 0, short Target = -1, string Text = "");
    internal sealed record Report(short Side, string Plan, int Step, int TotalSteps, string Action, string Name,
        string Status, double ProgressPercent, double StepProgressPercent, int Money, double? EtaSeconds,
        string EtaBasis, string[] CompletedTechs, string[] Stations, short? Constructor, short? ReservedRock, int Cycles);

    private sealed class Work
    {
        internal double Began = double.NaN;
        internal double LastDonation = double.NegativeInfinity;
        internal double LastOrder = double.NegativeInfinity;
        internal long SeenMoney = -1, SeenMilliseconds = -1;
        internal double FundingObservedAt;
        internal HashSet<short>? Before;
        internal short? Constructor;
        internal short? Rock;
        internal int? PendingWallet;
        internal long PendingFunding;
    }
    private readonly ResearchTree _tree;
    private readonly ResearchPlan _plan;
    private readonly IReadOnlyList<BucketCatalog.BucketView> _buckets;
    private readonly Dictionary<int, Work> _work = new();
    private readonly Dictionary<short, Work> _repairs = new();
    private readonly Dictionary<short, short> _reservations;
    private readonly bool _loop;
    private readonly double _researchTimeScale;
    private int _index, _cycles;
    private double _firstTime = double.NaN, _lastIncomeTime, _income;
    private int? _lastWallet;
    private string _announcement = "";
    internal Report? Telemetry { get; private set; }
    internal short Side { get; }
    internal string Epoch { get; }
    /// <summary>P802 — the civilization the executing plan was built for. A
    /// lobby CHANGE_TEAM_CIV re-selects the side's plan for the new civ; the
    /// research loop rebuilds this commander when this no longer matches the
    /// fresh briefing's <c>Plan.CivId</c> (the epoch is unchanged — native keeps
    /// one mission identity across a pre-start civ change).</summary>
    internal short PlanCivId => _plan.CivId;

    internal ResearchCommander(short side, string epoch, ResearchTree tree, ResearchPlan plan,
        TechTreeBitMask initialTechs, Dictionary<short, short> reservations, bool loop = false, double researchTimeScale = 1)
    {
        ResearchPlanCatalog.Validate(tree, plan.Core, plan);
        Side = side; Epoch = epoch; _tree = tree; _plan = plan; _reservations = reservations; _loop = loop; _researchTimeScale = researchTimeScale;
        // Freeze costs with CIV base attributes, never the post-research GA snapshot (bucketigc.cpp:66-74).
        _buckets = BucketCatalog.Describe(tree.Core, initialTechs, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
    }

    internal void ReleaseReservations()
    {
        foreach (var id in _reservations.Where(kv => kv.Value == Side).Select(kv => kv.Key).ToArray()) _reservations.Remove(id);
    }

    private IReadOnlyList<Command> TickPlan(Snapshot world)
    {
        var commands = new List<Command>();
        if (double.IsNaN(_firstTime)) _firstTime = _lastIncomeTime = world.Time;
        if (_lastWallet is { } prior && world.Wallet > prior) { _income += world.Wallet - prior; _lastIncomeTime = world.Time; }
        _lastWallet = world.Wallet;
        if (!world.Started) { Publish(world, null, null, "waiting for mission start", 0, null); return commands; }

        // Retire dead assets before selecting a repair. Otherwise an interrupted step
        // could hold the only compatible rock while a different prerequisite needs rebuilding.
        foreach (var pending in _work.Values.Concat(_repairs.Values))
        {
            if (pending.Constructor is { } id && !world.Units.Any(u => u.Id == id && u.Alive))
            {
                Release(pending); pending.Constructor = null; pending.PendingWallet = null;
                pending.SeenMoney = -1; pending.LastOrder = double.NegativeInfinity;
            }
            if (pending.Rock is { } rock && !world.Rocks.Any(r => r.Id == rock && (r.Available || r.Building))) Release(pending);
        }

        // Retention in P702 is a planning assumption, not a live guarantee. Rebuild the earliest
        // lost prerequisite base (or its now-buyable successor), preserving interrupted work.
        ResearchStep? repair = null;
        var stationsToKeep = new[] { _tree.Civ.InitialStationTypeId }
            .Concat(_plan.Steps.Take(_index).Where(s => s.Action == "station").Select(s => s.Id)).Distinct();
        foreach (short id in stationsToKeep)
        {
            if (HasStation(world, id)) continue;
            var effective = SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[id], world.Developments);
            var candidate = new ResearchStep { Action = "station", Id = effective.StationTypeId, Name = effective.Name,
                AsteroidClass = (short)(effective.AabmBuild & -effective.AabmBuild) };
            if (CanBuy(world, _tree.Find("station", candidate.Id))) { repair = candidate; break; }
        }

        if (_index >= _plan.Steps.Count && repair is null)
        {
            ReleaseReservations();
            Publish(world, null, null, "complete; idle", 1, 0);
            Announce(commands, $"Research plan {_plan.Name} complete. Standing by.");
            if (_loop && _plan.Steps.Count > 0) { _cycles++; _index = 0; _work.Clear(); }
            return commands;
        }
        if (repair is null && _index >= _plan.Steps.Count) return commands;
        var step = repair ?? _plan.Steps[_index];
        if (step.Action == "station")
        {
            var effective = SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[step.Id], world.Developments);
            if (effective.StationTypeId != step.Id)
                step = new ResearchStep { Action = "station", Id = effective.StationTypeId, Name = effective.Name,
                    AsteroidClass = (short)(effective.AabmBuild & -effective.AabmBuild) };
        }
        var works = repair is null ? _work : null;
        Work work;
        if (works is not null)
        {
            if (!works.TryGetValue(_index, out work!)) works[_index] = work = new();
        }
        else if (!_repairs.TryGetValue(step.Id, out work!)) _repairs[step.Id] = work = new();
        if (double.IsNaN(work.Began)) work.Began = world.Time;
        string prefix = repair is null ? "" : "rebuilding lost prerequisite: ";
        Announce(commands, $"{prefix}{step.Action} {step.Name} (plan {_plan.Name}, step {_index + 1}/{_plan.Steps.Count}).");

        bool completed = false;
        double progress = 0;
        double? eta = null;
        string status;
        if (step.Action == "wait")
        {
            bool tech = TechTreeBitMask.FromBytes(step.RequiredTechs).IsSubsetOf(Techs(world));
            double elapsed = world.Time - work.Began;
            completed = tech && elapsed >= step.WaitSeconds;
            progress = tech ? step.WaitSeconds == 0 ? 1 : Math.Clamp(elapsed / step.WaitSeconds, 0, 1) : 0;
            eta = tech ? Math.Max(0, step.WaitSeconds - elapsed) : null;
            status = tech ? "waiting for duration" : "waiting for tech mask";
        }
        else
        {
            var node = _tree.Find(step.Action, step.Id);
            if (step.Action == "develop")
                completed = world.CompletedDevelopments.Contains(step.Id)
                    || DevelopmentLogic.IsObsolete(_tree.Core.Developments[step.Id], world.Developments);
            if (step.Action == "station") completed = HasStation(world, step.Id);
            work.Before ??= world.Units.Select(u => u.Id).ToHashSet();
            if (step.Action == "drone")
                completed = world.Units.Any(u => u.DroneType == step.Id && !work.Before.Contains(u.Id));
            var bucket = _buckets.FirstOrDefault(b => step.Action switch
            {
                "develop" => b.ItemType == BucketItemType.BitDevelopment && b.DevelopmentId == step.Id,
                "station" => b.ItemType == BucketItemType.BitStationType && b.StationTypeId == step.Id,
                "drone" => b.ItemType == BucketItemType.BitDroneType && b.DroneTypeId == step.Id,
                _ => false,
            });
            if (completed) status = "complete";
            else if (bucket is null) status = "waiting: bucket absent from initial catalog";
            else if (!CanBuy(world, node)) status = "waiting for prerequisites";
            else
            {
                var funding = world.FundingByBucket.GetValueOrDefault(bucket.Id) ?? new(0, 0);
                double moneyFraction = bucket.Price <= 0 ? 1 : Math.Clamp((double)funding.Money / bucket.Price, 0, 1);
                if (funding.Money != work.SeenMoney || funding.Milliseconds != work.SeenMilliseconds)
                {
                    work.SeenMoney = funding.Money; work.SeenMilliseconds = funding.Milliseconds;
                    work.FundingObservedAt = world.Time;
                }
                // BUCKET_STATUS is donation-driven. Estimate between observations with the
                // native money-proportional time ceiling (bucketigc.h:44-61), never as completion proof.
                double seconds = Math.Min(Math.Floor(moneyFraction * bucket.TimeToBuildSec),
                    funding.Milliseconds / 1000.0 + Math.Max(0, world.Time - work.FundingObservedAt) * _researchTimeScale);
                progress = Math.Min(moneyFraction, bucket.TimeToBuildSec == 0 ? 1 : seconds / bucket.TimeToBuildSec);
                double rate = _income > 0 && _lastIncomeTime > _firstTime ? _income / (_lastIncomeTime - _firstTime) : 0;
                double deficit = Math.Max(0, bucket.Price - funding.Money - world.Wallet);
                eta = deficit == 0 ? Math.Max(0, bucket.TimeToBuildSec - seconds) / _researchTimeScale
                    : rate > 0 ? deficit / rate + Math.Max(0, bucket.TimeToBuildSec - seconds) / _researchTimeScale : null;
                status = funding.Money >= bucket.Price ? "building bucket" : world.Wallet == 0 ? "waiting for money" : "funding";
                bool allowed = true;
                if (step.Action == "drone" && world.Units.Count(u => u.Alive && u.DroneType == step.Id) >= MaxDronesPerType)
                { allowed = false; status = "waiting for live drone cap (4/type)"; eta = null; }
                if (step.Action == "station")
                {
                    progress *= .5;
                    eta = null; // Constructor travel and combat have no reliable finite ETA.
                    var constructors = world.Units.Where(u => u.StationType == step.Id).ToArray();
                    var builder = constructors.FirstOrDefault(u => u.Alive && (work.Constructor is null || u.Id == work.Constructor));
                    if (work.Constructor is { } old && !constructors.Any(u => u.Id == old && u.Alive))
                    {
                        Release(work); work.Constructor = null; work.SeenMoney = -1;
                        work.Before.UnionWith(world.Units.Select(u => u.Id));
                        status = "constructor lost; replacing";
                    }
                    if (builder is not null) work.Constructor = builder.Id;
                    if (work.Rock is { } rock && !world.Rocks.Any(r => r.Id == rock && (r.Available || r.Building))) Release(work);
                    if (work.Rock is null)
                    {
                        var target = world.Rocks.FirstOrDefault(r => r.Available && (r.Capabilities & step.AsteroidClass!.Value) != 0
                            && !_reservations.ContainsKey(r.Id));
                        if (target is not null) { work.Rock = target.Id; _reservations[target.Id] = Side; }
                    }
                    if (work.Rock is null) { allowed = false; status = "waiting for available compatible asteroid"; }
                    if (work.Constructor is { } constructor)
                    {
                        allowed = false; progress = .5;
                        if (work.Rock is { } rockId && !world.Rocks.Any(r => r.Id == rockId && r.Building) && world.Time - work.LastOrder >= 5)
                        {
                            var rockSector = world.Rocks.First(r => r.Id == rockId).Sector;
                            var shipSector = world.Units.First(u => u.Id == constructor).Sector;
                            // The port's build gate requires the constructor to be in the rock's cluster.
                            // Use the existing native goto/warp navigation before issuing BUILD.
                            if (rockSector < 0 || shipSector >= 0)
                            {
                                commands.Add(new(rockSector >= 0 && rockSector != shipSector ? "goto" : "build",
                                    constructor, Target: rockId));
                                work.LastOrder = world.Time;
                            }
                        }
                        status = work.Rock is null ? status : "constructor travelling/building";
                    }
                }
                if (work.PendingWallet is { } pending && (world.Wallet != pending || funding.Money != work.PendingFunding
                    || world.Time - work.LastDonation >= 5)) work.PendingWallet = null;
                if (allowed && work.PendingWallet is null && funding.Money < bucket.Price && world.Wallet > 0)
                {
                    int amount = (int)Math.Min(world.Wallet, bucket.Price - funding.Money);
                    commands.Add(new("donate", bucket.Id, amount));
                    work.LastDonation = world.Time; work.PendingWallet = world.Wallet; work.PendingFunding = funding.Money;
                }
            }
        }
        int displayedStep = Math.Min(_index + 1, _plan.Steps.Count);
        if (completed)
        {
            Release(work);
            Announce(commands, $"Complete: {step.Name}.");
            if (repair is null) _index++; else _repairs.Remove(step.Id);
            progress = 1;
        }
        Publish(world, step, work, prefix + status, progress, eta, displayedStep);
        return commands;
    }

    private void Announce(List<Command> commands, string text)
    {
        if (_announcement == text) return;
        _announcement = text;
        commands.Add(new("chat", Text: text));
    }
    private void Release(Work work)
    {
        if (work.Rock is { } id && _reservations.GetValueOrDefault(id, (short)-1) == Side) _reservations.Remove(id);
        work.Rock = null;
    }
    private bool HasStation(Snapshot world, short id)
    {
        var visited = new HashSet<short>();
        while (visited.Add(id) && _tree.Core.StationTypes.TryGetValue(id, out var station))
        {
            if (world.Stations.Contains(id)) return true;
            id = station.SuccessorStationTypeId;
        }
        return false;
    }
    private TechTreeBitMask Techs(Snapshot world) => world.Stations.Aggregate(world.Developments,
        (techs, id) => techs.Or(TechTreeBitMask.FromBytes(_tree.Core.StationTypes[id].TtbmEffects)));
    private bool CanBuy(Snapshot world, ResearchNode node)
    {
        var techs = Techs(world);
        if (node.Kind == "develop") return TechTreeBitMask.FromBytes(node.Required).IsSubsetOf(techs);
        if (node.Kind == "station" && SuccessorUpgrade.StationSuccessor(_tree.Core,
            _tree.Core.StationTypes[node.Id], world.Developments).StationTypeId != node.Id) return false;
        return world.Stations.Any(id => TechTreeBitMask.RequiredTechsBuyable(node.Required,
            _tree.Core.StationTypes[id].TtbmLocal, techs));
    }
    private void Publish(Snapshot world, ResearchStep? step, Work? work, string status, double progress, double? eta, int? displayedStep = null)
    {
        progress = Math.Clamp(progress, 0, 1);
        double overall = _plan.Steps.Count == 0 ? 1 : Math.Clamp((_index + (progress < 1 ? progress : 0)) / _plan.Steps.Count, 0, 1);
        Telemetry = new(Side, _plan.Name, displayedStep ?? Math.Min(_index + 1, _plan.Steps.Count), _plan.Steps.Count,
            step?.Action ?? "idle", step?.Name ?? "", status, overall * 100, progress * 100, world.Wallet,
            eta, "current step estimate from BUCKET_STATUS, sim time and showcase research scale; income from wallet credits; constructor travel/combat/unknown income = null",
            world.CompletedDevelopments.Order().Select(id => _tree.Core.Developments.TryGetValue(id, out var d) ? d.Name : id.ToString()).ToArray(),
            world.Stations.Select(id => _tree.Core.StationTypes[id].Name).ToArray(), work?.Constructor, work?.Rock, _cycles);
    }
}
