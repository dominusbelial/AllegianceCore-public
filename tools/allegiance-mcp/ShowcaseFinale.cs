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
        double EscortShare = .1, int EscortCap = 12, float EscortRadius = 1500, float Leash = 2500,
        double SurvivalMargin = 0)
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
                || !float.IsFinite(Leash) || Leash < EscortRadius
                // P896 — 0 keeps the P801/AIX-comparable wave (bare kill + one
                // spare); above 0 also carries expected run-in losses / defenders.
                || !double.IsFinite(SurvivalMargin) || SurvivalMargin < 0 || SurvivalMargin > 2)
                throw new ArgumentException("spawn_scenario: invalid finale settings (nonnegative minutes, side leader/0/1/both, shares 0..1, positive kill floor/bombers/radius, leash >= radius, survival margin 0..2)");
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

    // ── P778a — finale bomber push ────────────────────────────────────────────
    // Real Allegiance escorts "push the bomb": they nose into the back of their
    // own slow bomber to shove it toward the enemy station faster. The physics
    // is native and already ported (Sector.ResolveShipShipCollision,
    // shipIGC.cpp:1023-1202): a stern contact transfers momentum along the
    // contact normal mass-weighted, and FRIENDLY rams DO damage (native exempts
    // only friendly lifepod bumps, shipIGC.cpp:1166-1168) — so the closing speed
    // is picked small (PushClosingSpeed) and nanite scouts pay the hull back.

    /// <summary>How many escorts push one bomber (the stable pick takes the
    /// lowest ship ids among in-sector escorts while the bomber is not under
    /// fire; threat response keeps priority via EscortTarget).</summary>
    internal const int MaxBomberPushers = 2;

    /// <summary>Extra clearance beyond the two hull radii for the contact shell.
    /// The P778a cut used 4 u ("just outside the bounce shell so the approach
    /// never walls up"), but the staged live run measured the pusher walling up
    /// 12 u from the contact point with NO collision events for seconds — it rode
    /// the separate pass and delivered no impulse. Aiming AT the hull contact
    /// distance (0 margin) makes the pusher keep ramming, which is what native
    /// pushing is (a series of gentle contacts).</summary>
    internal const float PusherMargin = 0f;

    /// <summary>Stern-contact closing speed in u/s. At the real PCore001 finale
    /// hull masses this keeps one contact's damage to the bomber well under two
    /// hull hit points (derivation in docs/reports/P778A_BOMBER_PUSH.md and
    /// <see cref="PushContactDamage"/>).</summary>
    internal const float PushClosingSpeed = 8f;

    /// <summary>Cap on the CHASE closing speed while astern but still far from
    /// the contact shell. The staged live run (docs/reports/P778A_BOMBER_PUSH.md)
    /// measured 13-15 u/s impacts with the old 90 u/s cap: the pusher arrived
    /// hot, the contact normal was off the target line, and the bomber LOST
    /// 80→50 u/s at each hit instead of gaining. Tapering to
    /// <see cref="PushClosingSpeed"/> by the shell keeps every contact a stern
    /// nudge.</summary>
    internal const float PushChaseSpeed = 20f;

    /// <summary>Stable pusher pick: <paramref name="self"/> pushes iff the bomber
    /// has no attacker inside the threat window (threat response keeps priority)
    /// and its ship id is among the <see cref="MaxBomberPushers"/> LOWEST ids of
    /// the in-sector escorts — deterministic, so every escort computes the same
    /// pair without coordinating.</summary>
    internal static bool IsBomberPusher(IReadOnlyList<short> inSectorEscortIds, bool bomberThreatened, short self)
        => !bomberThreatened && inSectorEscortIds.Order().Take(MaxBomberPushers).Contains(self);

    /// <summary>The geometry of one stern push: a point just behind the bomber on
    /// the bomber→target line (sum of the two hull radii + a small margin) and
    /// the line direction, so closing along it keeps the contact normal pointed
    /// at the target and the momentum transfer pushes the bomber TOWARD it.</summary>
    internal readonly record struct BomberPushPlan(Vector3 ContactPoint, Vector3 Direction,
        float ContactDistance, float ClosingSpeed);

    /// <summary>Plan the push, or null when this escort must not push: the
    /// target line is degenerate, a radius is unknown, or the escort is NOT
    /// behind the bomber (at/inside the contact shell toward the target — an
    /// escort there can only nose backwards, which is not the tactic; it resumes
    /// its ordinary escort behaviour and can push once it drops astern).</summary>
    internal static BomberPushPlan? BomberPush(Vector3 escort, Vector3 bomber, Vector3 target,
        float bomberRadius, float pusherRadius, float closingSpeed = PushClosingSpeed, float margin = PusherMargin)
    {
        var toTarget = target - bomber;
        float distance = toTarget.Length();
        if (distance <= 1e-3f || bomberRadius <= 0f || pusherRadius <= 0f) return null;
        var direction = toTarget / distance;
        float contact = bomberRadius + pusherRadius + margin;
        if ((escort - bomber) * direction > contact) return null;   // not astern of the contact shell
        return new(bomber - direction * contact, direction, contact, closingSpeed);
    }

    /// <summary>Native two-body collision damage for one stern push contact at
    /// <paramref name="closingSpeed"/> (shipIGC.cpp:1093-1186), derived exactly
    /// like Sector.ResolveShipShipCollision: the velocity change of a body is
    /// <c>(1+cElasticity)·s·m_other/(m1+m2)</c> with cElasticity = 0.75 along the
    /// contact normal, <c>base = |Δv|²/128</c>, <c>damage ahead = 2·base_ahead +
    /// base_pusher</c> and <c>damage pusher = base_ahead + 2·base_pusher</c>,
    /// each then split through the native <c>hp·d/(hp+d)</c> share with the
    /// counterpart's hit-point pool. Returns (damage to bomber, damage to
    /// pusher) as hit-point amounts.</summary>
    internal static (float Bomber, float Pusher) PushContactDamage(float bomberMass, float pusherMass,
        float closingSpeed, float pusherHpPool, float bomberHpPool)
    {
        float dvBomber = 1.75f * closingSpeed * pusherMass / (bomberMass + pusherMass);
        float dvPusher = 1.75f * closingSpeed * bomberMass / (bomberMass + pusherMass);
        float baseBomber = dvBomber * dvBomber * (1f / 128f);
        float basePusher = dvPusher * dvPusher * (1f / 128f);
        float dBomber = 2f * baseBomber + basePusher;   // shipIGC.cpp:1141-1145 (ahead ship = ship1)
        float dPusher = baseBomber + 2f * basePusher;
        return (pusherHpPool * dBomber / (pusherHpPool + dBomber),      // shipIGC.cpp:1172-1176
                bomberHpPool * dPusher / (bomberHpPool + dPusher));     // shipIGC.cpp:1181-1185
    }
}
