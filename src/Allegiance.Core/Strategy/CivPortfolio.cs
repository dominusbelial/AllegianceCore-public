using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Allegiance.Core.Model;

namespace Allegiance.Core.Strategy;

/// <summary>Public planning assumptions, never inferred from hidden mission state. Distances
/// model delivery/turnaround; they do not assert that a rock, route or crew is already available.</summary>
public sealed record PortfolioSettings(double Funding = 30000, double Distance = 1000, int Pilots = 6,
    int CrewPerShip = 3, double Window = 30, double ResearchTimeScale = 1, string Goals = "default", string Map = "", int? MapSeed = null)
{
    public void Validate()
    {
        if (!double.IsFinite(Funding) || Funding <= 0 || !double.IsFinite(Distance) || Distance < 0
            || Pilots <= 0 || CrewPerShip <= 0 || !double.IsFinite(Window) || Window <= 0
            || !double.IsFinite(ResearchTimeScale) || ResearchTimeScale <= 0)
            throw new InvalidDataException("invalid public portfolio assumptions");
    }
}
public sealed record FittedPart(short Id, int Mount, int Amount);
public sealed record DeliveredSpike(string Duty, short Hull, short Station, ImmutableArray<FittedPart> Parts,
    int Ships, int Crew, double Price, double TravelSeconds, double Power, double OreCycleSeconds = 0);
public sealed record PortfolioCandidate(string Id, ProfileRoute Route, double Score, double ShipGain,
    double BaseGain, double EconomyGain, double SupportGain, ImmutableArray<DeliveredSpike> Spikes);
public sealed record PortfolioChoice(string InputHash, long Seed, short Civ, ImmutableArray<short> Opponents,
    PortfolioSettings Assumptions, PortfolioCandidate? Primary, PortfolioCandidate? Counter,
    ImmutableArray<PortfolioCandidate> Alternatives, ImmutableArray<PortfolioCandidate> CounterAlternatives, ImmutableArray<string> Rejections);

