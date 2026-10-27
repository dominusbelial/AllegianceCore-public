using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class ResearchCommander
{
    // P832b strategy is a port policy, not a native AI rule. All observations
    // and actions use the existing human client's mission/wallet/bucket path.
    internal const double ProsperityHorizonSeconds = 300;
    private int? _prosperityWallet;
    private double _prosperityStart = double.NaN, _prosperityIncome, _prosperitySavings;
    private readonly Work _prosperityWork = new();
    private long? _defenderPrice;

    internal IReadOnlyList<Command> Tick(Snapshot world)
    {
        // Preserve the original controller, including its trace and telemetry,
        // byte for byte when this goal is absent (also before MISSIONDEF).
        if (world.Goals?.IsProsperityGame != true) return TickPlan(world);
        if (!world.Started) return TickPlan(world);

        var miner = _buckets.Where(b => b.DroneType is { PilotType: Ship.PilotTypeMiner }
                && CanBuy(world, _tree.Find("drone", b.DroneTypeId)))
            .OrderBy(b => b.Price).ThenBy(b => b.Id).FirstOrDefault();
        int miners = world.Units.Count(u => u.Alive && _tree.Core.DroneTypes.TryGetValue(u.DroneType, out var d)
            && d.PilotType == Ship.PilotTypeMiner);
        _defenderPrice ??= world.Stations.Select(id => TechLoadoutSelector.Select(_tree.Core, _tree.Civ.CivilizationId,
                Showcase.HullRole.Fighter, Techs(world), id)?.Price)
            .OfType<long>().DefaultIfEmpty(0).Min();
        // Two complete legal fighter loadouts plus two replacement miners.
        int defence = (int)Math.Min(int.MaxValue, 2 * _defenderPrice.Value);
        int reserve = (int)Math.Min(int.MaxValue, (long)defence + 2L * (miner?.Price ?? 0));
        int surplus = Math.Max(0, world.Wallet - reserve);
        if (double.IsNaN(_prosperityStart))
        {
            _prosperityStart = world.Time;
            _prosperitySavings = surplus / 2d;
        }
        if (_prosperityWallet is { } previous && world.Wallet > previous)
        {
            int credit = world.Wallet - previous;
            _prosperityIncome += credit;
            _prosperitySavings += credit * .5;
        }
        _prosperityWallet = world.Wallet;
        _prosperitySavings = Math.Min(_prosperitySavings, surplus);

        // Restore income before racing. Only buy miners where observed He3
        // remains; native miner mining/delivery supplies the actual credits
        // (Igc/shipIGC.cpp:1988,2166-2213). The normal plan supplies He3 tech.
        var goal = _buckets.FirstOrDefault(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        if (goal is null) return TickReservedPlan(world, surplus);
        // Igc/missionigc.h:790: multiplier times the loaded win-money constant.
        int price = (int)(world.Goals.TeamMoney * _tree.Core.Constants.FloatConstants[12]);
        long funded = world.FundingByBucket.GetValueOrDefault(goal.Id)?.Money ?? 0;
        double rate = _prosperityIncome / Math.Max(1, world.Time - _prosperityStart);
        bool grow = miners < 2 && miner is not null && world.Rocks.Any(r => r.Ore > 0
            && (r.Capabilities & DataAsteroidIGC.AabmMineHe3) == DataAsteroidIGC.AabmMineHe3);
        var bucket = grow ? miner! : goal;
        var funding = world.FundingByBucket.GetValueOrDefault(bucket.Id) ?? new Funding(0, 0);
        int cost = grow ? bucket.Price : price;
        bool near = Math.Max(0, price - funded - surplus) <= rate * .5 * ProsperityHorizonSeconds;
        int budget = grow ? Math.Max(0, world.Wallet - defence)
            : near ? (price - funded <= surplus ? surplus : (int)_prosperitySavings) : 0;
        var work = _prosperityWork;
        // Require the wallet debit even after BUCKET_STATUS arrives. A funding
        // acknowledgement alone must not authorize spending a stale wallet.
        if (work.PendingWallet is { } pending)
        {
            bool acknowledged = world.Wallet <= pending - work.PendingFunding;
            if (!acknowledged && world.Time - work.LastDonation < 5)
            {
                Publish(world, new() { Action = grow ? "drone" : "develop", Id = grow ? bucket.DroneTypeId : bucket.DevelopmentId,
                    Name = bucket.Name }, null, "prosperity: awaiting donation acknowledgement", 0, null);
                return [];
            }
            work.PendingWallet = null;
        }
        // Native team-money development bypasses its required-tech mask
        // (Igc/sideigc.h:237-243); ordinary miner gates were checked above.
        if (budget > 0 && funding.Money < cost)
        {
            int amount = (int)Math.Min(budget, cost - funding.Money);
            work.PendingWallet = world.Wallet; work.PendingFunding = amount; work.LastDonation = world.Time;
            if (!grow) _prosperitySavings = Math.Max(0, _prosperitySavings - amount);
            Publish(world, new() { Action = grow ? "drone" : "develop", Id = grow ? bucket.DroneTypeId : bucket.DevelopmentId,
                Name = bucket.Name }, null, grow ? "prosperity: growing/replacing miners" : "prosperity: funding economic victory",
                price <= 0 ? 1 : (double)funded / price, null);
            return [new("donate", bucket.Id, amount)];
        }
        // A fully funded miner is still building. Don't let the plan fund the
        // same repeating bucket while waiting for its production observation.
        if (grow)
        {
            Publish(world, null, null, "prosperity: waiting for miner production or income", 0, null);
            return [];
        }
        // Bank half observed income while the unchanged plan spends the rest
        // on He3 research, stations and combat tech. Savings cannot be drained
        // by repeated ticks without a new credit. Full funding awaits native
        // DEV_COMPLETED/GAME_OVER, never elapsed-time inference.
        return TickReservedPlan(world, Math.Max(0, surplus - (int)Math.Ceiling(_prosperitySavings)));
    }

    private IReadOnlyList<Command> TickReservedPlan(Snapshot world, int budget)
    {
        var commands = TickPlan(world with { Wallet = budget });
        // Restrict spending without reporting the budget as the real wallet.
        if (Telemetry is { } report) Telemetry = report with { Money = world.Wallet };
        return commands;
    }
}
