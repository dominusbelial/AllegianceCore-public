using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Strategy;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class ResearchCommander
{
    internal const int ParallelResearchLimit = 4, ParallelConstructionLimit = 2;
    internal const double ConstructionLeaseSeconds = 180;
    internal sealed record Job(string Kind, short Id, string State, short? Constructor, short? Rock);
    internal IReadOnlyList<Job> PortfolioJobs { get; private set; } = [];
    private readonly Dictionary<(string Kind, short Id), Work> _portfolioWork = new();
    private readonly HashSet<short> _portfolioDrones = new();
    private readonly Dictionary<short, double> _rockCooldown = new();
    private readonly HashSet<short> _builtStations = new();
    private long _pendingDebit, _debitAtSend, _settledAtSend;
    private int _walletAtSend;
    internal int ProtectedReserve { get; private set; }
    private long _tailSpent, _openingSpent, _pendingTail;

    private static bool Fundable(BucketCatalog.BucketView b)
    {
        byte group = b.Development?.GroupId ?? b.StationType?.GroupId ?? b.DroneType?.GroupId ?? byte.MaxValue;
        // BuyableGroupID is a signed char (igc.h:639); the donation handler
        // requires a nonnegative group (FedSrv.CPP:7147). Buckets without a
        // positive price/time cannot advance in the native Update body.
        return group < 128 && b.Price > 0 && b.TimeToBuildSec > 0;
    }

    // Igc/bucketigc.cpp:66-74 freezes development cost/time at creation.
    // Igc/bucketigc.h:44-67 advances each funded bucket independently, bounded
    // by invested money. This policy never asserts completion from a timer.
    private IReadOnlyList<Command> TickPortfolio(Snapshot world)
    {
        var commands = new List<Command>();
        if (!world.Started) { Publish(world, null, null, "portfolio: waiting for mission start", 0, null); return commands; }
        CivExecutionPlan.TryRead(_plan, out double share, out int opening);
        var jobs = new List<Job>();
        var completed = new HashSet<(string, short)>();
        bool Done(ResearchStep s) => s.Action switch
        {
            "develop" => world.CompletedDevelopments.Contains(s.Id)
                || DevelopmentLogic.IsObsolete(_tree.Core.Developments[s.Id], world.Developments),
            "station" => HasStation(world, s.Id),
            "drone" => _portfolioDrones.Contains(s.Id),
            _ => false,
        };
        foreach (var s in _plan.Steps)
        {
            if (Done(s)) { completed.Add((s.Action, s.Id)); if (s.Action == "station") _builtStations.Add(s.Id); }
        }
        foreach (var (key, work) in _portfolioWork)
        {
            if (key.Kind == "drone" && work.Before is { } before
                && world.Units.Any(u => u.Alive && u.DroneType == key.Id && !before.Contains(u.Id)))
            { _portfolioDrones.Add(key.Id); completed.Add(key); }
            if (completed.Contains(key) || key.Kind == "station" && HasStation(world, key.Id))
            { Release(work); if (key.Kind == "station") _builtStations.Add(key.Id); continue; }
            bool shell = work.Rock is { } activeRock && world.Rocks.Any(r => r.Id == activeRock && r.Building);
            if (work.Constructor is { } id && !world.Units.Any(u => u.Id == id && u.Alive) && !shell)
            { Release(work); work.Constructor = null; work.LastOrder = double.NegativeInfinity; }
            if (work.Rock is { } rock && !world.Rocks.Any(r => r.Id == rock && (r.Available || r.Building))) Release(work);
            // Lease a travelling site, not a construction shell. A failed route
            // or consumed site cannot hold every other research/fleet job hostage.
            if (!shell && work.Rock is { } leased && world.Time - work.Began >= ConstructionLeaseSeconds)
            { _rockCooldown[leased] = world.Time + ConstructionLeaseSeconds; Release(work); work.Began = world.Time; }
        }
        var miner = _buckets.Where(b => b.DroneType is { PilotType: Ship.PilotTypeMiner }
                && Fundable(b) && CanBuy(world, _tree.Find("drone", b.DroneTypeId)))
            .OrderBy(b => b.Price).ThenBy(b => b.Id).FirstOrDefault();
        // Reserve two complete defensive ships and two replacement miners.
        // A reserve is never an artificial grant: it remains in the real wallet.
        long fighter = world.Stations.Select(id => TechLoadoutSelector.Select(_tree.Core, _tree.Civ.CivilizationId,
                Showcase.HullRole.Fighter, Techs(world), id)?.Price
            ?? TechLoadoutSelector.Select(_tree.Core, _tree.Civ.CivilizationId, Showcase.HullRole.Scout, Techs(world), id)?.Price)
            .OfType<long>().DefaultIfEmpty(0).Min();
        ProtectedReserve = (int)Math.Min(int.MaxValue, 2 * fighter + 2L * (miner?.Price ?? 0));
        int budget = Math.Max(0, world.Wallet - ProtectedReserve);
        // One aggregate in-flight ledger prevents each parallel job spending the
        // same stale wallet. BUCKET_STATUS alone is never a wallet debit ack.
        if (_pendingDebit > 0)
        {
            if (world.WalletDebited - _debitAtSend >= _pendingDebit || world.Wallet <= _walletAtSend - _pendingDebit
                || world.DonationSettled - _settledAtSend >= _pendingDebit)
            {
                long spent = Math.Clamp(world.WalletDebited - _debitAtSend, 0, _pendingDebit);
                if (spent == 0 && world.Wallet < _walletAtSend)
                    spent = Math.Min(_pendingDebit, (long)_walletAtSend - world.Wallet);
                // Only observed spending earns coverage credit. A refused or
                // partly refunded request cannot inflate the research budget.
                long tailSpent = Math.Min(spent, _pendingTail);
                // With a partial refund, conservatively attribute accepted money
                // to coverage first. Never manufacture opening credit.
                _openingSpent += spent - tailSpent; _tailSpent += tailSpent;
                _pendingDebit = _pendingTail = 0;
                foreach (var work in _portfolioWork.Values) work.PendingWallet = null;
            }
            else
            {
                budget = 0;
                // Guaranteed self-debit/refund MONEY_CHANGE observations settle
                // refusals as well. Elapsed time alone never releases this ledger.
            }
        }
        var pending = _plan.Steps.Select((step, index) => (Step: step, Index: index))
            .Where(p => !completed.Contains((p.Step.Action, p.Step.Id))).ToList();
        // Repair only infrastructure actually delivered, independently of research.
        // Use current successors and currently observed local/global gates.
        var repairs = _builtStations.Append(_tree.Civ.InitialStationTypeId).Distinct()
            .Where(id => !HasStation(world, id)).Select(id => _tree.Core.StationTypes[id])
            .Select(s => SuccessorUpgrade.StationSuccessor(_tree.Core, s, world.Developments))
            .DistinctBy(s => s.StationTypeId).Select(s => new ResearchStep { Action = "station", Id = s.StationTypeId,
                Name = s.Name, AsteroidClass = (short)(s.AabmBuild & -s.AabmBuild) }).ToArray();
        var candidates = repairs.Select(s => (Step: s, Index: -1)).Concat(pending).DistinctBy(p => (p.Step.Action, p.Step.Id)).ToArray();
        bool Funded(ResearchStep s) => _buckets.Any(b => Fundable(b) && (s.Action == "develop" && b.DevelopmentId == s.Id
            && b.ItemType == BucketItemType.BitDevelopment || s.Action == "station" && b.StationTypeId == s.Id
            && b.ItemType == BucketItemType.BitStationType) && world.FundingByBucket.GetValueOrDefault(b.Id)?.Money > 0);
        int research = pending.Count(p => p.Step.Action == "develop" && Funded(p.Step));
        int construction = pending.Count(p => p.Step.Action == "station" && (Funded(p.Step)
            || _portfolioWork.TryGetValue(("station", p.Step.Id), out var w) && w.Constructor is not null));
        int delivery = 0, repairCount = 0;
        bool openingBlocked = pending.Any(p => p.Index < opening) && !pending.Where(p => p.Index < opening).Any(p =>
            CanBuy(world, _tree.Find(p.Step.Action, p.Step.Id)) && (p.Step.Action != "station" || Funded(p.Step)
                || world.Rocks.Any(r => (r.Available || r.Building) && (r.Capabilities & _tree.Find("station", p.Step.Id).AsteroidMask) != 0)));
        // Restore a lost income source from its reserve; no unobserved ore assumed.
        bool replaceMiner = miner is not null && world.Units.Count(u => u.Alive
            && _tree.Core.DroneTypes.TryGetValue(u.DroneType, out var d) && d.PilotType == Ship.PilotTypeMiner) < 2
            && world.Rocks.Any(r => r.Ore > 0 && (r.Capabilities & DataAsteroidIGC.AabmMineHe3) != 0);
        if (replaceMiner)
        {
            var n = _tree.Find("drone", miner!.DroneTypeId);
            candidates = new[] { (Step: new ResearchStep { Action = "drone", Id = n.Id, Name = n.Name }, Index: -2) }
                .Concat(candidates.Where(c => c.Step.Action != "drone" || c.Step.Id != n.Id)).ToArray();
            budget = _pendingDebit > 0 ? 0 : Math.Max(0, world.Wallet - (int)Math.Min(int.MaxValue, 2 * fighter));
        }
        ResearchStep? displayed = null; Work? displayedWork = null;
        var visited = new HashSet<(string, short)>();
        foreach (var (authored, index) in candidates)
        {
            var step = authored;
            if (step.Action == "station")
            {
                var s = SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[step.Id], world.Developments);
                // A researched successor must not orphan an already issued
                // constructor or its reservation. Finish that delivery first.
                var inFlight = _portfolioWork.FirstOrDefault(p => p.Key.Kind == "station"
                    && (p.Value.Constructor is { } id && world.Units.Any(u => u.Id == id && u.Alive)
                        || p.Value.Rock is { } rock && world.Rocks.Any(r => r.Id == rock && r.Building))
                    && SuccessorUpgrade.StationSuccessor(_tree.Core, _tree.Core.StationTypes[p.Key.Id], world.Developments).StationTypeId == s.StationTypeId);
                if (inFlight.Value is not null) s = _tree.Core.StationTypes[inFlight.Key.Id];
                step = new() { Action = "station", Id = s.StationTypeId, Name = s.Name,
                    AsteroidClass = (short)(s.AabmBuild & -s.AabmBuild) };
            }
            var key = (step.Action, step.Id);
            if (!visited.Add(key)) continue;
            if (Done(step) && index != -2) continue;
            var node = _tree.Find(step.Action, step.Id);
            if (!_portfolioWork.TryGetValue(key, out var work)) _portfolioWork[key] = work = new();
            work.Before ??= world.Units.Select(u => u.Id).ToHashSet();
            if (double.IsNaN(work.Began)) work.Began = world.Time;
            var bucket = _buckets.FirstOrDefault(b => step.Action switch
            {
                "develop" => b.ItemType == BucketItemType.BitDevelopment && b.DevelopmentId == step.Id,
                "station" => b.ItemType == BucketItemType.BitStationType && b.StationTypeId == step.Id,
                "drone" => b.ItemType == BucketItemType.BitDroneType && b.DroneTypeId == step.Id,
                _ => false,
            });
            string state = "waiting for prerequisites";
            if (bucket is null || !CanBuy(world, node) && !(step.Action == "station" && work.Constructor is { } building
                && world.Units.Any(u => u.Id == building && u.Alive))) { jobs.Add(new(index == -1 ? "repair" : step.Action, step.Id, state, null, null)); continue; }
            if (!Fundable(bucket))
            { jobs.Add(new(step.Action, step.Id, "deferred: native bucket group/price/time gate", null, null)); continue; }
            var funding = world.FundingByBucket.GetValueOrDefault(bucket.Id) ?? new Funding(0, 0);
            work.SeenMoney = funding.Money;
            bool active = funding.Money > 0;
            if (step.Action == "station")
            {
                var builder = world.Units.FirstOrDefault(u => u.Alive && u.StationType == step.Id
                    && !_portfolioWork.Any(p => p.Key != key && p.Value.Constructor == u.Id));
                if (builder is not null) work.Constructor = builder.Id;
                bool shell = work.Rock is { } r && world.Rocks.Any(a => a.Id == r && a.Building);
                if (work.Constructor is not null || shell) active = true;
                if (!active && (construction >= ParallelConstructionLimit || index == -1 && repairCount >= 1)) continue;
                if (work.Rock is null)
                {
                    var rock = PickConstructionRock(world.Rocks.Where(r => _rockCooldown.GetValueOrDefault(r.Id) <= world.Time),
                        node.AsteroidMask, _reservations.Keys);
                    if (rock is not null) { work.Rock = rock.Id; _reservations[rock.Id] = Side; work.Began = world.Time; }
                }
                if (work.Rock is null) { jobs.Add(new(index == -1 ? "repair" : "construction", step.Id, "deferred: no side-observed compatible rock", work.Constructor, null)); continue; }
                if (!active) construction++; if (index == -1) repairCount++;
                if (shell) state = "construction shell";
                else if (work.Constructor is { } constructor && builder is not null)
                {
                    var rock = world.Rocks.First(r => r.Id == work.Rock);
                    if (builder.Sector >= 0 && world.Time - work.LastOrder >= 5)
                    { commands.Add(new(ConstructorOrderKind(rock.Sector, builder.Sector), constructor, Target: rock.Id)); work.LastOrder = world.Time; }
                    state = "constructor delivery";
                }
                else state = funding.Money >= bucket.Price ? "constructor production" : "funding";
            }
            else if (step.Action == "develop")
            { if (!active && research >= ParallelResearchLimit) continue; if (!active) research++; state = funding.Money >= bucket.Price ? "maturing" : "funding"; }
            else
            {
                if (delivery++ >= 1 || world.Units.Count(u => u.Alive && u.DroneType == step.Id) >= MaxDronesPerType) continue;
                state = funding.Money >= bucket.Price ? "fleet delivery" : "funding";
            }
            displayed ??= step; displayedWork ??= work;
            jobs.Add(new(index == -1 ? "repair" : step.Action == "station" ? "construction" : step.Action == "drone" ? "fleet-delivery" : "research",
                step.Id, state, work.Constructor, work.Rock));
            bool tail = index >= opening && !openingBlocked;
            // While an opening remains, coverage uses a token budget earned by
            // opening investment. Once it completes the entire reachable tail runs.
            long tailAllowance = share == 0 ? 0 : (long)Math.Floor(_openingSpent * share / (1 - share)) - _tailSpent - _pendingTail;
            int available = tail && pending.Any(p => p.Index < opening) ? (int)Math.Min(budget, Math.Max(0, tailAllowance)) : budget;
            if (state != "funding" || available <= 0 || funding.Money >= bucket.Price) continue;
            int amount = DonationAmount(available, bucket.Price, funding.Money);
            if (amount <= 0) continue;
            commands.Add(new("donate", bucket.Id, amount)); budget -= amount;
            work.PendingWallet = world.Wallet; work.PendingFunding = funding.Money;
            if (_pendingDebit == 0) { _walletAtSend = world.Wallet; _debitAtSend = world.WalletDebited; _settledAtSend = world.DonationSettled; }
            _pendingDebit += amount;
            if (tail) _pendingTail += amount;
        }
        _index = _plan.Steps.TakeWhile(s => completed.Contains((s.Action, s.Id))).Count();
        PortfolioJobs = jobs;
        Publish(world, displayed, displayedWork, $"portfolio: research={research} construction={construction} repair={repairCount} delivery={delivery}; reserve={ProtectedReserve}; pendingDebit={_pendingDebit}",
            0, null);
        if (Telemetry is { } report) Telemetry = report with
        {
            ProgressPercent = _plan.Steps.Count == 0 ? 100 : 100d * _plan.Steps.Count(s => completed.Contains((s.Action, s.Id))) / _plan.Steps.Count,
            Action = displayed?.Action ?? (pending.Count > 0 ? "replan" : "complete"),
            Name = displayed?.Name ?? pending.FirstOrDefault().Step?.Name ?? "",
            Jobs = jobs.OrderBy(j => j.State.StartsWith("waiting", StringComparison.Ordinal) || j.State.StartsWith("deferred", StringComparison.Ordinal))
                .Take(24).ToArray(),
        };
        return commands;
    }
}
