using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;
using Allegiance.Core.Model;
using Allegiance.Core.Strategy;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Bounded purchase exploration. Per-side/per-epoch state, serialized
/// with selections. Capability grants/firing/completions are separate evidence.</summary>
internal static class CivCoverage
{
    private sealed class State(string epoch)
    {
        internal readonly string Epoch = epoch;
        internal long Selected, Covered, StrategyMoney, CoverageMoney;
        internal readonly HashSet<short> Hulls = new(), Parts = new();
        internal readonly Dictionary<string, long> Duties = new();
        internal readonly Dictionary<short, (bool Coverage, long Price)> Pending = new();
    }
    private static readonly object Sync = new();
    private static readonly Dictionary<short, State> States = new();
    private static readonly Dictionary<short, (short Side, string Epoch)> Pilots = new();
    internal static void Reset() { lock (Sync) { States.Clear(); Pilots.Clear(); } }
    internal static void ObserveGrant(ConnectionSession peer, short hull, IReadOnlyList<ExpandedPartData> parts, bool launched)
    {
        var world = peer.World;
        if (!CivExecutionPlan.TryRead(world.ResearchBriefing?.Plan, out _, out _) || world.MySideId is not { } side
            || world.MyShipId is not { } pilot) return;
        string epoch = world.ResearchBriefing!.Epoch;
        ObserveAccepted(side, epoch, pilot, hull, parts, launched);
    }
    internal static void ObserveAccepted(short side, string epoch, short pilot, short hull,
        IReadOnlyList<ExpandedPartData> parts, bool launched)
    {
        lock (Sync)
        {
            var state = StateFor(side, epoch);
            Pilots[pilot] = (side, epoch);
            if (launched)
            {
                bool coverage = state.Pending.Remove(pilot, out var pending) && pending.Coverage;
                state.Hulls.Add(hull);
                var core = Catalog.RepresentativeCore();
                long price = Math.Max(0, core.HullTypes[hull].Price) + parts.Sum(p =>
                    TechLoadoutSelector.PartPrice(TechLoadoutSelector.Part(core, p.PartId), p.Amount));
                if (coverage) state.CoverageMoney += price; else state.StrategyMoney += price;
            }
            foreach (var p in parts)
                if (TechLoadoutSelector.Part(Catalog.RepresentativeCore(), p.PartId).EquipmentType is EquipmentType.Shield
                    or EquipmentType.Afterburner or EquipmentType.Cloak) state.Parts.Add(p.PartId);
        }
    }
    internal static void ObserveCrewed(ConnectionSession peer)
    {
        var world = peer.World;
        if (world.MySideId is not { } side || world.MyShipId is not { } pilot || world.ResearchBriefing is not { } briefing) return;
        lock (Sync)
            if (States.TryGetValue(side, out var state) && state.Epoch == briefing.Epoch)
                state.Pending.Remove(pilot); // actual boarding consumes a crew seat, no invented fighter purchase
    }
    private static State StateFor(short side, string epoch)
    {
        if (States.TryGetValue(side, out var state) && state.Epoch == epoch) return state;
        foreach (var pilot in Pilots.Where(p => p.Value.Side == side).Select(p => p.Key).ToArray()) Pilots.Remove(pilot);
        return States[side] = new(epoch);
    }
    // Coverage priority uses actual grants and driver use attempts. Exact server
    // firing/hit/completion evidence is emitted independently, never inferred here.
    internal static void ObserveUse(short pilot, short part)
    {
        lock (Sync)
            if (Pilots.TryGetValue(pilot, out var key) && States.TryGetValue(key.Side, out var state) && state.Epoch == key.Epoch)
                state.Parts.Add(part);
    }

    internal static int CrewSlots(int pilots, double share) => (int)Math.Floor(pilots * share);
    internal static bool CrewAllowed() => Environment.GetEnvironmentVariable("ALLEGIANCE_GUNNERS")?.Trim().ToLowerInvariant()
        is not ("0" or "off" or "false");