/// <summary>Shared modern portfolio policy. Own legality is exact ResearchState replay;
/// delivered power and funding/time utility are explicit estimates, not native game rules.</summary>
public static class CivPortfolio
{
    public static PortfolioChoice Choose(StaticCore core, CivProfile profile, IEnumerable<CivProfile> opponents,
        long seed, PortfolioSettings? settings = null)
    {
        var options = settings ?? new(); options.Validate();
        var enemies = opponents.OrderBy(p => p.Civ).ThenBy(p => p.CoreIdentity, StringComparer.Ordinal).ToArray();
        if (enemies.Any(p => p.CoreIdentity != profile.CoreIdentity || p.Settings != profile.Settings))
            throw new InvalidDataException("opponent profiles must share core and research settings");
        if (!ReferenceEquals(profile, CivProfile.Get(core, profile.Civ, profile.Settings.Research())))
            throw new InvalidDataException("profile belongs to another core");
        string input = System.Text.Json.JsonSerializer.Serialize(new PortfolioInput(profile.CoreIdentity, profile.CoreVersion,
            profile.Civ, profile.Settings, seed, options, [.. enemies.Select(p => p.Civ)]), StrategyJsonContext.Default.PortfolioInput);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        string inputHash = Convert.ToHexString(hash);
        var tree = new ResearchTree(core, profile.Civ, profile.Settings.Research());
        var initial = Evaluate(tree, [], enemies, options);
        double[] before = Values(initial);
        var candidates = new List<PortfolioCandidate>();
        int noGain = 0, noFieldable = 0;
        var evaluated = profile.Routes.Where(r => r.Target.Kind != "drone")
            .Select(route => (Route: route, Spikes: Evaluate(tree, route.Steps, enemies, options))).ToArray();
        var reference = before.Select((b, i) => Math.Max(1, Math.Max(b,
            .10 * evaluated.Select(e => Values(e.Spikes)[i]).DefaultIfEmpty().Max()))).ToArray();
        foreach (var (route, spikes) in evaluated)
        {
            double[] after = Values(spikes);
            var gain = before.Select((b, i) => Math.Log((reference[i] + after[i]) / (reference[i] + b))).ToArray();
            double benefit = .45 * gain[0] + .30 * gain[1] + .15 * gain[2] + .10 * gain[3];
            if (spikes.All(s => s.Ships == 0)) { noFieldable++; continue; }
            if (benefit <= 0) { noGain++; continue; }
            double time = CriticalSeconds(tree, route, options);
            double score = benefit / (1 + route.Funding / options.Funding + time / 300);
            string id = string.Join('/', route.Steps.Select(k => $"{k.Kind}:{k.Id}"));
            candidates.Add(new(id, route with { CriticalSeconds = time }, score, gain[0], gain[1], gain[2], gain[3], spikes));
        }
        var ranked = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        // Only use near-optimal variety, bounded to three complete useful portfolios.
        var close = ranked.Where(c => c.Score >= ranked[0].Score * .8).Take(3).ToArray();
        var primary = close.Length == 0 ? null : close[System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) % close.Length];
        // A second path earns value from opposing defense/mobility and complementary duties.
        // Shared prerequisites are free in marginal cost; do not choose an identical unlock.
        int incompatible = 0;
        var primaryPower = primary is null ? before : Values(primary.Spikes);
        var counterRanked = ranked.Where(c => c.Route.Target != primary?.Route.Target &&
                c.Route.Steps.Any(k => primary is null || !primary.Route.Steps.Contains(k)))
            .Select(c =>
            {
                var combined = Combine(tree, primary, c);
                if (combined is null) { incompatible++; return (Candidate: c, Score: 0.0); }
                var spikes = Evaluate(tree, combined, enemies, options);
                var power = Values(spikes);
                var gains = power.Select((value, i) => Math.Log((reference[i] + value) / (reference[i] + before[i]))).ToArray();
                double marginal = power.Select((value, i) => new[] { .45, .30, .15, .10 }[i]
                    * Math.Log((reference[i] + value) / (reference[i] + primaryPower[i]))).Sum();
                double benefit = .45 * gains[0] + .30 * gains[1] + .15 * gains[2] + .10 * gains[3];
                double cost = combined.Sum(k => (double)tree.Find(k.Kind, k.Id).Price);
                double time = CriticalSeconds(tree, c.Route with { Steps = [.. combined] }, options);
                // Compare close COMPLETE primary+counter portfolios; weak marginal
                // counters cannot displace a useful opening merely to add variety.
                double score = marginal > 0 ? benefit / (1 + cost / options.Funding + time / 300) : 0;
                return (Candidate: c with { Score = score, Spikes = spikes, ShipGain = gains[0], BaseGain = gains[1],
                    EconomyGain = gains[2], SupportGain = gains[3] }, Score: score);
            }).Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Candidate.Id, StringComparer.Ordinal).ToArray();
        var counterClose = counterRanked.Where(c => c.Score >= counterRanked[0].Score * .8).Take(3).ToArray();
        var counter = counterClose.Length == 0 ? null : counterClose[
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4)) % counterClose.Length].Candidate;
        var rejections = new List<string> { $"no-positive-gain={noGain}", $"no-fieldable-spike={noFieldable}",
            $"outside-close-set={Math.Max(0, ranked.Length - close.Length)}", $"incompatible-counter-route={incompatible}" };
        rejections.AddRange(ranked.Where(c => !close.Contains(c)).Take(3).Select(c => $"{c.Route.Target.Kind}:{c.Route.Target.Id}:score-below-selection-or-close-cap"));
        if (ranked.Length == 0) rejections.Add("no-legal-improving-route");
        return new(inputHash, seed, profile.Civ, [.. enemies.Select(p => p.Civ)], options, primary, counter, [.. close], [.. counterClose.Select(c => c.Candidate)], [.. rejections]);
    }
    private static StrategyNode[]? Combine(ResearchTree tree, PortfolioCandidate? primary, PortfolioCandidate counter)
    {
        var keys = primary?.Route.Steps.ToList() ?? new List<StrategyNode>();
        var state = CivProfile.Replay(tree, keys);
        foreach (var key in counter.Route.Steps.Where(k => !keys.Contains(k)).ToArray())
        {
            if (!state.CanApply(tree.Find(key.Kind, key.Id))) return null;
            state.Apply(CivProfile.Step(tree, key)); keys.Add(key);
        }
        return keys.ToArray();
    }
    private static double CriticalSeconds(ResearchTree tree, ProfileRoute route, PortfolioSettings options)
    {
        var ends = new List<(ResearchNode Node, double End)>();
        foreach (var key in route.Steps)
        {
            var n = tree.Find(key.Kind, key.Id);
            double start = ends.Where(p => Enumerable.Range(0, 50).Any(b =>
                (n.Required[b] & (p.Node.Effects[b] | p.Node.Local[b])) != 0)).Select(p => p.End).DefaultIfEmpty().Max();
            double travel = n.Kind == "station" && tree.Core.DroneTypes.TryGetValue(n.ConstructorId, out var drone)
                && tree.Core.HullTypes.TryGetValue(drone.HullTypeId, out var h)
                ? options.Distance / Math.Max(1, h.Speed * tree.Civ.GasBaseAttributes[GlobalAttributeSet.MaxSpeed]) : 0;
            ends.Add((n, start + n.Seconds / options.ResearchTimeScale + travel
                + (n.Kind == "station" ? Sim.BuildingEffect.BuildDuration / options.ResearchTimeScale : 0)));
        }
        return ends.Select(p => p.End).DefaultIfEmpty().Max();
    }
    private static double[] Values(ImmutableArray<DeliveredSpike> s) => new[] { "anti-ship", "anti-base", "miner", "rescue-repair" }
        .Select(duty => s.Where(p => p.Duty == duty).Select(p => p.Power).DefaultIfEmpty().Max()).ToArray();

    public static ImmutableArray<DeliveredSpike> Evaluate(ResearchTree tree, IEnumerable<StrategyNode> route,
        IReadOnlyList<CivProfile> opponents, PortfolioSettings options)
    {
        options.Validate();
        var keys = route.ToArray(); var state = CivProfile.Replay(tree, keys);
        var ga = (float[])tree.Civ.GasBaseAttributes.Clone();
        foreach (var k in keys.Where(k => k.Kind == "develop"))
        {
            var n = tree.Find(k.Kind, k.Id);
            for (int i = 0; i < ga.Length; i++) ga[i] *= n.Attributes[i];
        }
        var shipTargets = opponents.SelectMany(p => p.Hulls.Where(h => p.StartingPurchases.Any(u => u.Kind == "hull" && u.Id == h.Id)
            && h.FixedGuns > 0).Select(h =>
            {
                var shields = tree.Parts.Where(part => part.Type is DataPartTypeIGC.Shield
                    && p.StartingPurchases.Any(u => u.Kind == "part" && u.Id == part.PartId)
                    && tree.Core.HullTypes[h.Id].CanMountPart(part, 0))
                    .Select(part => (DataPartTypeIGC.Shield)part.Type).OrderByDescending(sh => sh.MaxStrength).FirstOrDefault();
                return (Armor: h.Armor * p.Attributes[GlobalAttributeSet.MaxArmorShip], ArmorDefense: (int)h.Defense,
                    Shield: (shields?.MaxStrength ?? 0) * p.Attributes[GlobalAttributeSet.MaxShieldShip], ShieldDefense: (int)(shields?.DefenseType ?? 0),
                    Speed: h.Speed * p.Attributes[GlobalAttributeSet.MaxSpeed]);
            })).ToArray();
        double enemySpeed = shipTargets.Select(t => (double)t.Speed).DefaultIfEmpty(150).Average();
        var baseTargets = opponents.Select(p =>
        {
            var station = tree.Core.StationTypes[p.StartingStation];
            return (Armor: station.MaxArmorHitPoints * p.Attributes[GlobalAttributeSet.MaxArmorStation], ArmorDefense: (int)station.DefenseTypeArmor,
                Shield: station.MaxShieldHitPoints * p.Attributes[GlobalAttributeSet.MaxShieldStation], ShieldDefense: (int)station.DefenseTypeShield, Speed: 0f);
        }).ToArray();
        var shipEffects = Enumerable.Range(0, DataConstantsIGC.DmgidMax).Select(d => Effect(d, shipTargets)).ToArray();
        var baseEffects = Enumerable.Range(0, DataConstantsIGC.DmgidMax).Select(d => Effect(d, baseTargets)).ToArray();
        var effectiveStations = state.Stations.Select(id => SuccessorUpgrade.StationSuccessor(tree.Core,
            tree.Core.StationTypes[id], state.DevelopmentTechs).StationTypeId).Distinct().Order().ToArray();
        var techs = effectiveStations.Aggregate(state.DevelopmentTechs,
            (current, id) => current.Or(TechTreeBitMask.FromBytes(tree.Core.StationTypes[id].TtbmEffects)));
        var result = new List<DeliveredSpike>();
        foreach (var stationId in effectiveStations)
        {
            var station = tree.Core.StationTypes[stationId];
            bool Buy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, station.TtbmLocal, techs);
            var parts = tree.Parts.Where(p => p.GroupId != byte.MaxValue && Buy(p.TtbmRequired)
                && !SuccessorUpgrade.PartIsObsolete(tree.Core, p, station.TtbmLocal, techs)).ToArray();
            foreach (var hull in tree.Core.HullTypes.Values.OrderBy(h => h.HullId).Where(h => h.GroupId >= 0
                && !h.HasCapability(DataHullTypeIGC.HabmLifepod | DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder)
                && Buy(h.TtbmRequired) && !SuccessorUpgrade.HullIsObsolete(tree.Core, h, station.TtbmLocal, techs)))
            {
                // Fixed guns require one pilot. Turrets only contribute if crew is allocated.
                foreach (string duty in new[] { "anti-ship", "anti-base", "rescue-repair" })
                {
                    int fighterSlots = (options.Pilots + 1) / 2;
                    int strikeSlots = Math.Min(options.Pilots - fighterSlots, (options.Pilots + 2) / 3);
                    int slots = duty == "anti-ship" ? fighterSlots : duty == "anti-base" ? strikeSlots : options.Pilots - fighterSlots - strikeSlots;
                    if (slots == 0) continue;
                    int crew = 1 + Math.Min(Math.Min(options.CrewPerShip, slots) - 1, hull.Hardpoints.Count(p => !p.Fixed));
                    double dutyFunding = .8 * options.Funding * slots / options.Pilots;
                    var fitted = new List<FittedPart>(); double price = Math.Max(0, hull.Price), mass = hull.Mass;
                    double energy = hull.MaxEnergy * ga[GlobalAttributeSet.MaxEnergy] / options.Window + hull.RechargeRate;
                    double ammo = Math.Max(0, (int)hull.MaxAmmo) / options.Window, dps = 0;
                    var shield = parts.Where(p => p.Type is DataPartTypeIGC.Shield && hull.CanMountPart(p, 0))
                        .OrderByDescending(p => ((DataPartTypeIGC.Shield)p.Type).MaxStrength).ThenBy(p => p.PartId).FirstOrDefault();
                    double shieldHp = shield.Type is DataPartTypeIGC.Shield sh ? sh.MaxStrength * ga[GlobalAttributeSet.MaxShieldShip] : 0;
                    if (shield.Type is not null) { fitted.Add(new(shield.PartId, 0, 1)); price += shield.Price; mass += shield.Mass; }
                    int turrets = 0;
                    for (int mount = 0; mount < hull.Hardpoints.Length; mount++)
                    {
                        if (!hull.Hardpoints[mount].Fixed && ++turrets > crew - 1) continue;
                        var gun = parts.Where(p => p.Type is DataPartTypeIGC.Weapon && hull.CanMountPart(p, mount))
                            .Select(p =>
                            {
                                var w = (DataPartTypeIGC.Weapon)p.Type;
                                if (!tree.Core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r)) return (Part: p, Rate: 0.0, Power: 0.0);
                                double rate = Math.Min(1 / Math.Max(.01, w.DtimeBurst), Math.Min(w.EnergyPerShot > 0 ? energy / w.EnergyPerShot : double.MaxValue,
                                    w.CAmmoPerShot > 0 ? ammo / w.CAmmoPerShot : double.MaxValue));
                                double power = duty == "rescue-repair" ? Math.Max(0, -r.Power) * ga[GlobalAttributeSet.DamageGuns] * rate
                                    : (Math.Max(0, r.Power) + .5 * Math.Max(0, r.BlastPower)) * ga[GlobalAttributeSet.DamageGuns] * rate * DamageRate(r.DamageType, duty)
                                        * Math.Min(1, (r.Speed * (w.CAmmoPerShot > 0 ? ga[GlobalAttributeSet.SpeedAmmo] : 1)) / Math.Max(1, enemySpeed))
                                        * Math.Min(1, r.Speed * (w.CAmmoPerShot > 0 ? ga[GlobalAttributeSet.SpeedAmmo] : 1) * r.Lifespan * (w.EnergyPerShot > 0 ? ga[GlobalAttributeSet.LifespanEnergy] : 1) / Math.Max(1, options.Distance));
                                return (Part: p, Rate: rate, Power: power);
                            }).OrderByDescending(p => p.Power).ThenBy(p => p.Part.PartId).FirstOrDefault();
                        if (gun.Power <= 0 || gun.Part.Type is not DataPartTypeIGC.Weapon weapon) continue;
                        fitted.Add(new(gun.Part.PartId, mount, 1)); mass += gun.Part.Mass; price += gun.Part.Price; dps += gun.Power;
                        energy = Math.Max(0, energy - gun.Rate * weapon.EnergyPerShot);
                        ammo = Math.Max(0, ammo - gun.Rate * weapon.CAmmoPerShot);
                    }
                    if (duty != "rescue-repair")
                    {
                        var magazine = parts.Where(p => p.Type is DataPartTypeIGC.MissileLauncher && hull.CanMountPart(p, 0))
                            .Select(p =>
                            {
                                var m = tree.Core.MissileTypes[((DataPartTypeIGC.MissileLauncher)p.Type).ExpendableTypeId];
                                int capacity = (int)Math.Floor(hull.CapacityMagazine / Math.Max(1.0, m.LauncherExpendableSize));
                                double shots = Math.Min(capacity, options.Window / Math.Max(.1, m.ReadyTime + m.LockTime));
                                return (Part: p, Capacity: capacity, Power: (duty == "anti-base" && m.HasCaptureCapability ? 0 : Math.Max(0, m.Power) + .5 * Math.Max(0, m.BlastPower)) * ga[GlobalAttributeSet.DamageMissiles]
                                    * shots / options.Window * DamageRate(m.DamageType, duty));
                            }).OrderByDescending(m => m.Power).ThenBy(m => m.Part.PartId).FirstOrDefault();
                        if (magazine.Power > 0)
                        { fitted.Add(new(magazine.Part.PartId, 0, magazine.Capacity)); dps += magazine.Power; price += magazine.Part.Price * magazine.Capacity; mass += magazine.Part.Mass * magazine.Capacity; }
                    }
                    double speed = Math.Max(1, hull.Speed * ga[GlobalAttributeSet.MaxSpeed]);
                    double accel = hull.Thrust * ga[GlobalAttributeSet.Thrust] / Math.Max(1, mass);
                    double travel = options.Distance / speed + Math.Sqrt(2 * options.Distance / Math.Max(.01, accel));
                    double durability = Math.Max(1, hull.HitPoints * ga[GlobalAttributeSet.MaxArmorShip] + shieldHp);
                    int ships = Math.Min(slots / crew, (int)Math.Min(int.MaxValue, Math.Floor(dutyFunding / Math.Max(1, price))));
                    double survival = Math.Sqrt(durability / 400) * Math.Sqrt(speed / 150)
                        * Math.Pow(Math.Max(.01, hull.MaxTurnRates.Take(2).Average() * ga[GlobalAttributeSet.TurnRate]), .25)
                        / Math.Pow(Math.Max(.1, hull.Signature / ga[GlobalAttributeSet.Signature]), .25);
                    double power = ships * dps * survival / (1 + travel / options.Window);
                    if (duty == "rescue-repair" && hull.HasCapability(DataHullTypeIGC.HabmRescue))
                        power += ships * speed / (1 + travel) / 10;
                    if (ships > 0 && power > 0) result.Add(new(duty, hull.HullId, stationId, [.. fitted], ships, crew, price, travel, power));
                }
            }
            foreach (var drone in tree.Core.DroneTypes.Values.Where(d => d.PilotType == Sim.Ship.PilotTypeMiner && Buy(d.TtbmRequired)))
            {
                if (!tree.Core.HullTypes.TryGetValue(drone.HullTypeId, out var h)) continue;
                double capacity = tree.Core.Constants.FloatConstants[7] * ga[GlobalAttributeSet.MiningCapacity];
                double travel = 2 * options.Distance / Math.Max(1, h.Speed * ga[GlobalAttributeSet.MaxSpeed]);
                double cycle = capacity / Math.Max(.01, ga[GlobalAttributeSet.MiningRate]) + travel;
                int count = Math.Min(Math.Max(1, tree.Settings.InitialMinersPerTeam), (int)(.2 * options.Funding / Math.Max(1, drone.Price)));
                if (count <= 0) continue;
                double proceeds = capacity * tree.Core.Constants.FloatConstants[8] * ga[GlobalAttributeSet.MiningYield];
                double payday = (tree.Core.Constants.FloatConstants[30] * (1 + tree.Civ.IncomeMoney) + effectiveStations.Sum(id => tree.Core.StationTypes[id].Income))
                    / Math.Max(1, tree.Core.Constants.FloatConstants[6]);
                result.Add(new("miner", h.HullId, stationId, [], count, 0, drone.Price, travel, count * proceeds / Math.Max(1, cycle) + payday, cycle));
            }
        }
        return [.. result.GroupBy(s => s.Duty).Select(g => g.OrderByDescending(s => s.Power).ThenBy(s => s.Hull).First())];

        double DamageRate(byte damage, string duty)
        {
            int row = damage & Sim.BlastDamage.Mask; // Igc/missionigc.h:149-155
            return row < DataConstantsIGC.DmgidMax ? (duty == "anti-base" ? baseEffects[row] : shipEffects[row]) : 1;
        }
        double Effect(int damage, (float Armor, int ArmorDefense, float Shield, int ShieldDefense, float Speed)[] targets)
        {
            if (targets.Length == 0) return 1;
            return targets.Average(t =>
            {
                double armor = tree.Core.Constants.DamageConstants[damage][t.ArmorDefense];
                double shield = tree.Core.Constants.DamageConstants[damage][t.ShieldDefense];
                if ((t.Armor > 0 && armor <= 0) || (t.Shield > 0 && shield <= 0)) return 0;
                double time = (t.Armor > 0 ? t.Armor / armor : 0) + (t.Shield > 0 ? t.Shield / shield : 0);
                return time > 0 ? (t.Armor + t.Shield) / time : 0;
            });
        }

    }

    /// <summary>Report-only ordering witness. Never installed into Mission.ResearchPlans.</summary>
    public static ResearchPlan ComparisonPlan(ResearchTree tree, PortfolioChoice choice, string coreName)
    {
        var result = new ResearchPlan { Core = coreName, CoreVersion = tree.Core.Version, CivId = tree.Civ.CivilizationId, Name = "p1000-comparison" };
        var state = new ResearchState(tree); var used = new HashSet<StrategyNode>();
        foreach (var candidate in new[] { choice.Primary, choice.Counter }.Where(c => c is not null))
            foreach (var key in candidate!.Route.Steps)
                if (state.CanApply(tree.Find(key.Kind, key.Id))) Add(key);
        while (tree.Nodes.FirstOrDefault(n => state.CanApply(n)) is { } n) Add(CivProfile.Key(n));
        return result;
        void Add(StrategyNode key)
        {
            if (!used.Add(key)) return;
            var step = CivProfile.Step(tree, key); state.Apply(step); result.Steps.Add(step);
        }
    }
}
