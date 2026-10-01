using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P474 — the pure planning half of <c>run_scenario</c>: resolve the REAL hulls,
/// weapons and stations the two parity-verification presets (<c>bomber_run</c>,
/// <c>capture_attempt</c>) use, straight out of the decoded static
/// <c>PCore001.igc</c> corpus via <see cref="Catalog.RepresentativeCore"/>.
///
/// This is deliberately free of any server and of any hard-coded part id: an
/// "anti-base weapon" and a "boarding hull" are derived by deterministic rules
/// over the corpus, so a name/id change in a future core still resolves (and a
/// corpus that cannot supply one fails loudly at plan time, before anything
/// touches the wire — the same reuse-only stance as <see cref="MatrixPlanner"/>).
/// Both plans are unit-testable against the real corpus with no wire (see
/// <c>BotSpawnerResolutionTests.ScenarioPlanTests</c>).
/// </summary>
public static class ScenarioPlanner
{
    /// <summary>P474 — a resolved bomber-run group: the bomber hull, the real
    /// anti-base weapon that hull can mount, and the escort hull (the same real
    /// fighter the spawn tools default to) that screens it.</summary>
    public readonly record struct BomberPlan(
        short BomberHullId, string BomberHullName,
        short AntiBaseWeaponId, string AntiBaseWeaponName,
        short EscortHullId, string EscortHullName);

    /// <summary>P474 — a resolved capture-attempt group: the boarding hull that
    /// carries <c>c_habmBoard</c> (<see cref="DataHullTypeIGC.HabmBoard"/>, the
    /// only hull a hostile ship can capture a station with) and the escort hull
    /// whose gunfire knocks the station's shields below the "downed shield"
    /// fraction (<c>c_fcidDownedShield = 0.1f</c>, the same literal the P450
    /// capture gate uses).</summary>
    public readonly record struct CapturePlan(
        short BoardingHullId, string BoardingHullName,
        short EscortHullId, string EscortHullName);

    /// <summary>
    /// P474 — plan the bomber group. The anti-base weapon is the REAL weapon
    /// part with the greatest projectile power (max per-shot damage) that
    /// actually mounts on a bomb-capable hull (see <see cref="PickHullWeapon"/>),
    /// so it is a genuine anti-station gun, never a hard-coded id. The bomber
    /// hull is the preferred bomb-carrying hull the weapon fits; the escort is
    /// <see cref="Catalog.DefaultFighterHull"/>.
    /// </summary>
    public static BomberPlan PlanBomberRun()
    {
        var core = Catalog.RepresentativeCore();
        var (hull, weapon) = PickHullWeapon(core, preferBomber: true);
        var escort = Catalog.DefaultFighterHull();
        return new BomberPlan(hull.HullId, hull.Name, weapon.PartId, weapon.Name,
            escort.Id, escort.Name);
    }

    /// <summary>
    /// P474 — plan the capture attempt: the boarding hull is the lowest-id real
    /// hull carrying <c>c_habmBoard</c> (deterministic; a corpus with no
    /// board-capable hull is an immediate, specific plan error). The escort is
    /// <see cref="Catalog.DefaultFighterHull"/> (its default gun is enough to
    /// drain a station shield for the capture).
    /// </summary>
    public static CapturePlan PlanCaptureAttempt()
    {
        var core = Catalog.RepresentativeCore();
        var boarder = core.HullTypes.Values
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmBoard))
            .OrderBy(h => h.HullId)
            .FirstOrDefault();
        if (boarder.HullId == 0)
            throw new InvalidOperationException("capture_attempt: no hull in the representative core carries c_habmBoard");
        var escort = Catalog.DefaultFighterHull();
        return new CapturePlan(boarder.HullId, boarder.Name, escort.Id, escort.Name);
    }

    /// <summary>
    /// The deterministic hull+weapon pick shared by the group planners: a real
    /// hull (bomber-named first when <paramref name="preferBomber"/>, else
    /// highest-armour first) and the real weapon of greatest projectile power
    /// that <c>CanMountPart</c> mounts on that hull. Scans hulls in priority
    /// order and, for each, scans weapons by descending power until one fits —
    /// a greedy maximisation that always prefers the biggest gun the preferred
    /// hull can actually carry. Never hard-codes an id; throws only if the
    /// corpus supplies no fit at all.
    /// </summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC weapon) PickHullWeapon(StaticCore core, bool preferBomber)
    {
        var weapons = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .OrderByDescending(p =>
            {
                // Power lives on the projectile type the weapon fires; a weapon
                // with no resolvable projectile can't deal real damage.
                var proj = p.Type is DataPartTypeIGC.Weapon w && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt)
                    ? pt.Power : 0f;
                return proj;
            })
            .ToList();

        // Bomb-capable hulls first (name contains "bomber", case-insensitive),
        // then by armour so a non-bomber fallback is still a heavy platform.
        var hulls = core.HullTypes.Values
            .OrderByDescending(h => h.Name.Contains("bomber", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.HitPoints)
            .ToList();

        foreach (var hull in hulls)
        foreach (var weapon in weapons)
            if (weapon.Type is DataPartTypeIGC.Weapon && hull.CanMountPart(in weapon))
                return (hull, weapon);

        throw new InvalidOperationException(
            "no (hull, weapon) pair in the representative core mounts a real projectile weapon"
            + (preferBomber ? " on a bomber-capable hull" : ""));
    }
}
