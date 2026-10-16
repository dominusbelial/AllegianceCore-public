using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P557 — the pure, native-cited logic behind the showcase scenarios: hull
/// ROLE classification, the player-buyable eligibility test, the deterministic
/// mixed-fleet composition, the mine-dispenser selection, the wingman
/// behaviour flags (UseMines / UseMissiles / Strafe), and the minefield
/// placement default. Everything here is dependency-free of a live server (it
/// takes the real <see cref="StaticCore"/> explicitly), so the composition and
/// decision rules are unit-provable against the real <c>PCore001.igc</c>
/// corpus with no wire peers — the same stance as the P474 ScenarioPlan /
/// P539b combat-metric tests.
///
/// Native citations:
///   • wingman flags  — WingmanBehaviourBitMask, src/Igc/igc.h:785-794
///   • mine fire rule — wingman combat branch, src/Igc/shipIGC.cpp:2651-2665
///   • missile rule   — src/Igc/shipIGC.cpp:2584-2596
///   • strafe         — src/Igc/shipIGC.cpp:2639-2644, 2598-2607
///   • layer drone    — c_ptLayer, src/Igc/igc.h:688; FedSrv.CPP:11524-11540
///   • drone hulls    — P470: drone hulls carry group id −1
/// </summary>
internal static class Showcase
{
    // ── WingmanBehaviourBitMask (src/Igc/igc.h:785-794) ───────────────────
    public const short WbbmStrafe = 0x01;
    public const short WbbmUseMissiles = 0x02;
    public const short WbbmUseMines = 0x04;          // "needs mines already equipped"
    // c_wbbmInRangeAggressive = 0x40, c_wbbmTempSectorAggressive = 0x80 —
    // declared for completeness but not wired into the port's bot law (native
    // uses them in its own threat/aggression scoring, not in the per-branch
    // fire law this port mirrors).
    /// <summary>c_wbbmInRangeAggressive (igc.h:791) — an aggressive wingman
    /// switches target on a Lost-band escape (shipIGC.cpp:2545-2565) instead
    /// of just burning after the escaped target.</summary>
    public const short WbbmInRangeAggressive = 0x40;

    /// <summary>The default scenario wingman mask: missiles on (the standing
    /// P433 combat behaviour for a launcher-equipped bot is unchanged), mines
    /// on but ineffective without a mounted mine dispenser (the native gate),
    /// strafe off (does not change the established dogfight law unless a caller
    /// opts in).</summary>
    public const short DefaultScenarioWingman = WbbmUseMissiles | WbbmUseMines;

    // ── PilotType (src/Igc/igc.h:685-692) ─────────────────────────────────
    // c_ptWingman = 2, c_ptPlayer = 10, c_ptCheatPlayer = 11. The native mine
    // rule fires when a DIFFERENT-side ship within 2500 u has
    // (pt == c_ptWingman || pt >= c_ptPlayer) — i.e. a combat-capable actor.
    public const int PtWingman = 2;
    public const int PtPlayer = 10;

    /// <summary>The native mine-fire proximity rule (shipIGC.cpp:2655-2665):
    /// a target qualifies when it is a different-side combat actor within
    /// <c>2500.0f</c> units. Exposed pure so the exact threshold is provable.</summary>
    public const float WingmanMineRange = 2500f;

    /// <summary>Native's constants in the missile-evasion gate
    /// (shipIGC.cpp:2588-2594): <c>maxEvasionSpeed = 20.0f +
    /// missileType-&gt;GetMaxLock() * 30.0f</c>.</summary>
    public const float MissileEvasionBase = 20.0f;
    public const float MissileEvasionPerLock = 30.0f;

    // ── Hull role classification ──────────────────────────────────────────

    /// <summary>Fleet policy roles derived from native capabilities, mounts and sensor reach.
    /// The exact policy lives with the shared purchase selector.</summary>
    public enum HullRole { Fighter, Interceptor, Scout, Bomber, Heavy }

    public static HullRole ClassifyHull(DataHullTypeIGC h) => TechLoadoutSelector.Classify(h);

