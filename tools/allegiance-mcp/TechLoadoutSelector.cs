using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Pure purchase policy. Unlocks supplies identities, never a synthetic tech level.
/// Native gates: Igc/stationIGC.h:383-433; hullTypeIGC.h:163-213;
/// human catalogue: WinTrek/load.cpp:1603-1606. Ranking is harness policy.</summary>
internal static class TechLoadoutSelector
{
    internal sealed record Selection(short HullId, string HullName, IReadOnlyList<ExpandedPartData> Parts,
        long Price, string Reason, ShowcaseFinale.StrikeLoadout? Strike = null);

    private sealed record Context(StaticCore Core, TechTreeBitMask Techs, byte[]? Local,
        HashSet<short> Hulls, HashSet<short> Parts, long Budget);

    internal static Selection? Select(StaticCore core, short civId, Showcase.HullRole role,
        TechTreeBitMask techs, short dockTypeId, DataStationTypeIGC? target = null, long budget = long.MaxValue)
    {
        if (!core.StationTypes.TryGetValue(dockTypeId, out var dock)) return null;
        return Select(Create(core, civId, techs, dock.TtbmLocal, [dockTypeId], budget), role,
            target ?? (role == Showcase.HullRole.Bomber ? dock : null));
    }

    // Kept for existing socket-free finale catalogue queries with a local mask.
    internal static Selection? SelectAtLocal(StaticCore core, short civId, Showcase.HullRole role,
        TechTreeBitMask techs, byte[]? local, DataStationTypeIGC? target = null)
        => Select(Create(core, civId, techs, local, core.StationTypes.Keys, long.MaxValue,
            techs.Or(TechTreeBitMask.FromBytes(local ?? new byte[50]))), role, target);

