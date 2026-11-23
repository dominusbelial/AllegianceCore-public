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

    // ── P1077 — constructor stall safety net (PORT-ONLY bot orchestration) ──
    // A station step that can never complete must not block the plan for the rest
    // of the match. These are policy numbers for the commander's own bookkeeping,
    // not native rules: native's commander is a human, and native drones simply
    // keep flying their accepted build plan (Igc/shipIGC.cpp:2810-2827).
    /// <summary>No measurable progress toward the reserved rock for this long
    /// releases the reservation and retargets another compatible rock. Measured
    /// worst trip in the P1077 reproduction is 98-112 s (3748 units at the
    /// constructor's 60 u/s cruise plus launch and approach), so 180 s is about
    /// 1.6x that worst case and still far inside one match.</summary>
    internal const double StationNoProgressSeconds = 180;
    /// <summary>Hard cap on one station step that has not registered a build, for
    /// snapshots without wire positions (no progress signal is available there).</summary>
    internal const double StationStepCapSeconds = 600;
    /// <summary>How long a station step deferred for lack of any alternative rock
    /// waits before it is retried once through the unbuilt-station path.</summary>
    internal const double StationRetrySeconds = 300;
    private const int MaxStationRetries = 1;
    internal sealed record Unit(short Id, short DroneType, short StationType, bool Alive, short Sector = -1,
        float[]? Position = null);
    internal sealed record Rock(short Id, short Capabilities, bool Available, bool Building = false, short Sector = -1, float Ore = 1,
        float[]? Position = null);
    internal sealed record Funding(long Money, long Milliseconds);
    internal sealed record Snapshot(double Time, bool Started, int Wallet, TechTreeBitMask Developments,
        short[] Stations, HashSet<short> CompletedDevelopments, Unit[] Units, Rock[] Rocks,
        IReadOnlyDictionary<short, Funding> FundingByBucket, MissionGoals? Goals = null, long WalletDebited = 0, long DonationSettled = 0)
    {
        internal Snapshot(double time, bool started, int wallet, TechTreeBitMask developments,
            short[] stations, HashSet<short> completedDevelopments, Unit[] units, Rock[] rocks,
            IReadOnlyDictionary<short, Funding> fundingByBucket)
            : this(time, started, wallet, developments, stations, completedDevelopments, units, rocks, fundingByBucket, null) { }
    }
    internal sealed record Command(string Kind, short Id = -1, int Amount = 0, short Target = -1, string Text = "");
    internal sealed record Report(short Side, string Plan, int Step, int TotalSteps, string Action, string Name,
        string Status, double ProgressPercent, double StepProgressPercent, int Money, double? EtaSeconds,
        string EtaBasis, string[] CompletedTechs, string[] Stations, short? Constructor, short? ReservedRock, int Cycles,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Job[]? Jobs = null);

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
        /// <summary>P1077 — last sim time the reserved-rock trip showed measurable
        /// progress (the constructor's distance to the rock shrinking, or the rock
        /// reporting a registered construction). NaN = not yet observed.</summary>
        internal double ProgressAt = double.NaN;
        /// <summary>P1077 — smallest distance from the constructor to its reserved
        /// rock seen so far (float.MaxValue = none observed yet).</summary>
        internal float ProgressDistance = float.MaxValue;
        /// <summary>P1077 — sim time the current rock trip started (its reservation), the
        /// fallback origin of the no-progress window before the first wire pose lands.</summary>
        internal double TripAt = double.NaN;
        /// <summary>P1077 — rocks this step gave up on after a no-progress stall;
        /// the next rock pick skips them.</summary>
        internal readonly HashSet<short> AvoidedRocks = new();
    }

    /// <summary>P1077 — a station step the no-progress safety net removed from the
    /// plan because no compatible rock was left. Retried once, later, through the
    /// existing unbuilt-station (repair) path.</summary>
    private sealed record DeferredStation(double RetryAt, string Name);
    private readonly ResearchTree _tree;
    private readonly ResearchPlan _plan;
    private readonly IReadOnlyList<BucketCatalog.BucketView> _buckets;
    private readonly Dictionary<int, Work> _work = new();
    private readonly Dictionary<short, Work> _repairs = new();
    private readonly Dictionary<short, DeferredStation> _deferredStations = new();
    /// <summary>P1077 — station types that spent their retry budget after a no-progress stall; the
    /// unbuilt-station scan stops re-arming them so they cannot starve the plan.</summary>
    private readonly HashSet<short> _abandonedStations = new();
    /// <summary>P1077 — deferrals per station type (drives <see cref="MaxStationRetries"/>).</summary>
    private readonly Dictionary<short, int> _stationStallAttempts = new();
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
    internal string PlanName => _plan.Name;

    internal ResearchCommander(short side, string epoch, ResearchTree tree, ResearchPlan plan,
        TechTreeBitMask initialTechs, Dictionary<short, short> reservations, bool loop = false, double researchTimeScale = 1)
    {
        ResearchPlanCatalog.Validate(tree, plan.Core, plan);
        Side = side; Epoch = epoch; _tree = tree; _plan = ResearchSchedule(tree, plan); _reservations = reservations; _loop = loop; _researchTimeScale = researchTimeScale;
        // Freeze costs with CIV base attributes, never the post-research GA snapshot (bucketigc.cpp:66-74).
        _buckets = BucketCatalog.Describe(tree.Core, initialTechs, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
    }

    private static ResearchPlan ResearchSchedule(ResearchTree tree, ResearchPlan plan)
    {
        // Authored plans keep their exact order. Recognize the canonical
        // generated fallback by name and exact steps, so a custom authored
        // plan called "generated" cannot silently acquire different semantics.
        if (plan.Name != "generated") return plan;
        var canonical = tree.Generate(plan.Core);
        if (plan.Steps.Count != canonical.Steps.Count || !plan.Steps.Zip(canonical.Steps).All(pair =>
                pair.First.Action == pair.Second.Action && pair.First.Id == pair.Second.Id
                && pair.First.Name == pair.Second.Name && pair.First.AsteroidClass == pair.Second.AsteroidClass
                && pair.First.WaitSeconds == pair.Second.WaitSeconds
                && pair.First.RequiredTechs.SequenceEqual(pair.Second.RequiredTechs)
                && pair.First.ExpectedUnlocks.SequenceEqual(pair.Second.ExpectedUnlocks))) return plan;
        return tree.Generate(plan.Core, prioritizeResearch: true);
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
        bool deferredRetry = false;
        var stationsToKeep = new[] { _tree.Civ.InitialStationTypeId }
            .Concat(_plan.Steps.Take(_index).Where(s => s.Action == "station").Select(s => s.Id)).Distinct();
        foreach (short id in stationsToKeep)
        {
            if (HasStation(world, id)) continue;
            // P1077 — a station the safety net deferred is not "lost": wait for its retry window
            // instead of re-arming the same unmovable step every tick. One whose retry budget is
            // spent stays abandoned so it cannot starve the plan of rocks or constructors.
            if (_abandonedStations.Contains(id) || AwaitingDeferredRetry(world, id)) continue;
            var effective = SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[id], world.Developments);
            var candidate = new ResearchStep { Action = "station", Id = effective.StationTypeId, Name = effective.Name,
                AsteroidClass = (short)(effective.AabmBuild & -effective.AabmBuild) };
            if (CanBuy(world, _tree.Find("station", candidate.Id))) { repair = candidate; break; }
        }

        // P1077 — retry a deferred station step once, later, through the same unbuilt-station path.
        if (repair is null)
            (repair, deferredRetry) = PromoteDeferredStation(world);

        if (_index >= _plan.Steps.Count && repair is null)
        {
            ReleaseReservations();
            Publish(world, null, null, "complete; idle", 1, 0);
            Announce(commands, $"Research plan {_plan.Name} complete. Standing by.");
            if (_loop && _plan.Steps.Count > 0)
            {
                _cycles++; _index = 0; _work.Clear();
                _deferredStations.Clear(); _abandonedStations.Clear(); _stationStallAttempts.Clear();
            }
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
        string prefix = !deferredRetry ? repair is null ? "" : "rebuilding lost prerequisite: " : "retrying deferred station: ";
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
                    // P1077 — the PORT-ONLY safety net: a constructor that cannot make measurable
                    // progress toward its reserved rock must not hold the plan for the whole match.
                    bool deferred = work.Constructor is { } tracking && work.Rock is { } trackedRock
                        && CheckConstructorProgress(work, tracking, trackedRock, world, step);
                    if (!deferred && work.Rock is null)
                    {
                        var candidates = work.AvoidedRocks.Count == 0
                            ? world.Rocks
                            : world.Rocks.Where(r => !work.AvoidedRocks.Contains(r.Id));
                        var target = PickConstructionRock(candidates, step.AsteroidClass!.Value, _reservations.Keys);
                        if (target is not null) Reserve(work, target, world);
                    }
                    if (deferred)
                    {
                        allowed = false;
                        status = "step deferred: no constructor progress and no other compatible rock";
                        if (repair is null) _index++; else _repairs.Remove(step.Id);
                    }
                    else if (work.Rock is null) { allowed = false; status = "waiting for available compatible asteroid"; }
                    if (!deferred && work.Constructor is { } constructor && work.Rock is { } rockId)
                    {
                        allowed = false; progress = .5;
                        if (!world.Rocks.Any(r => r.Id == rockId && r.Building) && world.Time - work.LastOrder >= 5)
                        {
                            var rockSector = world.Rocks.First(r => r.Id == rockId).Sector;
                            var shipSector = world.Units.First(u => u.Id == constructor).Sector;
                            // The port's build gate requires the constructor to be in the rock's cluster.
                            // Use the existing native goto/warp navigation before issuing BUILD.
                            if (rockSector < 0 || shipSector >= 0)
                            {
                                string kind = ConstructorOrderKind(rockSector, shipSector);
                                commands.Add(new(kind, constructor, Target: rockId));
                                work.LastOrder = world.Time;
                            }
                        }
                        status = "constructor travelling/building";
                    }
                }
                if (work.PendingWallet is { } pending && (world.Wallet != pending || funding.Money != work.PendingFunding
                    || world.Time - work.LastDonation >= 5)) work.PendingWallet = null;
                if (allowed && work.PendingWallet is null && funding.Money < bucket.Price && world.Wallet > 0)
                {
                    int amount = DonationAmount(world.Wallet, bucket.Price, funding.Money);
                    commands.Add(new("donate", bucket.Id, amount));
                    work.LastDonation = world.Time; work.PendingWallet = world.Wallet; work.PendingFunding = funding.Money;
                }
            }
        }
        int displayedStep = Math.Min(_index + 1, _plan.Steps.Count);
        if (completed)
        {
            Release(work);
            if (step.Action == "station")
            {
                // P1077 — a completed station clears its stall bookkeeping.
                _stationStallAttempts.Remove(step.Id);
                _abandonedStations.Remove(step.Id);
                _deferredStations.Remove(step.Id);
            }
            Announce(commands, $"Complete: {step.Name}.");
            if (repair is null) _index++; else _repairs.Remove(step.Id);
            progress = 1;
        }
        Publish(world, step, work, prefix + status, progress, eta, displayedStep);
        return commands;
    }

    /// <summary>P1077 — the PORT-ONLY constructor-stall safety net for a station step.
    /// <para><b>Progress</b> is the constructor's distance to its reserved rock
    /// shrinking by at least one unit, or the rock reporting a registered
    /// construction (<see cref="Rock.Building"/>, i.e. drilling arrived and
    /// started). Neither for <see cref="StationNoProgressSeconds"/> (180 s ≈ 1.6×
    /// the worst measured 98-112 s trip in the P1077 reproduction) → the rock
    /// reservation is released, the rock is excluded for this step and another
    /// compatible rock is reserved in the same tick; the next BUILD retargets the
    /// constructor. With no alternative rock the step is deferred
    /// (<see cref="StationRetrySeconds"/>, retried once later) and the plan moves on,
    /// so a station step can never block the match again.</para>
    /// <para><b>Never abandoned:</b> a rock that reports <c>Building</c> resets the
    /// clock every tick, so a constructor that is drilling/building is untouched.
    /// Native's constructor has no such policy (a human commander re-tasks by hand,
    /// <c>FedSrv/FedSrv.CPP:3278-3285</c>); this is bot orchestration, not a
    /// simulation rule.</para>
    /// <returns><see langword="true"/> when the step was deferred because no
    /// alternative compatible rock exists (the caller advances the plan).</returns>
    private bool CheckConstructorProgress(Work work, short constructor, short rockId, Snapshot world,
        ResearchStep step)
    {
        var sighting = world.Rocks.FirstOrDefault(r => r.Id == rockId);
        if (sighting is { Building: true })
        {
            work.ProgressAt = world.Time;               // drilling/building — never abandoned
            work.ProgressDistance = float.MaxValue;
            return false;
        }
        double now = world.Time;
        var unit = world.Units.FirstOrDefault(u => u.Id == constructor);
        bool signal = false;
        if (unit?.Position is { Length: 3 } up && sighting?.Position is { Length: 3 } rp)
        {
            signal = true;
            float dx = up[0] - rp[0], dy = up[1] - rp[1], dz = up[2] - rp[2];
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (work.ProgressDistance == float.MaxValue)
                work.ProgressDistance = distance;        // first sighting: baseline only, the clock keeps running
            else if (distance < work.ProgressDistance - 1f)
            {
                work.ProgressDistance = distance;        // closing on the rock = measurable progress
                work.ProgressAt = now;
            }
        }
        // The no-progress window runs from the last measured progress, or from the start of the trip
        // when no measurement has landed yet (a snapshot cadence coarser than the window must not
        // postpone the check). Without wire positions there is no signal at all, so only the hard
        // step cap applies; a trip that IS closing on its rock is healthy however long it takes.
        double idle = !double.IsNaN(work.ProgressAt) ? now - work.ProgressAt : now - work.TripAt;
        bool noProgress = signal && idle >= StationNoProgressSeconds;
        bool capped = !signal && now - work.Began >= StationStepCapSeconds;
        if (!noProgress && !capped) return false;

        Release(work);                                  // drop the reservation
        work.AvoidedRocks.Add(rockId);                   // never re-pick the same rock for this step
        work.Constructor = null;
        work.LastOrder = double.NegativeInfinity;
        work.ProgressAt = double.NaN;
        work.ProgressDistance = float.MaxValue;
        var alternative = PickConstructionRock(
            work.AvoidedRocks.Count == 0 ? world.Rocks : world.Rocks.Where(r => !work.AvoidedRocks.Contains(r.Id)),
            step.AsteroidClass!.Value, _reservations.Keys);
        if (alternative is not null)
        {
            Reserve(work, alternative, world);
            work.Constructor = constructor;              // keep the ship; the next order retargets it
            LogConstructorStall(step, constructor, rockId, idle,
                $"released rock reservation, retargeted asteroid {alternative.Id}");
            return false;
        }
        DeferStation(step, now, idle, constructor, rockId);
        return true;
    }

    /// <summary>P1077 — reserve a rock for this step and start its progress clock. The clock starts
    /// when the trip starts (not at the first wire pose) so a snapshot cadence coarser than
    /// <see cref="StationNoProgressSeconds"/> cannot postpone the check indefinitely.</summary>
    private void Reserve(Work work, Rock target, Snapshot world)
    {
        work.Rock = target.Id;
        _reservations[target.Id] = Side;
        work.TripAt = world.Time;
        work.ProgressAt = double.NaN;
        work.ProgressDistance = float.MaxValue;
    }

    /// <summary>P1077 — one clear log line per safety-net firing (side, step,
    /// constructor, rock, seconds without progress) so an operator can
    /// <c>grep '\[constructor-stall\]'</c> the match log and count them.</summary>
    private void LogConstructorStall(ResearchStep step, short constructor, short rockId, double idleSeconds, string action) =>
        Console.WriteLine(FormattableString.Invariant(
            $"[constructor-stall] side={Side} step={Math.Min(_index + 1, _plan.Steps.Count)}/{_plan.Steps.Count} '{step.Name}' constructor={constructor} rock={rockId} no-progress={idleSeconds:0}s -> {action}"));

    /// <summary>P1077 — remember a station step that could not progress and had no
    /// alternative rock. It is retried once, later, through the unbuilt-station
    /// path; a second failure drops it (logged) so the plan keeps moving.</summary>
    private void DeferStation(ResearchStep step, double now, double idleSeconds, short constructor, short rockId)
    {
        int attempts = _stationStallAttempts[step.Id] = _stationStallAttempts.GetValueOrDefault(step.Id) + 1;
        if (attempts > MaxStationRetries)
        {
            _abandonedStations.Add(step.Id);
            LogConstructorStall(step, constructor, rockId, idleSeconds, "retry already spent; step abandoned");
            return;
        }
        _deferredStations[step.Id] = new(now + StationRetrySeconds, step.Name);
        LogConstructorStall(step, constructor, rockId, idleSeconds,
            $"no other compatible rock; step deferred {StationRetrySeconds:0}s (retry {attempts}/{MaxStationRetries})");
    }

    /// <summary>P1077 — true while a station type sits in the deferral window, so the
    /// unbuilt-prerequisite scan does not immediately re-arm the same step.</summary>
    private bool AwaitingDeferredRetry(Snapshot world, short id)
    {
        if (!_deferredStations.TryGetValue(id, out var entry)) return false;
        if (HasStation(world, id)) { _deferredStations.Remove(id); return false; }
        return true;
    }

    /// <summary>P1077 — promote the earliest due deferred station step back into
    /// work through the existing unbuilt-station (repair) path. Entries whose
    /// retry budget is spent, whose station now exists, or whose type is no longer
    /// buyable are dropped.</summary>
    private (ResearchStep? Step, bool Retry) PromoteDeferredStation(Snapshot world)
    {
        if (_deferredStations.Count == 0) return (null, false);
        foreach (var (id, entry) in _deferredStations.OrderBy(kv => kv.Value.RetryAt).ToArray())
        {
            if (HasStation(world, id)) { _deferredStations.Remove(id); continue; }
            if (world.Time < entry.RetryAt) continue;
            var effective = SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[id], world.Developments);
            if (!CanBuy(world, _tree.Find("station", effective.StationTypeId))) continue;
            _deferredStations.Remove(id);
            return (new ResearchStep { Action = "station", Id = effective.StationTypeId, Name = effective.Name,
                AsteroidClass = (short)(effective.AabmBuild & -effective.AabmBuild) }, true);
        }
        return (null, false);
    }

    /// <summary>Pure construction-rock choice from the commander's observed snapshot.</summary>
    internal static Rock? PickConstructionRock(IEnumerable<Rock> rocks, short asteroidClass,
        IReadOnlyCollection<short> reserved)
    {
        var target = rocks.FirstOrDefault(r => r.Available && (r.Capabilities & asteroidClass) != 0
            && !reserved.Contains(r.Id));
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("ResearchCommander", "ConstructionRock",
                new { asteroidClass, reserved = reserved.Order().ToArray(), rocks = rocks.ToArray() },
                new { rockId = target?.Id });
        return target;
    }

    /// <summary>Pure constructor travel-vs-build order from observed sectors.</summary>
    internal static string ConstructorOrderKind(short rockSector, short shipSector)
    {
        string kind = rockSector >= 0 && rockSector != shipSector ? "goto" : "build";
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("ResearchCommander", "ConstructorOrder",
                new { rockSector, shipSector }, new { kind });
        return kind;
    }

    /// <summary>Pure bounded bucket donation, shared by every research/build step.</summary>
    internal static int DonationAmount(int wallet, long bucketPrice, long funded)
    {
        int amount = (int)Math.Min(wallet, bucketPrice - funded);
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("ResearchCommander", "Donation",
                new { wallet, bucketPrice, funded }, new { amount });
        return amount;
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