    /// <summary>
    /// Whether a hull is a PLAYER-BUYABLE fleet ship for a showcase side.
    /// Excludes, with native basis:
    ///   • drone-only hulls — <c>GroupId == -1</c> (P470: drone hulls group −1);
    ///   • lifepods          — c_habmLifepod (igc.h:728);
    ///   • miners/builders   — c_habmMiner (igc.h:740) / c_habmBuilder (igc.h:741);
    ///   • anything the core lacks a mountable native preferred loadout for is
    ///     filtered later (the per-hull resolution catches it).
    /// </summary>
    public static bool IsPlayerBuyable(StaticCore core, DataHullTypeIGC h)
    {
        if (h.GroupId < 0) return false;                                  // P470 drone
        if (h.HasCapability(DataHullTypeIGC.HabmLifepod)) return false;   // pod
        if (h.HasCapability(DataHullTypeIGC.HabmMiner)) return false;     // miner
        if (h.HasCapability(DataHullTypeIGC.HabmBuilder)) return false;   // builder
        if (!core.PartTypes.Values.Any(p => h.CanMountPart(in p) && p.EquipmentType == EquipmentType.Weapon))
            return false;                                                 // no mountable gun -> not a combat ship
        return true;
    }

    /// <summary>One showcase fleet slot: a hull (by real id/name), how many
    /// bots, and the per-slot tech level (P556).</summary>
    public readonly record struct FleetEntry(short HullId, string HullName, int Count, int Tech);

    /// <summary>Return a deterministic role representative: the LOWEST-HullId
    /// eligible hull of that role (same-named variants collapse to the
    /// lowest-id — the exact same disambiguation <see cref="Catalog.ResolveHull"/>
    /// uses). null when no eligible hull of the role exists.</summary>
    public static DataHullTypeIGC? RepresentativeHullOfRole(StaticCore core, HullRole role)
    {
        DataHullTypeIGC? best = null;
        foreach (var h in core.HullTypes.Values)
        {
            if (!IsPlayerBuyable(core, h)) continue;
            if (ClassifyHull(h) != role) continue;
            if (best is null || h.HullId < best.Value.HullId) best = h;
        }
        return best;
    }

    /// <summary>
    /// The showcase composition rule (documented): per side, build a
    /// deterministic mix of the roles that have an eligible hull in the real
    /// core — fighters as the main line, then interceptors, scouts, and a
    /// couple of bombers/heavies where available — totalling
    /// <c>15 × sizeScale</c> ships by default (rounded to the last role so the
    /// total is exact). Each role contributes its LOWEST-HullId representative
    /// (determinism). The mix weights (by fraction of the total) are:
    ///   Fighter 0.46, Interceptor 0.20, Scout 0.20, Bomber 0.14
    /// (heavies share the bomber fraction when no bomber exists). Integer
    /// allocation uses largest-remainder so the slot counts always sum to the
    /// target. Single-role sides (only one eligible role) get the whole fleet
    /// on that role.
    /// </summary>
    public static IReadOnlyList<FleetEntry> ComposeShowcaseFleet(StaticCore core, int sizeScale)
    {
        int perSide = 15 * Math.Max(1, sizeScale);
        var weights = new (HullRole Role, double W)[]
        {
            (HullRole.Fighter, 0.46),
            (HullRole.Interceptor, 0.20),
            (HullRole.Scout, 0.20),
            (HullRole.Bomber, 0.14),
        };
        // Fill only roles with an eligible representative.
        var slots = new List<(HullRole Role, DataHullTypeIGC Hull, double W)>();
        foreach (var (role, w) in weights)
        {
            var rep = RepresentativeHullOfRole(core, role);
            if (rep is null)
            {
                // Heavy shares the bomber weight when no bomber hull exists.
                if (role == HullRole.Bomber && RepresentativeHullOfRole(core, HullRole.Heavy) is { } heavy)
                    slots.Add((HullRole.Heavy, heavy, w));
                continue;
            }
            slots.Add((role, rep.Value, w));
        }
        if (slots.Count == 0)
            return Array.Empty<FleetEntry>();

        // Largest-remainder integer allocation of `perSide` across the slots.
        var raw = slots.Select(s => s.W * perSide).ToArray();
        var floors = raw.Select(Math.Floor).Select(v => (int)v).ToArray();
        int assigned = floors.Sum();
        int remain = perSide - assigned;
        if (remain > 0)
        {
            // Distribute the leftover to the slots with the largest fractional parts.
            var frac = raw.Select((r, i) => (Idx: i, Frac: r - floors[i]))
                          .OrderByDescending(x => x.Frac)
                          .Take(remain);
            foreach (var x in frac) floors[x.Idx]++;
        }
        var result = new List<FleetEntry>();
        for (int i = 0; i < slots.Count; i++)
            if (floors[i] > 0)
                result.Add(new FleetEntry(slots[i].Hull.HullId, slots[i].Hull.Name, floors[i], 0));
        return result;
    }

