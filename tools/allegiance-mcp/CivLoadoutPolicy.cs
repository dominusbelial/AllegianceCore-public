using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Strategy;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Default-off modern duty policy. All candidates are complete purchases
/// at one observed dock. Public enemy specs supply defenses; no live enemy state.</summary>
internal static class CivLoadoutPolicy
{
    internal enum Duty { Fighter, Scout, Strike, Support, Recon }
    internal sealed record Input(StaticCore Core, CivProfile Profile, IReadOnlyList<CivProfile> Opponents,
        TechTreeBitMask Techs, GlobalAttributeSet Attributes, short Dock, long Budget,
        DataStationTypeIGC? Target = null, double Window = 30, double Distance = 1000,
        IReadOnlyList<ObservedHull>? Observed = null, GlobalAttributeSet? TargetAttributes = null);
    internal sealed record ObservedHull(short Hull, short Civ, GlobalAttributeSet Attributes);
    internal sealed record Rated(TechLoadoutSelector.Selection Loadout, double Score, double Damage,
        double Acceleration, double Range, double Sustain, double Scan);

    internal static IReadOnlyList<Rated> Candidates(Input input, Duty duty)
    {
        var core = input.Core; var ga = input.Attributes;
        if (!core.StationTypes.TryGetValue(input.Dock, out var dock)) return [];
        bool Buy(byte[] mask) => TechTreeBitMask.RequiredTechsBuyable(mask, dock.TtbmLocal, input.Techs);
        var legal = new ResearchTree(core, input.Profile.Civ, input.Profile.Settings.Research()).Parts
            .Where(p => p.GroupId < 128 && Buy(p.TtbmRequired)
                && !SuccessorUpgrade.PartIsObsolete(core, p, dock.TtbmLocal, input.Techs)).ToArray();
        var defenses = Targets(input, duty == Duty.Strike);
        // The beam's per-gun hint is additive. Complete-fit scoring below
        // combines armor and shield rates BEFORE computing target TTK: a shield
        // gun and an armor gun can complement one another.
        double[] effects = Enumerable.Range(0, DataConstantsIGC.DmgidMax).Select(d => defenses.Length == 0 ? 1 : defenses.Average(t =>
            (Math.Max(0, core.Constants.DamageConstants[d][t.ArmorDefense])
             + Math.Max(0, core.Constants.DamageConstants[d][t.ShieldDefense])) / 2d)).ToArray();
        double enemySpeed = defenses.Select(t => t.Speed).DefaultIfEmpty(150).Average();
        var results = new List<Rated>();
        foreach (var hull in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
            && (duty != Duty.Strike || h.HasCapability(DataHullTypeIGC.HabmThreatToStation))
            && h.Price <= input.Budget && Buy(h.TtbmRequired) && !SuccessorUpgrade.HullIsObsolete(core, h, dock.TtbmLocal, input.Techs))
            .OrderBy(h => h.HullId))
        {
            // Beam search retains complete mixed gun sets, sharing the actual
            // ammo/energy supply. No hull classification gates a flight duty.
            var frontier = new List<List<ExpandedPartData>> { new() };
            foreach (int mount in Enumerable.Range(0, Math.Min(8, hull.Hardpoints.Length)).Where(m => hull.Hardpoints[m].Fixed))
            {
                var guns = legal.Where(p => p.Type is DataPartTypeIGC.Weapon && hull.CanMountPart(p, mount)
                        && (duty == Duty.Strike ? Gun(p).Raw : Gun(p).Power) > 0)
                    .OrderByDescending(p => Gun(p).Power).ThenBy(p => p.PartId).Take(12).ToArray();
                var next = frontier.SelectMany(parts => guns.Select(g => parts.Append(new ExpandedPartData(g.PartId, 1,
                        (byte)mount, ExpandedPartData.FullyMounted)).ToList()).Prepend(parts))
                    .Where(parts => Price(hull, parts) <= input.Budget && VolleyFits(hull, parts))
                    .OrderByDescending(parts => Rate(hull, parts).Score + .01 * parts.Sum(p => Gun(TechLoadoutSelector.Part(core, p.PartId)).Power)).ThenBy(parts => Key(parts), StringComparer.Ordinal).Take(24).ToList();
                frontier = next;
            }
            foreach (var guns in frontier.Where(parts => parts.Count > 0))
            {
                var parts = guns.ToList();
                // Evaluate each optional item in the complete fit. Empty slots
                // remain legitimate when a part has no useful duty contribution.
                foreach (var equipment in new[] { EquipmentType.Shield, EquipmentType.Afterburner, EquipmentType.Magazine,
                    EquipmentType.Cloak, EquipmentType.Dispenser, EquipmentType.ChaffLauncher })
                {
                    var options = legal.Where(p => p.EquipmentType == equipment && hull.CanMountPart(p, 0))
                        .Select(p => (Part: p, Amount: Amount(hull, p, input.Budget - Price(hull, parts))))
                        .Where(p => p.Amount > 0).Select(p => parts.Append(new ExpandedPartData(p.Part.PartId, p.Amount, 0,
                            ExpandedPartData.FullyMounted)).ToList()).Prepend(parts);
                    parts = options.Where(p => Price(hull, p) <= input.Budget).OrderByDescending(p => Rate(hull, p).Score)
                        .ThenBy(Key, StringComparer.Ordinal).First();
                }
                // Equip turret hardware without crediting imaginary gunners.
                // Actual boarding, allocation and controls retain P814's gates.
                if (GunnerPolicy.Effective.Enable && duty != Duty.Support)
                {
                    foreach (int mount in Enumerable.Range(0, Math.Min(8, hull.Hardpoints.Length)).Where(m => !hull.Hardpoints[m].Fixed))
                    {
                        var gun = legal.Where(p => p.Type is DataPartTypeIGC.Weapon && hull.CanMountPart(p, mount) && Gun(p).Power > 0
                                && Price(hull, parts) + TechLoadoutSelector.PartPrice(p, 1) <= input.Budget)
                            .OrderByDescending(p => Gun(p).Power).ThenBy(p => p.PartId).FirstOrDefault();
                        if (gun.Type is not null) parts.Add(new(gun.PartId, 1, (byte)mount, ExpandedPartData.FullyMounted));
                    }
                }
                if (!TechLoadoutSelector.Legal(core, input.Profile.Civ, input.Techs, input.Dock, hull.HullId, parts)) continue;
                var rated = Rate(hull, parts);
                if (rated.Damage > 0 && (duty != Duty.Strike || input.Target is null
                    || ShowcaseFinale.Volley(core, parts, input.Target.Value).Hull > 0)) results.Add(rated);
            }
        }
        return results.GroupBy(r => (r.Loadout.HullId, Parts: Key(r.Loadout.Parts)))
            .Select(g => g.First()).OrderByDescending(r => r.Score).ThenBy(r => r.Loadout.HullId)
            .ThenBy(r => Key(r.Loadout.Parts), StringComparer.Ordinal).ToArray();