    private static Context Create(StaticCore core, short civId, TechTreeBitMask techs,
        byte[]? local, IEnumerable<short> stations, long budget, TechTreeBitMask? queryTechs = null)
    {
        var unlocks = new ResearchTree(core, civId).Unlocks(queryTechs ?? techs, stations);
        bool CanBuy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, local, techs);
        return new(core, techs, local,
            unlocks.Where(i => i.Kind == "hull" && CanBuy(core.HullTypes[i.Id].TtbmRequired)).Select(i => i.Id).ToHashSet(),
            unlocks.Where(i => i.Kind == "part" && CanBuy(Part(core, i.Id).TtbmRequired)).Select(i => i.Id).ToHashSet(), budget);
    }

    // Native has broad flags, not separate scout/interceptor flags. This policy
    // uses combat capabilities first, then scanner reach vs travel speed for
    // small craft. Names, civ IDs and core IDs play no role. A gunship's turrets
    // make it heavy even with the fighter bit. Station threat precedes turrets.
    internal static Showcase.HullRole Classify(DataHullTypeIGC h)
    {
        if (h.HasCapability(DataHullTypeIGC.HabmCarrier) || !h.HasCapability(DataHullTypeIGC.HabmFighter))
            return Showcase.HullRole.Heavy;
        if (h.HasCapability(DataHullTypeIGC.HabmThreatToStation)) return Showcase.HullRole.Bomber;
        if (h.MaxWeapons > h.MaxFixedWeapons) return Showcase.HullRole.Heavy;
        if (h.ScannerRange >= h.Speed * 12) return Showcase.HullRole.Scout;
        if (h.HasCapability(DataHullTypeIGC.HabmNoRipcord)
            || (h.MaxFixedWeapons > 0 && h.MaxAmmo > 0 && h.MaxAmmo <= h.MaxFixedWeapons * 225
                && h.ScannerRange <= h.Speed * 8)) return Showcase.HullRole.Interceptor;
        return Showcase.HullRole.Fighter;
    }

    private static Selection? Select(Context c, Showcase.HullRole role, DataStationTypeIGC? target)
    {
        if (role == Showcase.HullRole.Bomber && target is { } station)
        {
            var strike = SelectBomber(c, station);
            return strike is null ? null : Describe(c, role, strike.HullId, strike.Parts, strike);
        }
        var options = new List<Selection>();
        foreach (var h in c.Core.HullTypes.Values.Where(h => c.Hulls.Contains(h.HullId)
                     && Showcase.IsPlayerBuyable(c.Core, h) && Classify(h) == role
                     && !SuccessorUpgrade.HullIsObsolete(c.Core, h, c.Local, c.Techs)).OrderBy(h => h.HullId))
        {
            var parts = new List<ExpandedPartData>();
            long spent = Math.Max(0, h.Price);
            float energy = h.MaxEnergy;
            int ammo = h.MaxAmmo;
            var legal = c.Parts.Select(id => Part(c.Core, id)).Where(p => p.GroupId != byte.MaxValue
                && !SuccessorUpgrade.PartIsObsolete(c.Core, p, c.Local, c.Techs)).ToArray();
            foreach (var mount in Enumerable.Range(0, Math.Min(8, h.Hardpoints.Length)).Where(m => h.Hardpoints[m].Fixed))
            {
                var gun = legal.Where(p => p.Type is DataPartTypeIGC.Weapon w && h.CanMountPart(p, mount)
                        && w.EnergyPerShot <= energy && w.CAmmoPerShot <= ammo && spent + PartPrice(p, 1) <= c.Budget)
                    .OrderByDescending(p => WeaponScore(c.Core, p, role)).ThenBy(p => p.PartId)
                    .FirstOrDefault(p => WeaponScore(c.Core, p, role) > 0);
                if (gun.Type is null) continue;
                Add(gun, mount, 1);
                var w = (DataPartTypeIGC.Weapon)gun.Type;
                energy -= Math.Max(0, w.EnergyPerShot); ammo -= Math.Max(0, (int)w.CAmmoPerShot);
            }
            if (parts.Count == 0) continue; // automatic steering needs a damaging fixed gun
            foreach (var equipment in new[] { EquipmentType.Shield, EquipmentType.Afterburner,
                         EquipmentType.Magazine, EquipmentType.Dispenser, EquipmentType.ChaffLauncher, EquipmentType.Cloak })
            {
                var support = legal.Where(p => p.EquipmentType == equipment && h.CanMountPart(p, 0))
                    .Select(p => (Part: p, Amount: Ship.ClampPartAmount(h, c.Core, p,
                        equipment is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? short.MaxValue : 1)))
                    .Where(p => p.Amount > 0 && spent + PartPrice(p.Part, p.Amount) <= c.Budget)
                    .OrderByDescending(p => SupportScore(c.Core, p.Part, role)).ThenBy(p => p.Part.PartId)
                    .FirstOrDefault(p => SupportScore(c.Core, p.Part, role) > 0);
                if (support.Part.Type is not null) Add(support.Part, 0, support.Amount);
            }
            options.Add(Describe(c, role, h.HullId, parts));
            void Add(DataPartTypeIGC p, int mount, short amount)
            {
                parts.Add(new(p.PartId, amount, (byte)mount, ExpandedPartData.FullyMounted));
                spent += PartPrice(p, amount);
            }
        }
        return options.OrderByDescending(o => HullScore(c.Core.HullTypes[o.HullId], role))
            .ThenByDescending(o => o.Parts.Sum(p => WeaponScore(c.Core, Part(c.Core, p.PartId), role)))
            .ThenBy(o => o.HullId).FirstOrDefault();
    }

    private static double HullScore(DataHullTypeIGC h, Showcase.HullRole role) => role switch
    {
        Showcase.HullRole.Scout => h.ScannerRange * h.Speed,
        Showcase.HullRole.Interceptor => h.HitPoints * h.Speed,
        _ => h.HitPoints * Math.Max(1, (int)h.MaxFixedWeapons),
    };

    private static double WeaponScore(StaticCore core, DataPartTypeIGC p, Showcase.HullRole role)
    {
        if (p.Type is not DataPartTypeIGC.Weapon w || !core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r)
            || r.Power <= 0) return 0;
        // Mean effectiveness against player fighter armor avoids repair or anti-base guns
        // winning simply because their raw projectile power is large.
        var defenses = core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.HasCapability(DataHullTypeIGC.HabmFighter))
            .Select(h => h.DefenseType).Distinct().ToArray();
        double effectiveness = defenses.Length == 0 ? 1 : defenses.Average(d =>
            r.DamageType < DataConstantsIGC.DmgidMax && d < DataConstantsIGC.DefidMax
                ? Math.Max(0, core.Constants.DamageConstants[r.DamageType][d]) : 1);
        double dps = r.Power * effectiveness / Math.Max(.01f, w.DtimeBurst);
        return role == Showcase.HullRole.Interceptor ? dps * Math.Sqrt(Math.Max(1, r.Speed * r.Lifespan)) : dps;
    }

    private static double SupportScore(StaticCore core, DataPartTypeIGC p, Showcase.HullRole role) => p.Type switch
    {
        DataPartTypeIGC.Shield s => s.MaxStrength,
        DataPartTypeIGC.Afterburner a => a.MaxThrust,
        DataPartTypeIGC.Cloak c => c.MaxCloaking,
        DataPartTypeIGC.MissileLauncher m when core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var t) => t.Power / Math.Max(.01f, t.ReadyTime + t.LockTime),
        DataPartTypeIGC.ProbeLauncher m when core.ProbeTypes.TryGetValue(m.ExpendableTypeId, out var t) => role == Showcase.HullRole.Scout ? t.ScannerRange : 0,
        DataPartTypeIGC.MineLauncher m when core.MineTypes.TryGetValue(m.ExpendableTypeId, out var t) => role == Showcase.HullRole.Scout ? 0 : t.Power,
        DataPartTypeIGC.ChaffLauncher m when core.ChaffTypes.TryGetValue(m.ExpendableTypeId, out var t) => t.Lifespan,
        _ => 0,
    };

    internal static DataPartTypeIGC Part(StaticCore core, short id) => core.PartTypes.TryGetValue(id, out var p) ? p
        : core.TryBuildExpendableLauncher(id, out p) ? p : throw new InvalidOperationException($"unknown part {id}");
    internal static long PartPrice(DataPartTypeIGC p, short amount) => p.Type is DataPartTypeIGC.Pack ? 0
        : p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher
            ? (long)Math.Max(0, p.Price) * amount : Math.Max(0, p.Price);

    internal static bool Legal(StaticCore core, short civ, TechTreeBitMask techs, short dock,
        short hullId, IReadOnlyList<ExpandedPartData> parts)
    {
        if (!core.HullTypes.TryGetValue(hullId, out var h) || !Showcase.IsPlayerBuyable(core, h)) return false;
        var c = Create(core, civ, techs, core.StationTypes[dock].TtbmLocal, [dock], long.MaxValue);
        if (!c.Hulls.Contains(hullId) || SuccessorUpgrade.HullIsObsolete(core, h, c.Local, techs)) return false;
        var mounts = new HashSet<(EquipmentType, byte)>();
        foreach (var p in parts)
        {
            if (!c.Parts.Contains(p.PartId)) return false;
            var type = Part(core, p.PartId);
            if (type.GroupId == byte.MaxValue || !h.CanMountPart(type, unchecked((sbyte)p.MountId))
                || SuccessorUpgrade.PartIsObsolete(core, type, c.Local, techs)
                || p.Amount <= 0 || Ship.ClampPartAmount(h, core, type, p.Amount) != p.Amount
                || !mounts.Add((type.EquipmentType, p.MountId))) return false;
        }
        return true;
    }

    private static Selection Describe(Context c, Showcase.HullRole role, short hullId,
        IReadOnlyList<ExpandedPartData> parts, ShowcaseFinale.StrikeLoadout? strike = null)
    {
        var h = c.Core.HullTypes[hullId];
        long price = Math.Max(0, h.Price) + parts.Sum(p => PartPrice(Part(c.Core, p.PartId), p.Amount));
        string rule = strike is null ? $"hull role score={HullScore(h, role):F2}; fixed guns by anti-ship DPS (interceptors favor reach), support by strength/thrust/role"
            : "station armor/shield volley, then DPS, then anti-ship aim gun; shared energy/ammo budget";
        return new(h.HullId, h.Name, parts, price,
            $"role={role}; techBits={c.Techs.PopCount}; {rule}; stable ID ties; legal nonobsolete human hull {h.HullId} '{h.Name}'; cost={price}; "
            + string.Join(", ", parts.Select(p => $"{Part(c.Core, p.PartId).EquipmentType}@{p.MountId}={p.PartId} '{Part(c.Core, p.PartId).Name}' x{p.Amount}")), strike);
    }
    private static ShowcaseFinale.StrikeLoadout? SelectBomber(Context context, DataStationTypeIGC target)
    {
        var core = context.Core;
        var techs = context.Techs;
        var stationLocal = context.Local;
        var options = new List<ShowcaseFinale.StrikeLoadout>();
        foreach (var hull in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
                     && Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber
                     && context.Hulls.Contains(h.HullId)
                     && !SuccessorUpgrade.HullIsObsolete(core, h, stationLocal, techs)).OrderBy(h => h.HullId))
        {
            // Pareto search across hardpoints: greedy per-gun selection can waste
            // the shared energy/ammo pool and miss a stronger complete volley.
            var frontier = new List<GunSet> { new([], default, default, 0, 0, 0, 0) };
            foreach (var (hp, index) in hull.Hardpoints.Select((h, i) => (h, i)).Where(x => x.h.Fixed))
            {
                var guns = core.PartTypes.Values.Where(p => p.GroupId != byte.MaxValue
                        && context.Parts.Contains(p.PartId)
                        && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs)
                        && p.Type is DataPartTypeIGC.Weapon && (p.PartMask & ~hp.PartMask) == 0)
                    .OrderBy(p => p.PartId).Select(p => (Part: p, Damage: ShowcaseFinale.PartDamage(core, p.PartId, target)))
                    .Where(p => p.Part.Type is DataPartTypeIGC.Weapon w
                        && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var round) && round.Power > 0).ToArray();
                var next = new List<GunSet>();
                foreach (var state in frontier)
                {
                    Insert(next, state); // an empty socket can free resources for a later one
                    foreach (var gun in guns)
                    {
                        var w = (DataPartTypeIGC.Weapon)gun.Part.Type;
                        float energy = state.Energy + Math.Max(0, w.EnergyPerShot);
                        int ammo = state.Ammo + Math.Max(0, (int)w.CAmmoPerShot);
                        long gunPrice = state.Price + PartPrice(gun.Part, 1);
                        if (energy > hull.MaxEnergy || ammo > hull.MaxAmmo || hull.Price + gunPrice > context.Budget) continue;
                        Insert(next, new([.. state.Parts, new(gun.Part.PartId, 1, (byte)index, ExpandedPartData.FullyMounted)],
                            state.Volley + gun.Damage, state.PerSecond + gun.Damage * (1 / Math.Max(.01f, w.DtimeBurst)), energy, ammo, gunPrice, state.AimScore + WeaponScore(core, gun.Part, Showcase.HullRole.Fighter)));
                    }
                }
                frontier = next;
            }
            var missiles = core.Launchers.Keys.Order().Where(id => core.TryBuildExpendableLauncher(id, out var p)
                    && p.EquipmentType == EquipmentType.Magazine && p.GroupId != byte.MaxValue
                    && context.Parts.Contains(p.PartId)
                    && hull.CanMountPart(p) && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs)
                    && p.Type is DataPartTypeIGC.MissileLauncher ml
                    && core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var mt)
                    && hull.CapacityMagazine >= Math.Max(1, (int)mt.LauncherExpendableSize))
                .Select(id => (Id: id, Damage: ShowcaseFinale.PartDamage(core, id, target)))
                .Where(m => m.Damage.Hull > 0).ToArray();
            var combinations = new List<GunSet>();
            foreach (var state in frontier.Where(s => s.Parts.Count > 0))
            {
                combinations.Add(state);
                foreach (var m in missiles)
                {
                    var missile = core.MissileTypes[core.Launchers[m.Id].ExpendabletypeId];
                    short amount = (short)(hull.CapacityMagazine / Math.Max(1, (int)missile.LauncherExpendableSize));
                    combinations.Add(state with { Parts = [.. state.Parts, new(m.Id, amount, 0, ExpandedPartData.FullyMounted)],
                        Volley = state.Volley + m.Damage,
                        PerSecond = state.PerSecond + m.Damage * (1 / Math.Max(.01f, missile.ReadyTime + missile.LockTime)),
                        Price = state.Price + PartPrice(Part(core, m.Id), amount) });
                }
            }
            var best = combinations.Where(s => s.Volley.Hull > 0 && hull.Price + s.Price <= context.Budget).OrderByDescending(s => s.Volley.Score(target))
                .ThenByDescending(s => s.PerSecond.Score(target)).ThenByDescending(s => s.AimScore).FirstOrDefault();
            if (best is null) continue; // fixed aim gun plus station damage from guns or missiles
            var parts = best.Parts.ToList();
            var dps = best.PerSecond;
            // Role-compatible support keeps the purchased bomber viable.
            foreach (var equipment in new[] { EquipmentType.Shield, EquipmentType.Afterburner, EquipmentType.Dispenser, EquipmentType.ChaffLauncher, EquipmentType.Cloak })
            {
                long spent = hull.Price + parts.Sum(p => PartPrice(Part(core, p.PartId), p.Amount));
                var fitting = context.Parts.Select(id => Part(core, id)).Where(p => p.GroupId != byte.MaxValue
                    && p.EquipmentType == equipment && hull.CanMountPart(p, 0)
                    && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs))
                    .Select(p => (Part: p, Amount: Ship.ClampPartAmount(hull, core, p,
                        equipment is EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? short.MaxValue : 1)))
                    .Where(p => p.Amount > 0 && spent + PartPrice(p.Part, p.Amount) <= context.Budget)
                    .OrderByDescending(p => SupportScore(core, p.Part, Showcase.HullRole.Bomber)).ThenBy(p => p.Part.PartId)
                    .FirstOrDefault(p => SupportScore(core, p.Part, Showcase.HullRole.Bomber) > 0);
                if (fitting.Part.Type is not null) parts.Add(new(fitting.Part.PartId, fitting.Amount, 0, ExpandedPartData.FullyMounted));
            }
            long price = hull.Price;
            var names = new List<string>();
            foreach (var p in parts)
            {
                if (!core.PartTypes.TryGetValue(p.PartId, out var type)) core.TryBuildExpendableLauncher(p.PartId, out type);
                price += PartPrice(type, p.Amount);
                names.Add($"{p.PartId}: {type.Name} (mount {p.MountId}, amount {p.Amount})");
            }
            if (price > context.Budget) continue;
            options.Add(new(hull.HullId, hull.Name, target.StationTypeId, target.Name,
                parts, ShowcaseFinale.Volley(core, parts, target), dps, (int)Math.Min(int.MaxValue, price), names));
        }
        return options.OrderByDescending(o => o.Volley.Score(target)).ThenByDescending(o => o.PerSecond.Score(target))
            .ThenBy(o => o.HullId).FirstOrDefault();
    }

    private sealed record GunSet(IReadOnlyList<ExpandedPartData> Parts, ShowcaseFinale.Damage Volley,
        ShowcaseFinale.Damage PerSecond, float Energy, int Ammo, long Price, double AimScore);

    private static void Insert(List<GunSet> frontier, GunSet candidate)
    {
        bool Dominates(GunSet a, GunSet b) => (a.Parts.Count > 0 || b.Parts.Count == 0)
            && a.AimScore >= b.AimScore && a.Price <= b.Price && a.Energy <= b.Energy && a.Ammo <= b.Ammo
            && a.Volley.Hull >= b.Volley.Hull && a.Volley.Shield >= b.Volley.Shield
            && a.PerSecond.Hull >= b.PerSecond.Hull && a.PerSecond.Shield >= b.PerSecond.Shield;
        if (frontier.Any(s => Dominates(s, candidate))) return;
        frontier.RemoveAll(s => Dominates(candidate, s));
        frontier.Add(candidate);
    }

}