    internal static long ReservedFootprint(StaticCore core, TechLoadoutSelector.Selection fit)
    {
        // Native purchase can advance to a newly unlocked successor between
        // shop observation and ACK. Reserve the public chain's maximum price.
        long price = 0; var hulls = new HashSet<short>(); short hull = fit.HullId;
        while (hulls.Add(hull) && core.HullTypes.TryGetValue(hull, out var h))
        { price = Math.Max(price, Math.Max(0, h.Price)); hull = h.SuccessorHullId; }
        foreach (var part in fit.Parts)
        {
            long maximum = 0; var seen = new HashSet<short>(); short id = part.PartId;
            while (id >= 0 && seen.Add(id) && (core.PartTypes.ContainsKey(id) || core.Launchers.ContainsKey(id)))
            {
                var p = TechLoadoutSelector.Part(core, id);
                maximum = Math.Max(maximum, TechLoadoutSelector.PartPrice(p, part.Amount)); id = p.SuccessorPartId;
            }
            price += maximum;
        }
        return price;
    }

    internal static TechLoadoutSelector.Selection Select(BotManager.LoadoutShop shop, double share,
        IReadOnlyList<CivLoadoutPolicy.Rated> candidates, Showcase.HullRole role)
    {
        lock (Sync)
        {
            var state = StateFor(shop.Side, shop.Epoch);
            // An abandoned/refused exploratory request cannot earn strategic
            // credit. Conservatively retain its reserved footprint on a retry;
            // actual boarding releases a never-purchased fighter reservation.
            if (state.Pending.Remove(shop.Pilot, out var prior) && prior.Coverage) state.CoverageMoney += prior.Price;
            bool coverage = state.Covered + 1 <= Math.Floor((state.Selected + 1) * share);
            long allowance = share > 0 ? (long)Math.Floor(state.StrategyMoney * share / (1 - share)) - state.CoverageMoney
                - state.Pending.Where(p => p.Key != shop.Pilot && p.Value.Coverage).Sum(p => p.Value.Price) : 0;
            var selected = candidates[0].Loadout;
            if (coverage)
            {
                // Include the established legal variety builder to expose guns,
                // missiles and utility parts that a power beam naturally prunes.
                var variety = new TechLoadoutSelector.VarietyState(shop.Side, 1001, state.Selected,
                    state.Hulls.ToDictionary(id => id, _ => 1), state.Parts.ToDictionary(id => id, _ => 1));
                var extra = TechLoadoutSelector.SelectVaried(Catalog.RepresentativeCore(), shop.Civ, role,
                    shop.Techs, shop.DockType, variety, shop.Target, Math.Min(shop.Budget, Math.Max(0, allowance)));
                var pool = candidates.Select(c => c.Loadout).Concat(extra is null ? [] : new[] { extra }).ToList();
                if (shop.FleetSize >= 7)
                {
                    var core = Catalog.RepresentativeCore();
                    void Add(TechLoadoutSelector.Selection? fit, string duty)
                    { if (fit is not null && fit.Price <= allowance) pool.Add(fit with { CoverageDuty = duty }); }
                    long cap = Math.Min(shop.Budget, Math.Max(0, allowance));
                    Add(TechLoadoutSelector.SelectSupport(core, shop.Civ, shop.Techs, shop.DockType, cap), "repair");
                    var layer = TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Scout, shop.Techs, shop.DockType,
                        budget: cap, preferMines: true);
                    if (layer?.Parts.Any(p => Showcase.IsMineLauncher(core, p.PartId)) == true) Add(layer, "mines");
                    var scout = TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Scout, shop.Techs, shop.DockType, budget: cap);
                    if (scout?.Parts.Any(p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.ProbeLauncher) == true)
                        Add(scout, "probes");
                    var rescue = TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Scout, shop.Techs, shop.DockType,
                        budget: cap, rescueProbe: true);
                    if (rescue?.Parts.Any(p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.ProbeLauncher launcher
                        && core.ProbeTypes.TryGetValue(launcher.ExpendableTypeId, out var probe) && (probe.EabmCapabilities & 0x6000) != 0) == true)
                        Add(rescue, "rescue");
                    else if (TechLoadoutSelector.PickRescueProbeLauncher(core, shop.Techs) is { } rescueId)
                    {
                        // A high-scoring scout can lack a dispenser. Duties are
                        // hardware capabilities: try other complete legal fits
                        // rather than making a reachable rescue probe disappear.
                        var launcher = TechLoadoutSelector.Part(core, rescueId);
                        foreach (var candidate in candidates)
                        {
                            var fit = candidate.Loadout; var hull = core.HullTypes[fit.HullId];
                            if (!hull.CanMountPart(launcher, 0)) continue;
                            var parts = fit.Parts.Where(p => TechLoadoutSelector.Part(core, p.PartId).EquipmentType != EquipmentType.Dispenser)
                                .Append(new ExpandedPartData(rescueId, 1, 0, ExpandedPartData.FullyMounted)).ToArray();
                            long price = Math.Max(0, hull.Price) + parts.Sum(p => TechLoadoutSelector.PartPrice(TechLoadoutSelector.Part(core, p.PartId), p.Amount));
                            if (price > cap || !TechLoadoutSelector.Legal(core, shop.Civ, shop.Techs, shop.DockType, fit.HullId, parts)) continue;
                            Add(fit with { Parts = parts, Price = price, Reason = "coverage rescue-capable complete fit" }, "rescue");
                            break;
                        }
                    }
                    var armed = candidates.FirstOrDefault(c => c.Loadout.Parts.Any(p =>
                        TechLoadoutSelector.Part(core, p.PartId).EquipmentType == EquipmentType.ChaffLauncher))?.Loadout;
                    Add(armed, "chaff");
                    var ripper = candidates.FirstOrDefault(c => !core.HullTypes[c.Loadout.HullId].HasCapability(DataHullTypeIGC.HabmNoRipcord))?.Loadout;
                    if (shop.RemoteAnchor) Add(ripper, "ripcord");
                    if (shop.Conquest) Add(TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Fighter, shop.Techs,
                        shop.DockType, budget: cap, boarding: true), "boarding");
                    // Two slots are needed for a carrier plus a gunner. The
                    // explicit operator OFF setting remains authoritative.
                    if (CrewSlots(shop.FleetSize, share) >= 2 && CrewAllowed())
                    {
                        var carrier = TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Heavy, shop.Techs, shop.DockType, budget: cap);
                        if (carrier is not null && carrier.Parts.Any(p => p.MountId >= core.HullTypes[carrier.HullId].MaxFixedWeapons
                            && TechLoadoutSelector.Part(core, p.PartId).EquipmentType == EquipmentType.Weapon))
                            Add(carrier, "turret");
                        if (shop.FriendlyTurret) Add(candidates[0].Loadout, "gunner");
                    }
                }
                var available = pool.Where(c => c.Parts.All(p => TechLoadoutSelector.Part(Catalog.RepresentativeCore(), p.PartId).GroupId < 128))
                    .Where(c => ReservedFootprint(Catalog.RepresentativeCore(), c) <= allowance)
                    .OrderBy(c => c.CoverageDuty is null ? long.MaxValue : state.Duties.GetValueOrDefault(c.CoverageDuty))
                    .ThenByDescending(c => (!state.Hulls.Contains(c.HullId) ? 1 : 0)
                        + c.Parts.Select(p => p.PartId).Distinct().Count(id => !state.Parts.Contains(id))).ThenBy(c => c.HullId).ToArray();
                if (available.FirstOrDefault() is { } unused && (unused.CoverageDuty is not null || !state.Hulls.Contains(unused.HullId)
                    || unused.Parts.Any(p => !state.Parts.Contains(p.PartId)))) selected = unused;
                else coverage = false;
            }
            state.Selected++;
            if (coverage)
            {
                state.Covered++;
                if (selected.CoverageDuty is { } duty) state.Duties[duty] = state.Duties.GetValueOrDefault(duty) + 1;
            }
            state.Pending[shop.Pilot] = (coverage, coverage ? ReservedFootprint(Catalog.RepresentativeCore(), selected) : selected.Price);
            // A choice is explicitly not a grant or a launch.
            Console.WriteLine(FormattableString.Invariant($"[civ-coverage] cookie={shop.Cookie} epoch={shop.Epoch} side={shop.Side} pilot={shop.Pilot} stage=selected mechanism={selected.CoverageDuty ?? "hardware"} duty={role} hull={selected.HullId} parts={string.Join(',', selected.Parts.Select(p => p.PartId))} coverage={coverage.ToString().ToLowerInvariant()} selected={state.Selected} covered={state.Covered} coverageMoney={state.CoverageMoney} pendingCoverageMoney={state.Pending.Values.Where(p => p.Coverage).Sum(p => p.Price)} strategyMoney={state.StrategyMoney} share={share:R}"));
            return selected with { Reason = $"{selected.Reason}; coverage={coverage}; share={share}" };
        }
    }
}