        string Key(IEnumerable<ExpandedPartData> parts) => string.Join(',', parts.Select(p => $"{p.PartId}@{p.MountId}:{p.Amount}"));
        long Price(DataHullTypeIGC h, IEnumerable<ExpandedPartData> parts) => Math.Max(0, h.Price)
            + parts.Sum(p => TechLoadoutSelector.PartPrice(TechLoadoutSelector.Part(core, p.PartId), p.Amount));
        short Amount(DataHullTypeIGC h, DataPartTypeIGC p, long budget)
        {
            short max = Ship.ClampPartAmount(h, core, p, p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser
                or EquipmentType.ChaffLauncher ? short.MaxValue : 1);
            return (short)Math.Min(max, p.Price > 0 ? Math.Clamp(budget / p.Price, 0, short.MaxValue) : max);
        }
        bool VolleyFits(DataHullTypeIGC h, List<ExpandedPartData> parts)
        {
            var weapons = parts.Select(p => TechLoadoutSelector.Part(core, p.PartId).Type).OfType<DataPartTypeIGC.Weapon>().ToArray();
            return weapons.Sum(w => Math.Max(0, w.EnergyPerShot)) <= h.MaxEnergy * ga.Get(GlobalAttributeSet.MaxEnergy)
                && weapons.Sum(w => Math.Max(0, (int)w.CAmmoPerShot)) <= h.MaxAmmo;
        }
        (double Power, double Raw, int Damage, double Energy, double Ammo, double Range) Gun(DataPartTypeIGC p)
        {
            if (p.Type is not DataPartTypeIGC.Weapon w || !core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r)) return default;
            double power = duty == Duty.Support ? Math.Max(0, -r.Power) : Math.Max(0, r.Power) + .5 * Math.Max(0, r.BlastPower);
            int damage = r.DamageType & 0x1f;
            double speed = r.Speed * (w.CAmmoPerShot > 0 ? ga.Get(GlobalAttributeSet.SpeedAmmo) : 1);
            double range = speed * r.Lifespan * (w.EnergyPerShot > 0 ? ga.Get(GlobalAttributeSet.LifespanEnergy) : 1);
            double rate = 1 / Math.Max(.01, w.DtimeBurst);
            double raw = power * ga.Get(GlobalAttributeSet.DamageGuns) * rate * Math.Min(1, speed / Math.Max(1, enemySpeed))
                * Math.Min(1, range / Math.Max(1, input.Distance));
            return (raw * (duty == Duty.Support ? 1 : damage < effects.Length ? effects[damage] : 0), raw, damage,
                Math.Max(0, w.EnergyPerShot) * rate,
                Math.Max(0, (int)w.CAmmoPerShot) * rate, range);
        }
        Rated Rate(DataHullTypeIGC h, IReadOnlyList<ExpandedPartData> parts)
        {
            double mass = h.Mass, shield = 0, extraThrust = 0, signature = h.Signature,
                scan = h.ScannerRange * ga.Get(GlobalAttributeSet.ScanRange), utility = 0, cloakEnergy = 0;
            double energy = 0, ammo = 0, damage = 0, range = 0;
            var gunRates = new double[DataConstantsIGC.DmgidMax];
            var missileRates = new double[DataConstantsIGC.DmgidMax];
            foreach (var part in parts)
            {
                var p = TechLoadoutSelector.Part(core, part.PartId);
                mass += p.Mass * (p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? part.Amount : 1);
                signature += Math.Max(0, p.Signature);
                switch (p.Type)
                {
                    case DataPartTypeIGC.Weapon when h.Hardpoints[part.MountId].Fixed:
                        var gun = Gun(p); damage += gun.Raw; energy += gun.Energy; ammo += gun.Ammo; range = Math.Max(range, gun.Range);
                        if (gun.Damage < gunRates.Length) gunRates[gun.Damage] += gun.Raw;
                        break;
                    case DataPartTypeIGC.Shield s: shield += s.MaxStrength * ga.Get(GlobalAttributeSet.MaxShieldShip); break;
                    case DataPartTypeIGC.Afterburner a:
                        extraThrust += Math.Max(0, a.MaxThrust) * Math.Min(1, h.MaxFuel / Math.Max(.01, a.FuelConsumption * input.Window)); break;
                    case DataPartTypeIGC.Cloak c:
                        utility += .15 * c.MaxCloaking * (duty is Duty.Scout or Duty.Recon or Duty.Strike ? 2 : 1);
                        cloakEnergy += Math.Max(0, c.EnergyConsumption); break;
                    case DataPartTypeIGC.MissileLauncher m when core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var missile):
                        if (duty == Duty.Support || missile.HasCaptureCapability) break;
                        int d = missile.DamageType & 0x1f;
                        double tracking = missile.LockTime <= 0 ? 1 : Math.Min(1, missile.TurnRate * ga.Get(GlobalAttributeSet.TurnRateMissile)
                            * Math.Max(1, missile.Lifespan) / Math.Max(1, enemySpeed));
                        double rate = Math.Min(part.Amount / input.Window, 1 / Math.Max(.01, missile.ReadyTime + missile.LockTime));
                        double raw = (Math.Max(0, missile.Power) + .5 * Math.Max(0, missile.BlastPower))
                            * ga.Get(GlobalAttributeSet.DamageMissiles) * rate * tracking;
                        if (d < missileRates.Length) missileRates[d] += raw;
                        break;
                    case DataPartTypeIGC.ProbeLauncher probe when core.ProbeTypes.TryGetValue(probe.ExpendableTypeId, out var pr):
                        utility += .05 * Math.Log(1 + Math.Max(0, pr.ScannerRange) * Math.Max(1, (int)part.Amount)); break;
                    case DataPartTypeIGC.MineLauncher mine when core.MineTypes.TryGetValue(mine.ExpendableTypeId, out var mt):
                        utility += .025 * Math.Log(1 + Math.Max(0, mt.Power) * Math.Max(1, (int)part.Amount)); break;
                    case DataPartTypeIGC.ChaffLauncher: utility += .03 * Math.Log(1 + part.Amount); break;
                }
            }
            double sustain = Math.Min(1, Math.Min(energy > 0 ? Math.Max(0, h.MaxEnergy * ga.Get(GlobalAttributeSet.MaxEnergy) / input.Window
                    + h.RechargeRate - cloakEnergy) / energy : 1, ammo > 0 ? h.MaxAmmo / input.Window / ammo : 1));
            damage = duty == Duty.Support ? damage * sustain : defenses.Length == 0
                ? gunRates.Average() * sustain + missileRates.Average()
                : defenses.Average(t =>
                {
                    double armor = 0, shields = 0, bypass = 0;
                    for (int d = 0; d < gunRates.Length; d++)
                    {
                        double raw = gunRates[d] * sustain + missileRates[d];
                        double a = Math.Max(0, core.Constants.DamageConstants[d][t.ArmorDefense]);
                        double sh = Math.Max(0, core.Constants.DamageConstants[d][t.ShieldDefense]);
                        armor += raw * a; shields += raw * sh;
                        // Native zero shield multiplier bypasses the shield,
                        // it does not make an otherwise damaging gun useless.
                        if (sh == 0) bypass += raw * a;
                    }
                    if (armor <= 0) return 0;
                    double shieldTime = t.Shield > 0 && shields > 0 ? t.Shield / shields : 0;
                    double through = shieldTime + Math.Max(0, t.Armor - bypass * shieldTime) / armor;
                    double time = bypass > 0 ? Math.Min(through, t.Armor / bypass) : through;
                    return time > 0 ? (t.Armor + Math.Min(t.Shield, shields * time)) / time : 0;
                });
            double acceleration = (h.Thrust * ga.Get(GlobalAttributeSet.Thrust) + extraThrust) / Math.Max(1, mass);
            double speed = h.Speed * ga.Get(GlobalAttributeSet.MaxSpeed);
            double hp = h.HitPoints * ga.Get(GlobalAttributeSet.MaxArmorShip) + shield;
            double handling = Math.Sqrt(Math.Max(.001, acceleration)) * Math.Sqrt(Math.Max(1, speed))
                * Math.Pow(Math.Max(.01, h.MaxTurnRates.Average(x => (double)x) * ga.Get(GlobalAttributeSet.TurnRate)), .1);
            double sensing = scan * Math.Max(.01, ga.Get(GlobalAttributeSet.Signature)) / Math.Max(1, signature);
            long price = Price(h, parts);
            double score = damage * Math.Sqrt(Math.Max(1, hp)) * handling * (1 + utility) / (1 + price / 10000d);
            if (duty is Duty.Scout or Duty.Recon) score *= Math.Sqrt(Math.Max(1, sensing)) * (duty == Duty.Recon ? 1 + speed / 500 : 1);
            var selected = new TechLoadoutSelector.Selection(h.HullId, h.Name, parts.ToArray(), price,
                FormattableString.Invariant($"civ-strategy duty={duty}; complete-fit score={score:F3} dps={damage:F3} sustain={sustain:F3} acceleration={acceleration:F3} range={range:F1}; public-opponent defenses; live GA; cost={price}"));
            return new(selected, score, damage, acceleration, range, sustain, scan);
        }
    }

    private sealed record Target(double Armor, byte ArmorDefense, double Shield, byte ShieldDefense, double Speed);
    private static Target[] Targets(Input input, bool station)
    {
        var core = input.Core;
        if (station)
        {
            if (input.Target is { } explicitTarget)
            {
                var ga = input.TargetAttributes ?? GlobalAttributeSet.Identity;
                return [new(explicitTarget.MaxArmorHitPoints * ga.Get(GlobalAttributeSet.MaxArmorStation), explicitTarget.DefenseTypeArmor,
                    explicitTarget.MaxShieldHitPoints * ga.Get(GlobalAttributeSet.MaxShieldStation), explicitTarget.DefenseTypeShield, 0)];
            }
            return input.Opponents.Select(p =>
            {
                var s = core.StationTypes[p.StartingStation];
                return new Target(s.MaxArmorHitPoints * p.Attributes[GlobalAttributeSet.MaxArmorStation], s.DefenseTypeArmor,
                    s.MaxShieldHitPoints * p.Attributes[GlobalAttributeSet.MaxShieldStation], s.DefenseTypeShield, 0);
            }).ToArray();
        }
        if (input.Observed is { Count: > 0 } observed)
            return observed.Select(o =>
            {
                var h = core.HullTypes[o.Hull];
                // Enemy inventory is not public. The core's preferred fitting
                // is an explicit shield assumption; observed hull/GA are exact.
                var shield = h.PreferredPartsTypes.Where(core.PartTypes.ContainsKey).Select(id => core.PartTypes[id])
                    .Where(p => p.Type is DataPartTypeIGC.Shield && h.CanMountPart(p, 0)).Select(p => (DataPartTypeIGC.Shield)p.Type)
                    .OrderByDescending(s => s.MaxStrength).FirstOrDefault();
                return new Target(h.HitPoints * o.Attributes.Get(GlobalAttributeSet.MaxArmorShip), h.DefenseType,
                    (shield?.MaxStrength ?? 0) * o.Attributes.Get(GlobalAttributeSet.MaxShieldShip), shield?.DefenseType ?? 0,
                    h.Speed * o.Attributes.Get(GlobalAttributeSet.MaxSpeed));
            }).ToArray();
        return input.Opponents.SelectMany(p => p.Hulls.Where(h => h.FixedGuns > 0 && p.StartingPurchases.Any(u => u.Kind == "hull" && u.Id == h.Id))
            .Select(h =>
            {
                var shields = p.Parts.Where(s => s.Equipment == EquipmentType.Shield && p.StartingPurchases.Any(u => u.Kind == "part" && u.Id == s.Id)
                    && core.HullTypes[h.Id].CanMountPart(TechLoadoutSelector.Part(core, s.Id), 0))
                    .Select(s => (DataPartTypeIGC.Shield)TechLoadoutSelector.Part(core, s.Id).Type).OrderByDescending(s => s.MaxStrength).FirstOrDefault();
                return new Target(h.Armor * p.Attributes[GlobalAttributeSet.MaxArmorShip], h.Defense,
                    (shields?.MaxStrength ?? 0) * p.Attributes[GlobalAttributeSet.MaxShieldShip], shields?.DefenseType ?? 0,
                    h.Speed * p.Attributes[GlobalAttributeSet.MaxSpeed]);
            })).ToArray();
    }
}
