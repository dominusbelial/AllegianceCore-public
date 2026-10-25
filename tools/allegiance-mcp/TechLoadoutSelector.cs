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

    /// <summary>P814b — what a strike (bomber) loadout should optimise its hull
    /// for. The VISIBLE strike element picks a turret-capable carrier (so a bot
    /// gunner has a seat to board — the whole point of P814); P801 lone wolves
    /// keep the best cloak hull.</summary>
    internal enum StrikeHullPref
    {
        /// <summary>The best overall strike hull (cloak-priority) — used for P801 lone wolves.</summary>
        Best,
        /// <summary>Prefer a TURRET-capable strike hull (MaxWeapons &gt; MaxFixedWeapons,
        /// a carrier with seats for gunners) when its strike value is within
        /// <see cref="TurretStrikeMargin"/> of the best strike hull — the visible
        /// element's bomber (P814b scope 1). Falls back to the best hull when no
        /// turret-capable strike hull qualifies.</summary>
        TurretCarrier,
    }

    /// <summary>P814b — the default margin a visible-element turret bomber's
    /// strike value may trail the best strike hull by and still be chosen
    /// (0.25 = 25%). Tunable via <c>ALLEGIANCE_TURRET_STRIKE_MARGIN</c>.</summary>
    internal static double TurretStrikeMargin =>
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_TURRET_STRIKE_MARGIN"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)
            && m > 0 && m <= 1 ? m : 0.25;

    internal static Selection? Select(StaticCore core, short civId, Showcase.HullRole role,
        TechTreeBitMask techs, short dockTypeId, DataStationTypeIGC? target = null, long budget = long.MaxValue,
        bool preferMines = false, bool rescueProbe = false,
        StrikeHullPref strikePref = StrikeHullPref.Best)
    {
        if (!core.StationTypes.TryGetValue(dockTypeId, out var dock)) return null;
        return Select(Create(core, civId, techs, dock.TtbmLocal, [dockTypeId], budget), role,
            target ?? (role == Showcase.HullRole.Bomber ? dock : null), preferMines, rescueProbe, strikePref);
    }

    /// <summary>P779b-1 — the rescue-probe launcher part a side can actually buy:
    /// among the core's probe launchers whose <c>DataProbeTypeIGC</c> carries
    /// <c>c_eabmRescue</c> or <c>c_eabmRescueAny</c> (igc.h:781-782) and whose
    /// <c>LauncherTtbmRequired</c> is a subset of the side's techs, the longest
    /// <c>Lifespan</c>, then the highest <c>HitPoints</c> (the P779b-1 medevac
    /// ranking), ties by part id. null = this civ cannot buy a rescue probe
    /// (Ga'Taraan in PCore001), so the side has no medevac scout. Reads only
    /// core data — never a civ name or id.</summary>
    internal static short? PickRescueProbeLauncher(StaticCore core, TechTreeBitMask techs)
    {
        short? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var launcher in core.Launchers.Values.OrderBy(l => l.PartId))
        {
            if (!core.ProbeTypes.TryGetValue(launcher.ExpendabletypeId, out var probe)) continue;
            if ((probe.EabmCapabilities & 0x6000) == 0) continue;
            if (!TechTreeBitMask.FromBytes(probe.LauncherTtbmRequired).IsSubsetOf(techs)) continue;
            double score = probe.Lifespan * 1000 + Math.Min(999f, probe.HitPoints);
            if (score <= bestScore) continue;
            bestScore = score; best = launcher.PartId;
        }
        return best;
    }

    // ── P796 — loadout variety within legality ─────────────────────────────

    /// <summary>Immutable per-side usage snapshot the pure varied selector
    /// consumes: how many times each hull and part id has already been equipped
    /// on this side (P796 least-recently-used coverage state), the side id, and
    /// a deterministic (seed, ordinal) pair so the same inputs always produce
    /// the same selection. No civ names or ids play a role — purely data.</summary>
    internal sealed record VarietyState(short Side, long Seed, long Ordinal,
        IReadOnlyDictionary<short, int> HullUse, IReadOnlyDictionary<short, int> PartUse);

    /// <summary>P796 — the varied purchase policy: the SAME legality as
    /// <see cref="Select"/> (research unlocks, station local mask, successor
    /// obsolescence, mount masks, energy/ammo/budget), but instead of the
    /// per-role argmax it spreads purchases across every legal hull and part:
    /// the least-recently-used group wins, and within a group the choice is a
    /// seeded weighted pick that favours the higher-scoring options without
    /// starving the rest. Role rules keep the best-path behaviour: bombers
    /// keep the anti-base strike loadout, support pilots keep the nanite
    /// selector, medevac scouts keep the rescue-probe ranking (the caller
    /// routes those to <see cref="Select"/>).
    /// Deterministic: (state, seed, ordinal) → selection.</summary>
    internal static Selection? SelectVaried(StaticCore core, short civId, Showcase.HullRole role,
        TechTreeBitMask techs, short dockTypeId, VarietyState variety,
        DataStationTypeIGC? target = null, long budget = long.MaxValue, bool preferMines = false)
    {
        // Finale/strike (bomber vs a real station target) keeps today's best
        // loadout — a mandated P796 scope rule. Other roles ignore the target
        // exactly like the best selector does.
        if (role == Showcase.HullRole.Bomber) return Select(core, civId, role, techs, dockTypeId, target, budget, preferMines);
        if (!core.StationTypes.TryGetValue(dockTypeId, out var dock)) return null;
        var c = Create(core, civId, techs, dock.TtbmLocal, [dockTypeId], budget);
        var hulls = c.Core.HullTypes.Values.Where(h => c.Hulls.Contains(h.HullId)
            && Showcase.IsPlayerBuyable(c.Core, h) && Classify(h) == role
            && !SuccessorUpgrade.HullIsObsolete(c.Core, h, c.Local, c.Techs)).OrderBy(h => h.HullId).ToArray();
        if (hulls.Length == 0) return null;

        var rng = new Random(unchecked((int)StableHash(variety.Seed, variety.Ordinal, civId, dockTypeId, (short)role)));
        // Hull pick: least-used group first; weighted inside the group so the
        // better hulls of the group are favoured (weight 1..4) but no legal
        // hull is starved.
        var hullWeights = hulls.Select(h => (Hull: h, Use: variety.HullUse.GetValueOrDefault(h.HullId)))
            .GroupBy(x => x.Use).OrderBy(g => g.Key).First()
            .Select(x => (Hull: x.Hull, Weight: 1 + 3 * HullScore(x.Hull, role) / Math.Max(1e-9, hulls.Max(h2 => HullScore(h2, role)))))
            .ToList();
        var hull = WeightedPick(rng, hullWeights);

        var parts = new List<ExpandedPartData>();
        long spent = Math.Max(0, hull.Price);
        float energy = hull.MaxEnergy;
        int ammo = hull.MaxAmmo;
        var legal = c.Parts.Select(id => Part(c.Core, id)).Where(p => p.GroupId != byte.MaxValue
            && !SuccessorUpgrade.PartIsObsolete(c.Core, p, c.Local, c.Techs)).ToArray();
        var mounts = Enumerable.Range(0, Math.Min(8, hull.Hardpoints.Length)).Where(m => hull.Hardpoints[m].Fixed).ToArray();

        // Primary gun: least-used fitting weapon across ALL fixed mounts (rotates
        // every legal gun into service), weighted by the role DPS score inside
        // the least-used group; its mount is the first one it fits.
        var primaryCandidates = legal.Where(p => p.Type is DataPartTypeIGC.Weapon pw
                && WeaponScore(c.Core, p, role) > 0
                && pw.EnergyPerShot <= energy && pw.CAmmoPerShot <= ammo
                && spent + PartPrice(p, 1) <= c.Budget)
            .Select(p => (Part: p, Mount: mounts.FirstOrDefault(m => hull.CanMountPart(p, m))))
            .Where(x => hull.CanMountPart(x.Part, x.Mount)).ToArray();
        if (primaryCandidates.Length == 0) return null; // automatic steering needs a damaging fixed gun
        var primary = WeightedPick(rng, LeastUsedGroup(primaryCandidates,
            x => variety.PartUse.GetValueOrDefault(x.Part.PartId),
            x => WeaponScore(c.Core, x.Part, role)).ToList());
        Add(primary.Part, primary.Mount, 1);
        var primaryWeapon = (DataPartTypeIGC.Weapon)primary.Part.Type;
        energy -= Math.Max(0, primaryWeapon.EnergyPerShot);
        ammo -= Math.Max(0, (int)primaryWeapon.CAmmoPerShot);

        // Remaining mounts keep the best-path DPS pick from the leftover pool.
        foreach (var mount in mounts.Where(m => m != primary.Mount))
        {
            var gun = legal.Where(p => p.Type is DataPartTypeIGC.Weapon w && hull.CanMountPart(p, mount)
                    && w.EnergyPerShot <= energy && w.CAmmoPerShot <= ammo && spent + PartPrice(p, 1) <= c.Budget)
                .OrderByDescending(p => WeaponScore(c.Core, p, role)).ThenBy(p => p.PartId)
                .FirstOrDefault(p => WeaponScore(c.Core, p, role) > 0);
            if (gun.Type is null) continue;
            Add(gun, mount, 1);
            var w = (DataPartTypeIGC.Weapon)gun.Type;
            energy -= Math.Max(0, w.EnergyPerShot); ammo -= Math.Max(0, (int)w.CAmmoPerShot);
        }
        // P814 — a turret-capable hull also arms its TURRET mounts so a bot
        // carrier has crewed seats to offer gunners (native BoardShip requires an
        // equipped slot, FedSrv.CPP:2377-2392). P796 variety/coverage unchanged:
        // the turret gun rotates the same least-used legality gates.
        ArmTurretMounts(c.Core, hull, legal, parts, ref energy, ref ammo, ref spent, c.Budget);
        if (parts.Count == 0) return null;

        // Equipment: same order and gates as the best path (scouts reserve their
        // budget for stealth first), but each slot rotates the least-used legal
        // fitting part instead of always mounting the strongest.
        var equipmentOrder = role == Showcase.HullRole.Scout && !preferMines
            ? new[] { EquipmentType.Cloak, EquipmentType.Shield, EquipmentType.Afterburner,
                EquipmentType.Magazine, EquipmentType.Dispenser, EquipmentType.ChaffLauncher }
            : new[] { EquipmentType.Shield, EquipmentType.Afterburner, EquipmentType.Magazine,
                EquipmentType.Dispenser, EquipmentType.ChaffLauncher, EquipmentType.Cloak };
        foreach (var equipment in equipmentOrder)
        {
            var candidates = legal.Where(p => p.EquipmentType == equipment && hull.CanMountPart(p, 0))
                .Select(p => (Part: p, Amount: Ship.ClampPartAmount(hull, c.Core, p,
                    equipment is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? short.MaxValue : 1)))
                .Where(p => p.Amount > 0 && spent + PartPrice(p.Part, p.Amount) <= c.Budget)
                .Where(p => SupportScore(c.Core, p.Part, role, preferMines) > 0).ToArray();
            if (candidates.Length == 0) continue;
            var pick = WeightedPick(rng, LeastUsedGroup(candidates,
                x => variety.PartUse.GetValueOrDefault(x.Part.PartId),
                x => SupportScore(c.Core, x.Part, role, preferMines)).ToList());
            Add(pick.Part, 0, pick.Amount);
        }
        var selected = Describe(c, role, hull.HullId, parts);
        return selected with { Reason = $"varied seed={variety.Seed} ord={variety.Ordinal} hullUse={variety.HullUse.GetValueOrDefault(hull.HullId)}; " + selected.Reason };

        void Add(DataPartTypeIGC p, int mount, short amount)
        {
            parts.Add(new(p.PartId, amount, (byte)mount, ExpandedPartData.FullyMounted));
            spent += PartPrice(p, amount);
        }
    }

    /// <summary>The subset of candidates with the minimal usage count — the
    /// least-recently-equipped group the weighted pick rotates through.</summary>
    private static IEnumerable<(T Item, double Weight)> LeastUsedGroup<T>(IReadOnlyCollection<T> candidates,
        Func<T, int> use, Func<T, double> score)
    {
        int min = candidates.Count == 0 ? 0 : candidates.Min(use);
        var group = candidates.Where(x => use(x) == min).ToArray();
        double max = Math.Max(1e-9, group.Max(score));
        return group.Select(x => (Item: x, Weight: 1 + 3 * score(x) / max));
    }

    private static T WeightedPick<T>(Random rng, IReadOnlyList<(T Item, double Weight)> items)
    {
        double total = items.Sum(i => Math.Max(0, i.Weight));
        double roll = rng.NextDouble() * total;
        foreach (var (item, weight) in items)
        {
            roll -= Math.Max(0, weight);
            if (roll <= 0) return item;
        }
        return items[^1].Item;
    }

    private static long StableHash(long a, long b, short c, short d, short e)
    {
        // FNV-1a over the deterministic inputs — no Random-on-string, no time.
        ulong h = 1469598103934665603UL;
        Span<long> inputs = stackalloc long[] { a, b, c, d, e };
        foreach (long v in inputs)
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        return (long)h;
    }

    // Kept for existing socket-free finale catalogue queries with a local mask.
    internal static Selection? SelectAtLocal(StaticCore core, short civId, Showcase.HullRole role,
        TechTreeBitMask techs, byte[]? local, DataStationTypeIGC? target = null,
        StrikeHullPref strikePref = StrikeHullPref.Best)
        => Select(Create(core, civId, techs, local, core.StationTypes.Keys, long.MaxValue,
            techs.Or(TechTreeBitMask.FromBytes(local ?? new byte[50]))), role, target,
            strikePref: strikePref);

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

    private static Selection? Select(Context c, Showcase.HullRole role, DataStationTypeIGC? target,
        bool preferMines = false, bool rescueProbe = false, StrikeHullPref strikePref = StrikeHullPref.Best)
    {
        if (role == Showcase.HullRole.Bomber && target is { } station)
        {
            var strike = SelectBomber(c, station, pref: strikePref);
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
            // P814 — arm turret mounts on turret-capable hulls so a bot carrier
            // can crew them (native BoardShip needs an equipped slot,
            // FedSrv.CPP:2377-2392). Anti-fighter DPS ranking: gunners defend.
            ArmTurretMounts(c.Core, h, legal, parts, ref energy, ref ammo, ref spent, c.Budget);
            if (parts.Count == 0) continue; // automatic steering needs a damaging fixed gun
            // Scouts reserve their optional-equipment budget for stealth first;
            // dedicated mine layers retain their dispenser purchase policy.
            var equipmentOrder = role == Showcase.HullRole.Scout && !preferMines
                ? new[] { EquipmentType.Cloak, EquipmentType.Shield, EquipmentType.Afterburner,
                    EquipmentType.Magazine, EquipmentType.Dispenser, EquipmentType.ChaffLauncher }
                : new[] { EquipmentType.Shield, EquipmentType.Afterburner, EquipmentType.Magazine,
                    EquipmentType.Dispenser, EquipmentType.ChaffLauncher, EquipmentType.Cloak };
            foreach (var equipment in equipmentOrder)
            {
                var support = legal.Where(p => p.EquipmentType == equipment && h.CanMountPart(p, 0))
                    .Select(p => (Part: p, Amount: Ship.ClampPartAmount(h, c.Core, p,
                        equipment is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? short.MaxValue : 1)))
                    .Where(p => p.Amount > 0 && spent + PartPrice(p.Part, p.Amount) <= c.Budget)
                    .OrderByDescending(p => preferMines && p.Part.Type is DataPartTypeIGC.MineLauncher)
                    .ThenByDescending(p => SupportScore(c.Core, p.Part, role, preferMines, rescueProbe)).ThenBy(p => p.Part.PartId)
                    .FirstOrDefault(p => SupportScore(c.Core, p.Part, role, preferMines, rescueProbe) > 0);
                if (support.Part.Type is not null) Add(support.Part, 0, support.Amount);
            }
            options.Add(Describe(c, role, h.HullId, parts));
            void Add(DataPartTypeIGC p, int mount, short amount)
            {
                parts.Add(new(p.PartId, amount, (byte)mount, ExpandedPartData.FullyMounted));
                spent += PartPrice(p, amount);
            }
        }
        // A scanner/speed score alone permanently favored uncloaked scouts over
        // legal stealth hulls. Prefer an actually purchased cloak, not a socket
        // or an unresearched part. Native gates above still decide eligibility.
        // The showcase's Heavy slot is a GUNSHIP (fighter-class hull with turrets, a
        // seat bot gunners board); capital ships (non-fighter / carrier hulls) also
        // classify Heavy but fly and dock differently — use one only when the side
        // has no gunship.
        if (role == Showcase.HullRole.Heavy && options.Any(o => IsGunship(c.Core, o.HullId)))
            options.RemoveAll(o => !IsGunship(c.Core, o.HullId));
        return options.OrderByDescending(o => role == Showcase.HullRole.Scout && !preferMines && HasCloak(c.Core, o.Parts))
            .ThenByDescending(o => HullScore(c.Core.HullTypes[o.HullId], role))
            .ThenByDescending(o => o.Parts.Sum(p => WeaponScore(c.Core, Part(c.Core, p.PartId), role)))
            .ThenBy(o => o.HullId).FirstOrDefault();
    }

    // Repair identity is projectile power < 0 (Igc/shipIGC.cpp:2608).
    // Use the same per-dock research, successor, mount and wallet gates as combat.
    internal static Selection? SelectSupport(StaticCore core, short civ, TechTreeBitMask techs,
        short dockType, long budget)
    {
        if (!core.StationTypes.TryGetValue(dockType, out var dock)) return null;
        var c = Create(core, civ, techs, dock.TtbmLocal, [dockType], budget);
        var options = new List<Selection>();
        foreach (var h in core.HullTypes.Values.Where(h => c.Hulls.Contains(h.HullId)
            && h.MaxFixedWeapons > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed
            && Showcase.IsPlayerBuyable(core, h) && h.HasCapability(DataHullTypeIGC.HabmFighter)
            && !SuccessorUpgrade.HullIsObsolete(core, h, c.Local, techs)))
        {
            var legal = c.Parts.Select(id => Part(core, id)).Where(p => p.GroupId != byte.MaxValue
                && !SuccessorUpgrade.PartIsObsolete(core, p, c.Local, techs)).ToArray();
            // Native AI uses mount zero; player hulls may expose the repair socket
            // on another fixed mount (Ga'Taraan scout). Never mix positive-damage
            // guns into AllWeapons on a friendly target (shipIGC.cpp:2608,2673-2676).
            var gun = legal.Where(p => Enumerable.Range(0, h.Hardpoints.Length).Any(m => h.Hardpoints[m].Fixed && h.CanMountPart(p, m)) && p.Type is DataPartTypeIGC.Weapon w
                    && w.EnergyPerShot <= h.MaxEnergy && w.CAmmoPerShot <= h.MaxAmmo
                    && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r) && r.Power < 0
                    && r.Speed > 0 && r.Lifespan > 0 && h.Price + PartPrice(p, 1) <= budget)
                .OrderByDescending(p => -core.ProjectileTypes[((DataPartTypeIGC.Weapon)p.Type).ProjectileTypeId].Power
                    / Math.Max(.01f, ((DataPartTypeIGC.Weapon)p.Type).DtimeBurst)).ThenBy(p => p.PartId).FirstOrDefault();
            if (gun.Type is null) continue;
            byte mount = (byte)Enumerable.Range(0, h.Hardpoints.Length).First(m => h.Hardpoints[m].Fixed && h.CanMountPart(gun, m));
            var parts = new List<ExpandedPartData> { new(gun.PartId, 1, mount, ExpandedPartData.FullyMounted) };
            long price = Math.Max(0, h.Price) + PartPrice(gun, 1);
            foreach (var equipment in new[] { EquipmentType.Shield, EquipmentType.Afterburner })
            {
                var extra = legal.Where(p => p.EquipmentType == equipment && h.CanMountPart(p, 0)
                        && price + PartPrice(p, 1) <= budget)
                    .OrderByDescending(p => SupportScore(core, p, Showcase.HullRole.Fighter)).ThenBy(p => p.PartId).FirstOrDefault();
                if (extra.Type is null) continue;
                parts.Add(new(extra.PartId, 1, 0, ExpandedPartData.FullyMounted)); price += PartPrice(extra, 1);
            }
            var selected = Describe(c, Showcase.HullRole.Fighter, h.HullId, parts);
            options.Add(selected with { Reason = $"role=Support; negative-power lead gun; cheapest legal nonobsolete human hull; techBits={techs.PopCount}; stable ID ties; hull={h.HullId} '{h.Name}'; cost={selected.Price}; "
                + string.Join(", ", parts.Select(p => $"{Part(core, p.PartId).EquipmentType}@{p.MountId}={p.PartId} '{Part(core, p.PartId).Name}' x{p.Amount}")) });
        }
        return options.OrderBy(o => o.Price).ThenBy(o => o.HullId).FirstOrDefault();
    }

    internal static double HullScore(DataHullTypeIGC h, Showcase.HullRole role) => role switch
    {
        Showcase.HullRole.Scout => h.ScannerRange * h.Speed,
        Showcase.HullRole.Interceptor => h.HitPoints * h.Speed,
        _ => h.HitPoints * Math.Max(1, (int)h.MaxFixedWeapons),
    };

    /// <summary>P814 — arm a turret-capable hull's TURRET mounts
    /// (<c>[MaxFixedWeapons, Min(MaxWeapons, Hardpoints.Length))</c>) with legal
    /// weapons, one per mount, per <c>CanMountPart</c> on that mount. The fixed
    /// mounts are untouched; turrets are armed IN ADDITION, drawing from the same
    /// shared energy/ammo/budget pool (so a bot that arms its turrets has less
    /// simultaneous fixed-gun capacity, exactly as a real hull would). This is the
    /// loadout prerequisite for turret gunners: native <c>BoardShip</c> only lets a
    /// child occupy a turret slot that is BOTH unoccupied and EQUIPPED
    /// (<c>FedSrv.CPP:2377-2392</c>, the <c>ppl != NULL</c> check), so without a
    /// weapon in the mount a gunner can never even sit there. Ranked by
    /// <see cref="WeaponScore"/> at the <see cref="Showcase.HullRole.Fighter"/>
    /// weighting — a turret's job is defending the carrier (fighters first), so
    /// anti-fighter DPS is the right ordering for the seats that crew it.
    /// Tech/price/research legality comes from <paramref name="legal"/> exactly as
    /// the fixed-gun loop treats it — nothing is unlocked here.</summary>
    internal static void ArmTurretMounts(StaticCore core, DataHullTypeIGC h,
        IEnumerable<DataPartTypeIGC> legal, List<ExpandedPartData> parts,
        ref float energy, ref int ammo, ref long spent, long budget)
    {
        if (h.MaxWeapons <= h.MaxFixedWeapons) return;               // no turret mounts
        int end = Math.Min(h.MaxWeapons, Math.Min(8, h.Hardpoints.Length));
        for (int mount = h.MaxFixedWeapons; mount < end; mount++)
        {
            // Copy the mutable pool into locals for the LINQ predicate (ref
            // parameters may not be captured by a lambda).
            float e = energy; int a = ammo; long s = spent;
            var gun = legal.Where(p => p.Type is DataPartTypeIGC.Weapon w
                    && h.CanMountPart(p, mount)
                    && w.EnergyPerShot <= e && w.CAmmoPerShot <= a
                    && s + PartPrice(p, 1) <= budget)
                .OrderByDescending(p => WeaponScore(core, p, Showcase.HullRole.Fighter))
                .ThenBy(p => p.PartId).FirstOrDefault();
            if (gun.Type is null) continue;
            parts.Add(new(gun.PartId, 1, (byte)mount, ExpandedPartData.FullyMounted));
            spent += PartPrice(gun, 1);
            var gw = (DataPartTypeIGC.Weapon)gun.Type;
            energy -= Math.Max(0, gw.EnergyPerShot);
            ammo -= Math.Max(0, (int)gw.CAmmoPerShot);
        }
    }

    internal static double WeaponScore(StaticCore core, DataPartTypeIGC p, Showcase.HullRole role)
    {
        if (p.Type is not DataPartTypeIGC.Weapon w || !core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r)
            || r.Power <= 0) return 0;
        // Mean effectiveness against player fighter armor avoids repair or anti-base guns
        // winning simply because their raw projectile power is large. The defenses table
        // depends only on the core — computed once per core instance (pure; P796 made this
        // a cache so the varied rotation stays inside the Fast-tier time budget).
        double[] rates = DefenseRates(core);
        double dps = r.Power * (r.DamageType < DataConstantsIGC.DmgidMax ? rates[r.DamageType] : 1) / Math.Max(.01f, w.DtimeBurst);
        return role == Showcase.HullRole.Interceptor ? dps * Math.Sqrt(Math.Max(1, r.Speed * r.Lifespan)) : dps;
    }

    private static (StaticCore Core, double[] Rates) _defenseRates;
    private static double[] DefenseRates(StaticCore core)
    {
        if (_defenseRates.Core == core && _defenseRates.Rates is { Length: > 0 }) return _defenseRates.Rates;
        var defenses = core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.HasCapability(DataHullTypeIGC.HabmFighter))
            .Select(h => h.DefenseType).Distinct().ToArray();
        var rates = new double[DataConstantsIGC.DmgidMax];
        for (int d = 0; d < rates.Length; d++)
            rates[d] = defenses.Length == 0 ? 1
                : defenses.Average(def => def < DataConstantsIGC.DefidMax ? Math.Max(0, core.Constants.DamageConstants[d][def]) : 1);
        _defenseRates = (core, rates);
        return rates;
    }

    // P779b-1 — `rescueProbe` is the medevac scout's exception: a rescue-capable
    // probe launcher (c_eabmRescue/RescueAny) scores by the medevac ranking
    // (Lifespan first, then HitPoints, encoded as Lifespan*1000+HP) instead of
    // the 0 the scouting branch gives it; every other bot keeps the exclusion.
    internal static double SupportScore(StaticCore core, DataPartTypeIGC p, Showcase.HullRole role,
        bool preferMines = false, bool rescueProbe = false) => p.Type switch
    {
        DataPartTypeIGC.Shield s => s.MaxStrength,
        DataPartTypeIGC.Afterburner a => a.MaxThrust,
        DataPartTypeIGC.Cloak c => c.MaxCloaking,
        DataPartTypeIGC.MissileLauncher m when core.MissileTypes.TryGetValue(m.ExpendableTypeId, out var t) => t.Power / Math.Max(.01f, t.ReadyTime + t.LockTime),
        DataPartTypeIGC.ProbeLauncher m when core.ProbeTypes.TryGetValue(m.ExpendableTypeId, out var t) =>
            role != Showcase.HullRole.Scout ? 0
            : (t.EabmCapabilities & 0x6000) != 0
                ? (rescueProbe ? t.Lifespan * 1000 + Math.Min(999f, t.HitPoints) : 0)
                : (rescueProbe ? 0 : t.ScannerRange),
        DataPartTypeIGC.MineLauncher m when core.MineTypes.TryGetValue(m.ExpendableTypeId, out var t) => role == Showcase.HullRole.Scout && !preferMines ? 0 : t.Power,
        DataPartTypeIGC.ChaffLauncher m when core.ChaffTypes.TryGetValue(m.ExpendableTypeId, out var t) => t.Lifespan,
        _ => 0,
    };

    internal static DataPartTypeIGC Part(StaticCore core, short id) => core.PartTypes.TryGetValue(id, out var p) ? p
        : core.TryBuildExpendableLauncher(id, out p) ? p : throw new InvalidOperationException($"unknown part {id}");
    private static bool HasCloak(StaticCore core, IReadOnlyList<ExpandedPartData> parts)
        => parts.Any(p => p.MountId == 0 && p.Amount > 0
            && Part(core, p.PartId).Type is DataPartTypeIGC.Cloak { MaxCloaking: > 0 });
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
        string rule = strike is null ? $"scouts prefer mounted cloak; hull role score={HullScore(h, role):F2}; fixed guns by anti-ship DPS (interceptors favor reach), support by strength/thrust/role"
            : "bombers prefer mounted cloak; station armor/shield volley, then DPS, then anti-ship aim gun; shared energy/ammo budget";
        return new(h.HullId, h.Name, parts, price,
            $"role={role}; techBits={c.Techs.PopCount}; {rule}; stable ID ties; legal nonobsolete human hull {h.HullId} '{h.Name}'; cost={price}; "
            + string.Join(", ", parts.Select(p => $"{Part(c.Core, p.PartId).EquipmentType}@{p.MountId}={p.PartId} '{Part(c.Core, p.PartId).Name}' x{p.Amount}")), strike);
    }
    // Same exhaustive station-damage ranking and native purchase gates as the bomber selector.
    // Research finales may use another human combat hull while bomber tech is unavailable.
    internal static Selection? SelectAvailableStrike(StaticCore core, short civId, TechTreeBitMask techs,
        short dockTypeId, DataStationTypeIGC target, long budget)
    {
        if (!core.StationTypes.TryGetValue(dockTypeId, out var dock)) return null;
        var context = Create(core, civId, techs, dock.TtbmLocal, [dockTypeId], budget);
        var strike = SelectBomber(context, target, anyCombatHull: true);
        if (strike is null) return null;
        var selected = Describe(context, Showcase.HullRole.Bomber, strike.HullId, strike.Parts, strike);
        return selected with { Reason = "research finale: no legal affordable bomber; best available station-damage loadout; " + selected.Reason };
    }

    private static ShowcaseFinale.StrikeLoadout? SelectBomber(Context context, DataStationTypeIGC target,
        bool anyCombatHull = false, StrikeHullPref pref = StrikeHullPref.Best)
    {
        var core = context.Core;
        var techs = context.Techs;
        var stationLocal = context.Local;
        var options = new List<ShowcaseFinale.StrikeLoadout>();
        foreach (var hull in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
                     && (anyCombatHull || Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber)
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
            // P814 — a bomber is a prime turret carrier: arm its turret mounts too
            // so gunners can crew them (native BoardShip needs an equipped slot,
            // FedSrv.CPP:2377-2392). Drawn from the LEFTOVER energy/ammo/budget, so
            // the station volley (fixed mounts) is untouched; the turret guns are
            // the crew's defensive anti-fighter weapons.
            float turretEnergy = hull.MaxEnergy - best.Energy;
            int turretAmmo = hull.MaxAmmo - best.Ammo;
            long turretSpent = hull.Price + best.Price;
            var turretLegal = context.Parts.Select(id => Part(core, id))
                .Where(p => p.GroupId != byte.MaxValue
                    && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs)).ToArray();
            ArmTurretMounts(core, hull, turretLegal, parts,
                ref turretEnergy, ref turretAmmo, ref turretSpent, context.Budget);
            // Role-compatible support keeps the purchased bomber viable.
            foreach (var equipment in new[] { EquipmentType.Cloak, EquipmentType.Shield, EquipmentType.Afterburner, EquipmentType.Dispenser, EquipmentType.ChaffLauncher })
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
        // P814b scope 1 — the VISIBLE element's bomber flies a TURRET-capable
        // strike hull (a carrier a bot gunner can board) when such a hull's
        // strike value is within TurretStrikeMargin of the best strike hull.
        // Lone wolves (P801) keep the cloak-priority Best hull below. When no
        // turret-capable strike hull qualifies, fall back to the best hull.
        if (pref == StrikeHullPref.TurretCarrier)
        {
            double bestScore = options.Count == 0 ? 0 : options.Max(o => o.Volley.Score(target));
            var turret = options
                .Where(o => TurretCapacity(core, o.HullId) > 0
                    && o.Volley.Score(target) >= bestScore * (1.0 - TurretStrikeMargin))
                .OrderByDescending(o => o.Volley.Score(target))
                .ThenByDescending(o => o.PerSecond.Score(target))
                .ThenBy(o => o.HullId).FirstOrDefault();
            if (turret is not null) return turret;
        }
        // Preserve the strongest-strike fallback across other combat roles.
        // For true bomber duty, stealth is useful on the station approach.
        return options.OrderByDescending(o => !anyCombatHull && HasCloak(core, o.Parts))
            .ThenByDescending(o => o.Volley.Score(target)).ThenByDescending(o => o.PerSecond.Score(target))
            .ThenBy(o => o.HullId).FirstOrDefault();
    }

    /// <summary>P814b — the number of TURRET mounts a hull has
    /// (<c>MaxWeapons - MaxFixedWeapons</c>; 0 = no seats for gunners). The
    /// same measure the client uses for free-turret reporting
    /// (teleportpane.cpp:520-531).</summary>
    private static bool IsGunship(StaticCore core, short hullId)
        => core.HullTypes.TryGetValue(hullId, out var h) && h.HasCapability(DataHullTypeIGC.HabmFighter)
            && !h.HasCapability(DataHullTypeIGC.HabmCarrier) && h.MaxWeapons > h.MaxFixedWeapons;

    private static int TurretCapacity(StaticCore core, short hullId)
        => core.HullTypes.TryGetValue(hullId, out var h) ? Math.Max(0, h.MaxWeapons - h.MaxFixedWeapons) : 0;

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
