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

    // Compatibility entry point for catalogue proofs. Runtime callers use the
    // exact dock type through TechLoadoutSelector; no missing live tech implies all-tech.
    internal static StrikeLoadout? SelectLoadout(StaticCore core, DataStationTypeIGC target,
        TechTreeBitMask? sideTechs = null, byte[]? stationLocal = null)
        => TechLoadoutSelector.SelectAtLocal(core, core.Civilizations.Keys.Min(), Showcase.HullRole.Bomber,
            sideTechs ?? core.ComputeRequiredTechUnion(), stationLocal, target)?.Strike;

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
