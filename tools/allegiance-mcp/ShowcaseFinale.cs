using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

// Socket-free policy. The controller supplies observations from participating clients only.
internal static class ShowcaseFinale
{
    internal sealed record Settings(double AfterMinutes = 30, string Side = "leader",
        double MinRelaunchShare = .5, int MinKillsLastFiveMinutes = 5, int Bombers = 12,
        double EscortShare = .1, int EscortCap = 12, float EscortRadius = 1500, float Leash = 2500)
    {
        public bool Enabled => AfterMinutes > 0;
        internal void Validate()
        {
            if (!double.IsFinite(AfterMinutes) || AfterMinutes < 0
                || Side is not ("leader" or "0" or "1" or "both")
                || !double.IsFinite(MinRelaunchShare) || MinRelaunchShare < 0 || MinRelaunchShare > 1
                || MinKillsLastFiveMinutes < 1 || Bombers < 1 || Bombers > 200
                || !double.IsFinite(EscortShare) || EscortShare < 0 || EscortShare > 1
                || EscortCap < 0 || EscortCap > 200 || !float.IsFinite(EscortRadius) || EscortRadius <= 0
                || !float.IsFinite(Leash) || Leash < EscortRadius)
                throw new ArgumentException("spawn_scenario: invalid finale settings (nonnegative minutes, side leader/0/1/both, shares 0..1, positive kill floor/bombers/radius, leash >= radius)");
        }
        internal int EscortBudget(int fighters) => Math.Min(EscortCap, (int)Math.Floor(fighters * EscortShare));
    }

    internal sealed record Maturity(short Side, int CombatBots, int Relaunched, long Kills)
    {
        public double RelaunchShare => CombatBots == 0 ? 0 : (double)Relaunched / CombatBots;
    }

    internal static short[] TriggerSides(Settings settings, double elapsedSeconds,
        IReadOnlyList<Maturity> sides, int recentKills)
    {
        if (!settings.Enabled || elapsedSeconds < settings.AfterMinutes * 60 || sides.Count != 2
            || sides.Any(s => s.CombatBots == 0 || s.RelaunchShare < settings.MinRelaunchShare)
            || recentKills < settings.MinKillsLastFiveMinutes) return [];
        return settings.Side switch
        {
            "both" => sides.Select(s => s.Side).Order().ToArray(),
            "0" => sides.Where(s => s.Side == 0).Select(s => s.Side).ToArray(),
            "1" => sides.Where(s => s.Side == 1).Select(s => s.Side).ToArray(),
            _ => [sides.OrderByDescending(s => s.Kills).ThenBy(s => s.Side).First().Side],
        };
    }

    internal readonly record struct Damage(float Hull, float Shield)
    {
        public static Damage operator +(Damage a, Damage b) => new(a.Hull + b.Hull, a.Shield + b.Shield);
        public static Damage operator *(Damage a, float n) => new(a.Hull * n, a.Shield * n);
        // Full-shield equivalent damage: both pools must be defeated. A zero shield
        // matrix entry bypasses that pool, exactly as Station.ReceiveDamage does.
        internal float Score(DataStationTypeIGC target) => Hull <= 0 ? 0 :
            1f / (target.MaxArmorHitPoints / Hull + (Shield > 0 ? target.MaxShieldHitPoints / Shield : 0));
    }

    internal sealed record StrikeLoadout(short HullId, string HullName, short TargetTypeId,
        string TargetName, IReadOnlyList<ExpandedPartData> Parts, Damage Volley, Damage PerSecond,
        int Price, IReadOnlyList<string> PartNames);

    internal static Damage RoundDamage(StaticCore core, byte type, float power, DataStationTypeIGC target)
    {
        // Match Station.DamageConstant: invalid indices are neutral, negatives clamp to zero.
        float Mult(byte defense) => type >= DataConstantsIGC.DmgidMax || defense >= DataConstantsIGC.DefidMax
            ? 1 : Math.Max(0, core.Constants.DamageConstants[type][defense]);
        return new(power * Mult(target.DefenseTypeArmor), power * Mult(target.DefenseTypeShield));
    }

