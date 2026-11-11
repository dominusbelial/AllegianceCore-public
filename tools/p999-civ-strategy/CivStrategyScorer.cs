using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Tools.CivStrategy;

/// <summary>
/// P999 offline policy experiment. All legality comes from ResearchState and
/// ResearchPlanCatalog; utility is a modern heuristic, never a native rule.
/// No clocks, files, network, global caches, mutations of the core, or live wiring.
/// </summary>
public static class CivStrategyScorer
{
    public sealed record Power(double Skirmish, double Siege, double Recon, double Economy, double Support)
    {
        public double[] Values => [Skirmish, Siege, Recon, Economy, Support];
    }
    public sealed record Opportunity(string Kind, short Id, string Name, double Score,
        long Price, long Seconds, int Steps, bool Sliced, Power Power);
    public sealed record Result(ResearchPlan Plan, string Profile, Opportunity[] Opportunities,
        Power InitialPower, Power FinalPower, short[] OpponentDefenses);
    private sealed record Route(ResearchNode Target, ResearchStep[] Steps, Opportunity Opportunity);

    /// <param name="opponentCivs">Optional opponents; an empty list uses all other
    /// loaded civs. Their starting hull, fitting shield and station defense
    /// classes are used; no hidden in-match tech or positions are consulted.</param>
    public static Result Generate(ResearchTree tree, string coreName, long seed, int variant = 0,
        IReadOnlyList<short>? opponentCivs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(variant);
        if (variant > 2) throw new ArgumentOutOfRangeException(nameof(variant));
        var weights = variant switch
        {
            0 => new[] { .50, .20, .10, .15, .05 },
            1 => new[] { .25, .50, .05, .15, .05 },
            _ => new[] { .30, .15, .10, .40, .05 }
        };
        string profile = new[] { "skirmish", "siege", "economy" }[variant];
        var opponents = (opponentCivs ?? tree.Core.Civilizations.Keys.Where(id => id != tree.Civ.CivilizationId).ToArray())
            .Distinct().Order().Select(id => new ResearchTree(tree.Core, id)).ToArray();
        var defenses = opponents.SelectMany(t =>
        {
            var unlocks = new ResearchState(t).Unlocks;
            var hulls = unlocks.Where(u => u.Kind == "hull").Select(u => t.Core.HullTypes[u.Id]).Where(PlayerHull).ToArray();
            var parts = unlocks.Where(u => u.Kind == "part").Select(u => t.Parts.Single(p => p.PartId == u.Id));
            return hulls.Select(h => (short)h.DefenseType).Concat(parts.Where(p => p.Type is DataPartTypeIGC.Shield
                && hulls.Any(h => h.CanMountPart(p, 0))).Select(p => (short)((DataPartTypeIGC.Shield)p.Type).DefenseType));
        })
            .Distinct().Order().ToArray();
        var stationDefenses = opponents.Select(t => t.Core.StationTypes[t.Civ.InitialStationTypeId])
            .SelectMany(s => new[] { (short)s.DefenseTypeArmor, (short)s.DefenseTypeShield }).Distinct().Order().ToArray();
        var initial = Evaluate(tree, [], defenses, stationDefenses);
        var baseline = tree.Generate(coreName, prioritizeResearch: true);
        var envelope = Evaluate(tree, baseline.Steps, defenses, stationDefenses);
        // Normalize each dimension against attainable core power. In particular,
        // siege starts at zero for many civs: an arbitrary log(1 + raw DPS)
        // would make every profile choose the same first bomber research.
        var reference = initial.Values.Select((value, i) => Math.Max(1, Math.Max(value, .10 * envelope.Values[i]))).ToArray();
        double fundingScale = 10000 * Math.Max(.1, 1 + tree.Civ.BonusMoney);
        var routes = new List<Route>();
        var prefix = new List<ResearchStep>();
        foreach (var step in baseline.Steps)
        {
            prefix.Add(step);
            if (step.Action == "drone") continue; // purchased in the coverage tail
            var target = tree.Find(step.Action, step.Id);
            var route = Slice(tree, prefix);
            bool sliced = TryReplay(tree, route, out _);
            if (!sliced) route = prefix.ToArray(); // conservative, exact-valid fallback
            var power = Evaluate(tree, route, defenses, stationDefenses);
            long price = route.Sum(s => (long)tree.Find(s.Action, s.Id).Price);
            long seconds = route.Sum(s => (long)tree.Find(s.Action, s.Id).Seconds);
            double score = Gain(initial, power, weights, reference) / (1 + Math.Max(0, price) / fundingScale + seconds / 300.0);
            // Small seeded preference varies near ties without replacing utility.
            score *= .98 + .04 * Jitter(seed, variant, target.Kind, target.Id);
            routes.Add(new(target, route, new(target.Kind, target.Id, target.Name, score,
                price, seconds, route.Length, sliced, power)));
        }
        var ranked = routes.OrderByDescending(r => r.Opportunity.Score).ThenBy(r => r.Target.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Target.Id).ToArray();
        var state = new ResearchState(tree);
        var result = new List<ResearchStep>();
        var done = new HashSet<(string, short)>();
        // Lead with one explicit, affordable power spike. Its prerequisite route
        // can cross several stations; the target is selected from loaded specs.
        if (ranked.FirstOrDefault(r => r.Opportunity.Score > 0) is { } primary)
            foreach (var step in primary.Steps) Add(tree.Find(step.Action, step.Id));

        // Coverage is a tail, not a reason to fund a utility constructor before
        // the selected opening. Remaining targets keep their opening opportunity
        // scores; this bounded prototype does not solve an online economy.
        // Every still-reachable node is eventually exercised; obsolete alternatives
        // are naturally retired by the existing ResearchState availability gate.
        var pending = tree.Nodes.Where(n => !done.Contains((n.Kind, n.Id))).ToList();
        var scores = ranked.ToDictionary(r => (r.Target.Kind, r.Target.Id), r => r.Opportunity.Score);
        var covered = result.Select(s => s.Action).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var eligible = pending.Where(state.CanApply).ToArray();
            if (eligible.Length == 0) break;
            var next = eligible.OrderByDescending(n => scores.GetValueOrDefault((n.Kind, n.Id)))
                .ThenBy(n => covered.Contains(n.Kind) ? 1 : 0)
                .ThenBy(n => n.Price).ThenBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Id).First();
            Add(next); covered.Add(next.Kind); pending.Remove(next);
        }
        var plan = new ResearchPlan { Core = coreName, CoreVersion = tree.Core.Version,
            CivId = tree.Civ.CivilizationId, Name = $"offline-{profile}-{seed}", Steps = result };
        ResearchPlanCatalog.Validate(tree, coreName, plan);
        return new(plan, profile, ranked.Select(r => r.Opportunity).ToArray(), initial,
            Evaluate(tree, result, defenses, stationDefenses), defenses);

        void Add(ResearchNode node)
        {
            if (!state.CanApply(node)) throw new InvalidDataException($"offline route cannot apply {node.Kind} {node.Id}");
            var before = state.Unlocks;
            var step = Step(node);
            state.Apply(step);
            step.ExpectedUnlocks = state.Unlocks.Except(before).ToList();
            result.Add(step); done.Add((node.Kind, node.Id));
        }
    }

    private static ResearchStep Step(ResearchNode node) => new()
    {
        Action = node.Kind, Id = node.Id, Name = node.Name,
        AsteroidClass = node.Kind == "station" ? (short)(node.AsteroidMask & -node.AsteroidMask) : null
    };

    // Backward slice a known-valid baseline prefix by prerequisite bit providers.
    // A provider's own required bits enter the worklist. This deliberately
    // over-approximates station-local availability: exact replay rejects a slice
    // that would combine local masks or omit a successor constraint.
    private static ResearchStep[] Slice(ResearchTree tree, IReadOnlyList<ResearchStep> prefix)
    {
        var need = new byte[50];
        var chosen = new List<ResearchStep>();
        for (int i = prefix.Count - 1; i >= 0; i--)
        {
            var n = tree.Find(prefix[i].Action, prefix[i].Id);
            if (i != prefix.Count - 1 && !Enumerable.Range(0, 50).Any(b => (need[b] & (n.Effects[b] | n.Local[b])) != 0)) continue;
            chosen.Add(Step(n));
            for (int b = 0; b < 50; b++) need[b] = (byte)((need[b] & ~(n.Effects[b] | n.Local[b])) | n.Required[b]);
        }
        chosen.Reverse();
        return chosen.ToArray();
    }

    private static bool TryReplay(ResearchTree tree, IEnumerable<ResearchStep> steps, out ResearchState state)
    {
        state = new ResearchState(tree);
        foreach (var step in steps)
        {
            if (!state.CanApply(tree.Find(step.Action, step.Id))) return false;
            state.Apply(step);
        }
        return true;
    }

    private static double Gain(Power before, Power after, double[] weights, double[] reference) => weights
        .Select((weight, i) => weight * Math.Log((reference[i] + Math.Max(0, after.Values[i]))
            / (reference[i] + Math.Max(0, before.Values[i])))).Sum();

    private static double Jitter(long seed, int variant, string kind, short id)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant($"{seed}/{variant}/{kind}/{id}")))
            hash = unchecked((hash ^ b) * 1099511628211UL);
        return (hash >> 11) * (1.0 / (1UL << 53));
    }

    public static bool PlayerHull(DataHullTypeIGC h) => h.GroupId >= 0 &&
        !h.HasCapability(DataHullTypeIGC.HabmLifepod | DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder);

    /// <summary>Heuristic attainable power, evaluated per real station local mask.
    /// A thirty-second firing window limits ammo and energy; prices, flight and
    /// hit probabilities are approximations. This is not a combat simulator.</summary>
    public static Power Evaluate(ResearchTree tree, IReadOnlyList<ResearchStep> steps,
        short[] opponentDefenses, short[] stationDefenses)
    {
        if (!TryReplay(tree, steps, out var state)) throw new InvalidDataException("unreachable evaluation input");
        var ga = (float[])tree.Civ.GasBaseAttributes.Clone();
        foreach (var step in steps.Where(s => s.Action == "develop"))
        {
            var attributes = tree.Find(step.Action, step.Id).Attributes;
            for (int i = 0; i < ga.Length; i++) ga[i] *= attributes[i];
        }
        double skirmish = 0, siege = 0, recon = 0, support = 0;
        foreach (var station in state.Stations.Order())
        {
            var local = tree.Core.StationTypes[station].TtbmLocal;
            var techs = state.Techs;
            bool Buy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, local, techs);
            var parts = tree.Parts.Where(p => p.GroupId != byte.MaxValue && Buy(p.TtbmRequired)
                && !SuccessorUpgrade.PartIsObsolete(tree.Core, p, local, techs)).ToArray();
            foreach (var h in tree.Core.HullTypes.Values.Where(h => PlayerHull(h) && Buy(h.TtbmRequired)
                && !SuccessorUpgrade.HullIsObsolete(tree.Core, h, local, techs)))
            {
                var fitting = parts.Where(p => h.CanMountPart(p, 0)).ToArray();
                double shield = fitting.Where(p => p.Type is DataPartTypeIGC.Shield).Select(p =>
                    ((DataPartTypeIGC.Shield)p.Type).MaxStrength * ga[GlobalAttributeSet.MaxShieldShip]).DefaultIfEmpty().Max();
                double durability = Math.Max(1, h.HitPoints * ga[GlobalAttributeSet.MaxArmorShip] + shield);
                double speed = Math.Max(1, h.Speed * ga[GlobalAttributeSet.MaxSpeed]);
                double accel = Math.Max(.01, h.Thrust * ga[GlobalAttributeSet.Thrust] / Math.Max(1, h.Mass));
                double turn = h.MaxTurnRates.Take(2).DefaultIfEmpty().Average() * ga[GlobalAttributeSet.TurnRate];
                double handling = Math.Sqrt(speed / 200) * Math.Pow(accel / 50, .25)
                    * Math.Sqrt(Math.Max(.01, turn))
                    * Math.Pow(ga[GlobalAttributeSet.Signature] / Math.Max(.1, h.Signature), .25);
                double shipDps = Guns(opponentDefenses), baseDps = Guns(stationDefenses);
                // Missile magazine size and cycle matter, as does the damage matrix.
                foreach (var p in fitting)
                    if (p.Type is DataPartTypeIGC.MissileLauncher m && tree.Core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var missile))
                    {
                        double shots = Math.Min(h.CapacityMagazine / Math.Max(1.0, missile.LauncherExpendableSize),
                            30 / Math.Max(.1, missile.ReadyTime + missile.LockTime));
                        double dps = Math.Max(0, missile.Power) * ga[GlobalAttributeSet.DamageMissiles] * shots / 30;
                        // Only one magazine can be fitted. Keep the best complete
                        // gun-plus-magazine option, rather than summing launchers.
                        shipDps = Math.Max(shipDps, Guns(opponentDefenses) + dps * Rate(missile.DamageType, opponentDefenses));
                        baseDps = Math.Max(baseDps, Guns(stationDefenses) + dps * Rate(missile.DamageType, stationDefenses));
                    }
                double affordability = 1 + Math.Max(0, h.Price) / 10000.0;
                skirmish = Math.Max(skirmish, shipDps * Math.Sqrt(durability) * handling / affordability);
                siege = Math.Max(siege, baseDps * Math.Sqrt(durability) / affordability);
                recon = Math.Max(recon, h.ScannerRange * ga[GlobalAttributeSet.ScanRange] * speed / 1000
                    * ga[GlobalAttributeSet.Signature] / Math.Max(.1, h.Signature));
                double repair = parts.Where(p => p.Type is DataPartTypeIGC.Weapon && h.CanMountPart(p, 0))
                    .Select(p => (DataPartTypeIGC.Weapon)p.Type).Where(w => tree.Core.ProjectileTypes.ContainsKey(w.ProjectileTypeId))
                    .Select(w => Math.Max(0, -tree.Core.ProjectileTypes[w.ProjectileTypeId].Power) / Math.Max(.01, w.DtimeBurst))
                    .DefaultIfEmpty().Max();
                support = Math.Max(support, repair * Math.Sqrt(durability));

                double Guns(short[] defenses)
                {
                    double energy = h.MaxEnergy * ga[GlobalAttributeSet.MaxEnergy] / 30 + h.RechargeRate;
                    double ammo = Math.Max(0, (int)h.MaxAmmo) / 30.0;
                    double total = 0;
                    for (int mount = 0; mount < h.Hardpoints.Length && mount < h.MaxFixedWeapons; mount++)
                    {
                        if (!h.Hardpoints[mount].Fixed) continue;
                        var options = parts.Where(p => p.Type is DataPartTypeIGC.Weapon && h.CanMountPart(p, mount))
                            .Select(p => (DataPartTypeIGC.Weapon)p.Type).Where(w => tree.Core.ProjectileTypes.ContainsKey(w.ProjectileTypeId))
                            .Select(w =>
                            {
                                var r = tree.Core.ProjectileTypes[w.ProjectileTypeId];
                                double rate = Math.Min(1 / Math.Max(.01, w.DtimeBurst),
                                    Math.Min(w.EnergyPerShot > 0 ? energy / w.EnergyPerShot : double.MaxValue,
                                        w.CAmmoPerShot > 0 ? ammo / w.CAmmoPerShot : double.MaxValue));
                                return (Weapon: w, Rate: rate, Dps: Math.Max(0, r.Power) * rate
                                    * ga[GlobalAttributeSet.DamageGuns] * Rate(r.DamageType, defenses));
                            }).OrderByDescending(o => o.Dps).FirstOrDefault();
                        if (options.Weapon is null) continue;
                        total += options.Dps;
                        energy = Math.Max(0, energy - options.Rate * options.Weapon.EnergyPerShot);
                        ammo = Math.Max(0, ammo - options.Rate * options.Weapon.CAmmoPerShot);
                    }
                    return total;
                }
            }
        }
        // An explicitly dimensionless income index, not predicted credits/sec.
        // IncomeMoney is an additive fraction of base payday, NOT credits.
        double economy = (100 * Math.Max(0, 1 + tree.Civ.IncomeMoney) + state.Stations.Select(id => tree.Core.StationTypes[id].Income).DefaultIfEmpty().Max())
            + 100 * ga[GlobalAttributeSet.MiningRate] * ga[GlobalAttributeSet.MiningYield] * ga[GlobalAttributeSet.MiningCapacity];
        return new(skirmish, siege, recon, economy, support);

        double Rate(byte damage, short[] defenses) => defenses.Length == 0 ? 1 : defenses.Average(def =>
            damage < DataConstantsIGC.DmgidMax && def >= 0 && def < DataConstantsIGC.DefidMax
                ? Math.Max(0, tree.Core.Constants.DamageConstants[damage][def]) : 1);
    }
}