    // ── Mine-dispenser selection (part of each showcase fleet) ───────────

    /// <summary>
    /// Choose a real MINE DISPENSER for a hull, native-preferred first: scan the
    /// hull's native <c>PreferredPartsTypes</c> for a launcher whose expendable
    /// is a MINE type (DataPartTypeIGC.MineLauncher / core.MineTypes) that
    /// actually MOUNTS the hull (P555 mask); else fall back to the lowest-PartId
    /// real mine dispenser in the core that mounts the hull (the existing
    /// <c>launcher</c> path). Returns null (honestly) when the hull can mount no
    /// mine dispenser at all — such a hull simply carries no mines.
    /// </summary>
    public static (short? PartId, string Note) PickMineDispenser(StaticCore core, DataHullTypeIGC hull)
    {
        // 1) native preferred dispenser that is a mine type and mounts.
        foreach (var pid in hull.PreferredPartsTypes)
        {
            if (pid < 0) break;                                   // NA terminator
            if (!core.Launchers.ContainsKey(pid)) continue;       // not a launcher
            if (!core.TryBuildExpendableLauncher(pid, out var part)) continue;
            if (part.Type is not DataPartTypeIGC.MineLauncher) continue;   // not a mine
            if (!hull.CanMountPart(in part)) continue;            // P555 mask
            return (pid, $"native preferred mine dispenser '{Catalog.PartName(pid)}' (part id {pid})");
        }
        // 2) lowest-PartId real mine dispenser that mounts (existing launcher path).
        foreach (var (partId, launcher) in core.Launchers.OrderBy(kv => kv.Key))
        {
            if (!core.MineTypes.ContainsKey(launcher.ExpendabletypeId)) continue;
            if (!core.TryBuildExpendableLauncher(partId, out var lp)) continue;
            if (!hull.CanMountPart(in lp)) continue;
            return (partId, $"fallback mine dispenser '{Catalog.ResolveLauncherName(partId)}' (part id {partId})");
        }
        return (null, "no mine dispenser mounts on this hull — carries no mines");
    }

    /// <summary>Whether a launcher part id fires a MINE expendable (native
    /// <c>GetExpendableType()->GetObjectType() == OT_mineType</c>,
    /// shipIGC.cpp:2653).</summary>
    public static bool IsMineLauncher(StaticCore core, short launcherPartId)
    {
        if (!core.Launchers.TryGetValue(launcherPartId, out var l)) return false;
        return core.MineTypes.ContainsKey(l.ExpendabletypeId);
    }

    // ── Fleet parsing + validation (spawn_scenario fleet_a/fleet_b) ──────

    /// <summary>
    /// Resolve and validate a fleet description into real <see cref="FleetEntry"/>s.
    /// Each spec is a (hull name/substring, count, tech) triple; the hull is
    /// resolved through the real core (exact-over-substring, throws on unknown
    /// or ambiguous — <see cref="Catalog.ResolveHull"/>), then validated as a
    /// player-buyable combat hull used as a role seed. A spec that names a
    /// drone-only / lifepod / miner / builder hull, is a real, specific error — never silently skipped.
    /// `count` must be ≥ 1 and `tech` ≥ 0. Pure over the real core, so the
    /// parse + validation rules are unit-provable.
    /// </summary>
    public static IReadOnlyList<FleetEntry> ResolveFleet(
        StaticCore core, IReadOnlyList<(string Hull, int Count, int Tech)> specs, string sideLabel)
    {
        if (specs.Count == 0)
            throw new ArgumentException($"spawn_scenario: side '{sideLabel}' fleet is empty — name at least one hull");
        var result = new List<FleetEntry>();
        foreach (var (hullName, count, tech) in specs)
        {
            if (count < 1)
                throw new ArgumentException($"spawn_scenario: side '{sideLabel}' fleet entry '{hullName}' has count {count} — must be ≥ 1");
            if (tech < 0)
                throw new ArgumentException($"spawn_scenario: side '{sideLabel}' fleet entry '{hullName}' has tech {tech} — must be ≥ 0");
            var hull = Catalog.ResolveHull(hullName);
            if (!core.HullTypes.TryGetValue(hull.Id, out var dh))
                throw new ArgumentException($"spawn_scenario: hull '{hull.Name}' (id {hull.Id}) not in the representative core");
            if (!IsPlayerBuyable(core, dh))
                throw new ArgumentException(
                    $"spawn_scenario: side '{sideLabel}' hull '{hull.Name}' is not a player-buyable combat ship "
                    + "(drone-only/lifepod/miner/builder or no mountable gun — P470 drone hulls carry group −1); wire bots can't fly it");
            // Hardware is selected only after seating, from the live shop.
            result.Add(new FleetEntry(hull.Id, hull.Name, count, tech));
        }
        return result;
    }