    internal static Damage PartDamage(StaticCore core, short partId, DataStationTypeIGC target)
    {
        if (core.PartTypes.TryGetValue(partId, out var p) && p.Type is DataPartTypeIGC.Weapon w
            && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var round))
            return RoundDamage(core, round.DamageType, round.Power, target);
        if (core.Launchers.TryGetValue(partId, out var launcher)
            && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile))
            return RoundDamage(core, missile.DamageType, missile.Power, target);
        return default;
    }

    internal static Damage Volley(StaticCore core, IEnumerable<ExpandedPartData> parts, DataStationTypeIGC target)
        => parts.Aggregate(default(Damage), (sum, p) => sum + PartDamage(core, p.PartId, target));

    // Catalogue callers can evaluate the showcase's all-tech unlock. Live callers
    // supply the observed side/station techs; unknown tech must not imply all-tech.
    // No legal bomber loadout means keep the existing bombers' current loadouts.
    internal static StrikeLoadout? SelectLoadout(StaticCore core, DataStationTypeIGC target,
        TechTreeBitMask? sideTechs = null, byte[]? stationLocal = null)
    {
        var options = new List<StrikeLoadout>();
        var techs = sideTechs ?? core.ComputeRequiredTechUnion();
        bool Buyable(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, stationLocal, techs);
        foreach (var hull in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
                     && Showcase.ClassifyHull(h) == Showcase.HullRole.Bomber
                     && Buyable(h.TtbmRequired)
                     && !SuccessorUpgrade.HullIsObsolete(core, h, stationLocal, techs)).OrderBy(h => h.HullId))
        {
            // Pareto search across hardpoints: greedy per-gun selection can waste
            // the shared energy/ammo pool and miss a stronger complete volley.
            var frontier = new List<GunSet> { new([], default, default, 0, 0) };
            foreach (var (hp, index) in hull.Hardpoints.Select((h, i) => (h, i)).Where(x => x.h.Fixed))
            {
                var guns = core.PartTypes.Values.Where(p => p.GroupId != byte.MaxValue
                        && Buyable(p.TtbmRequired)
                        && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs)
                        && p.Type is DataPartTypeIGC.Weapon && (p.PartMask & ~hp.PartMask) == 0)
                    .OrderBy(p => p.PartId).Select(p => (Part: p, Damage: PartDamage(core, p.PartId, target)))
                    .Where(p => p.Damage.Hull > 0).ToArray();
                var next = new List<GunSet>();
                foreach (var state in frontier)
                {
                    Insert(next, state); // an empty socket can free resources for a later one
                    foreach (var gun in guns)
                    {
                        var w = (DataPartTypeIGC.Weapon)gun.Part.Type;
                        float energy = state.Energy + Math.Max(0, w.EnergyPerShot);
                        int ammo = state.Ammo + Math.Max(0, (int)w.CAmmoPerShot);
                        if (energy > hull.MaxEnergy || ammo > hull.MaxAmmo) continue;
                        Insert(next, new([.. state.Parts, new(gun.Part.PartId, 1, (byte)index, ExpandedPartData.FullyMounted)],
                            state.Volley + gun.Damage, state.PerSecond + gun.Damage * (1 / Math.Max(.01f, w.DtimeBurst)), energy, ammo));
                    }
                }
                frontier = next;
            }
            var missiles = core.Launchers.Keys.Order().Where(id => core.TryBuildExpendableLauncher(id, out var p)
                    && p.EquipmentType == EquipmentType.Magazine && p.GroupId != byte.MaxValue
                    && Buyable(p.TtbmRequired)
                    && hull.CanMountPart(p) && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs)
                    && p.Type is DataPartTypeIGC.MissileLauncher ml
                    && core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var mt)
                    && hull.CapacityMagazine >= Math.Max(1, (int)mt.LauncherExpendableSize))
                .Select(id => (Id: id, Damage: PartDamage(core, id, target)))
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
                        PerSecond = state.PerSecond + m.Damage * (1 / Math.Max(.01f, missile.ReadyTime + missile.LockTime)) });
                }
            }
            var best = combinations.OrderByDescending(s => s.Volley.Score(target))
                .ThenByDescending(s => s.PerSecond.Score(target)).FirstOrDefault();
            if (best is null) continue; // station steering needs an automated fixed gun
            var parts = best.Parts.ToList();
            var dps = best.PerSecond;
            // Best fitting shield and afterburner keep the purchased bomber viable.
            foreach (var equipment in new[] { EquipmentType.Shield, EquipmentType.Afterburner })
            {
                var fitting = core.PartTypes.Values.Where(p => p.GroupId != byte.MaxValue
                    && p.EquipmentType == equipment && hull.CanMountPart(p)
                    && Buyable(p.TtbmRequired)
                    && !SuccessorUpgrade.PartIsObsolete(core, p, stationLocal, techs))
                    .OrderByDescending(p => p.Type switch
                    {
                        DataPartTypeIGC.Shield s => s.MaxStrength,
                        DataPartTypeIGC.Afterburner a => a.MaxThrust,
                        _ => 0,
                    }).ThenBy(p => p.PartId).ToArray();
                if (fitting.Length > 0) parts.Add(new(fitting[0].PartId, 1, 0, ExpandedPartData.FullyMounted));
            }
            int price = hull.Price;
            var names = new List<string>();
            foreach (var p in parts)
            {
                if (!core.PartTypes.TryGetValue(p.PartId, out var type)) core.TryBuildExpendableLauncher(p.PartId, out type);
                price += type.Price;
                names.Add($"{p.PartId}: {type.Name} (mount {p.MountId}, amount {p.Amount})");
            }
            options.Add(new(hull.HullId, hull.Name, target.StationTypeId, target.Name,
                parts, Volley(core, parts, target), dps, price, names));
        }
        return options.OrderByDescending(o => o.Volley.Score(target)).ThenByDescending(o => o.PerSecond.Score(target))
            .ThenBy(o => o.HullId).FirstOrDefault();
    }

    private sealed record GunSet(IReadOnlyList<ExpandedPartData> Parts, Damage Volley,
        Damage PerSecond, float Energy, int Ammo);

    private static void Insert(List<GunSet> frontier, GunSet candidate)
    {
        bool Dominates(GunSet a, GunSet b) => a.Energy <= b.Energy && a.Ammo <= b.Ammo
            && a.Volley.Hull >= b.Volley.Hull && a.Volley.Shield >= b.Volley.Shield
            && a.PerSecond.Hull >= b.PerSecond.Hull && a.PerSecond.Shield >= b.PerSecond.Shield;
        if (frontier.Any(s => Dominates(s, candidate))) return;
        frontier.RemoveAll(s => Dominates(candidate, s));
        frontier.Add(candidate);
    }

    internal readonly record struct EscortEnemy(short Id, Vector3 Position, Vector3 Velocity, bool AttackingBomber);
    internal static short? EscortTarget(Vector3 escort, Vector3 bomber, IEnumerable<EscortEnemy> enemies,
        float radius, float leash)
    {
        if ((escort - bomber).LengthSquared() > leash * leash) return null;
        return enemies.Where(e => (e.Position - bomber).LengthSquared() <= leash * leash
                && (e.AttackingBomber || ((e.Position - bomber).LengthSquared() <= radius * radius
                    && Dot(e.Velocity, bomber - e.Position) > 0)))
            .OrderByDescending(e => e.AttackingBomber).ThenBy(e => (e.Position - bomber).LengthSquared())
            .ThenBy(e => e.Id).Select(e => (short?)e.Id).FirstOrDefault();
    }
    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