    // ── Wingman fire/behaviour decisions (native-cited) ──────────────────

    /// <summary>Native mine-fire qualification (shipIGC.cpp:2651-2665): a
    /// wingman with the UseMines flag and a mounted mine-type dispenser fires
    /// the dispenser when ANY different-side combat actor is within
    /// <see cref="WingmanMineRange"/> units. The proximity test is the
    /// distance-&lt;-2500u predicate; the caller supplies whether a qualifying
    /// enemy is present. Pure so the 2500 u rule is provable.</summary>
    public static bool EnemyWithinMineRange(float distance) => distance < WingmanMineRange;

    /// <summary>Native missile fire gate (shipIGC.cpp:2584-2596): with the
    /// UseMissiles flag and a mounted magazine whose lock is full
    /// (<c>&gt;= 1.0f</c>), fire when the target's evasion speed is below the
    /// missile's max evasion speed:
    ///   evasionSpeed    = sqrt(|targetVel·right| + |targetVel·up|) · |targetVel|
    ///   maxEvasionSpeed = 20.0f + missileType-&gt;GetMaxLock() * 30.0f
    /// Returns true when the missile should fire. Pure.</summary>
    public static bool DecideWingmanMissile(
        float lockQuality, Vector3 targetVel, Vector3 right, Vector3 up, float missileMaxLock)
    {
        if (lockQuality < 1.0f) return false;
        float orthRight = MathF.Abs(vx(targetVel, right));
        float orthUp = MathF.Abs(vx(targetVel, up));
        float evasionSpeed = MathF.Sqrt(orthRight + orthUp) * targetVel.Length();
        float maxEvasionSpeed = MissileEvasionBase + missileMaxLock * MissileEvasionPerLock;
        return evasionSpeed < maxEvasionSpeed;
    }

    /// <summary>Dot product of two <see cref="Vector3"/>s (this core's Vector3
    /// exposes no Dot; the algebra is the same as native's <c>·</c>).</summary>
    private static float vx(Vector3 a, Vector3 b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Whether the <c>c_wbbmUseMines</c> flag is set in a mask.</summary>
    public static bool UsesMines(short mask) => (mask & WbbmUseMines) != 0;
    /// <summary>Whether the <c>c_wbbmUseMissiles</c> flag is set in a mask.</summary>
    public static bool UsesMissiles(short mask) => (mask & WbbmUseMissiles) != 0;
    /// <summary>Whether the <c>c_wbbmStrafe</c> flag is set in a mask.</summary>
    public static bool StrafeEnabled(short mask) => (mask & WbbmStrafe) != 0;
    /// <summary>Whether the <c>c_wbbmInRangeAggressive</c> flag is set in a
    /// mask (P558 — drives the Lost-band target-switch, shipIGC.cpp:2545).</summary>
    public static bool InRangeAggressive(short mask) => (mask & WbbmInRangeAggressive) != 0;

    // ── Minefield placement default ───────────────────────────────────────

    /// <summary>
    /// The default minefield placement: in FRONT OF the enemy's arrival aleph
    /// in the rendezvous sector, so the arriving fleet flies into the field.
    /// Computed as a point <paramref name="offset"/> units from the aleph along
    /// the aleph→sector-centre direction (clamped so the point stays between
    /// the aleph and the centre). Pure geometry, so the default rule is
    /// unit-provable with synthetic aleph/centre positions.
    /// </summary>
    public static Vector3 PlaceInFrontOfArrivalAleph(Vector3 alephPos, Vector3 sectorCenter, float offset)
    {
        Vector3 toCentre = sectorCenter - alephPos;
        float len = toCentre.Length();
        if (len <= 1e-6f) return alephPos;                     // degenerate: no direction
        toCentre /= len;
        float dist = Math.Clamp(offset, 0f, len * 0.6f);       // never overshoot past the centre
        return alephPos + toCentre * dist;
    }
}
