using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P432 — prove <see cref="MatrixPlanner.Plan"/>'s cartesian-product expansion,
/// real hull/weapon resolution and real capability-mask mount compatibility in
/// isolation (NO server — pure logic over the same real PCore001.igc corpus the
/// resolver and live server use). These assert the planner BEHAVIOR that the
/// live <c>run_hull_matrix</c> tool is built on, so a caller trusts that the
/// per-combination rows it returns (and the compatibility-error rows) are the
/// real cartesian product of what they asked for.
///
/// Representative names are chosen from real, stable PCore001.igc entries and
/// span REAL capability classes the codebase already distinguishes (Scout =
/// light fast fighter, Bomber = slow heavy hull, plus the standing default):
///   • "PW Gat Gun 1"  (part mask 0x0001) — a common light gun that mounts on
///     Scouts, Fighters and Bombers alike.
///   • "PE AutoCan 3"  (part mask 0x4000) — a heavier cannon that only the
///     Bomber's second hardpoint hosts; Scouts/Fighters have no 0x4000 hardpoint.
///   • the default weapon ("PW A-B Omega 2", mask 0x0800) — a capital cannon that
///     by capability mask does NOT mount on the default fighter hull (a real,
///     pre-existing corpus/selection quirk the matrix surfaces honestly).
/// </summary>
public class RunHullMatrixPlanningTests
{
    // ── Cartesian-product expansion ────────────────────────────────────────

    [Fact]
    public void Plan_ExpandsFullCartesianProduct()
    {
        // 2 hulls × 3 weapons × 2 sizes = 12 combos.
        var plan = MatrixPlanner.Plan(
            ["Scout", "Bomber"],
            ["PW Gat Gun 1", "PE Vul Can 2", "PE AutoCan 3"],
            [5, 10]);
        Assert.Equal(12, plan.Count);

        // Order is hull-outer, then weapon, then size — stable and caller-visible.
        var ordered = plan.Select(c => $"{c.HullName}|{c.WeaponName}|{c.Size}").ToList();
        Assert.Equal("Scout|PW Gat Gun 1|5", ordered[0]);
        Assert.Equal("Scout|PW Gat Gun 1|10", ordered[1]);
        Assert.Equal("Scout|PE Vul Can 2|5", ordered[2]);
        Assert.Equal("Scout|PE AutoCan 3|10", ordered[5]);
        Assert.Equal("Bomber|PE AutoCan 3|10", ordered[11]);

        // Every combination carries the resolved real hull/weapon ids.
        Assert.All(plan, c => Assert.True(c.HullId > 0));
        Assert.All(plan, c => Assert.True(c.WeaponPartId > 0));
        Assert.All(plan, c => Assert.False(string.IsNullOrWhiteSpace(c.HullName)));
        Assert.All(plan, c => Assert.False(string.IsNullOrWhiteSpace(c.WeaponName)));
    }

    [Fact]
    public void Plan_DefaultsWeaponToTheStandingDefaultLoadoutGun_WhenWeaponsOmitted()
    {
        // The default weapon is the same real gun spawn_bot/spawn_scenario default to
        // (Catalog.DefaultWeaponPart). Passed against a Scout, it is a real
        // capability-mask MISMATCH (the capital cannon has no Scout hardpoint), so the
        // row is a specific error — the honest default behavior, not a silent skip.
        var plan = MatrixPlanner.Plan(["Scout"], [], [5]);
        var combo = Assert.Single(plan);
        Assert.Equal(Catalog.DefaultWeaponPart().Name, combo.WeaponName);
        Assert.NotNull(combo.Error);
        Assert.Contains("does not mount", combo.Error);
    }

    // ── Real capability-mask compatibility ─────────────────────────────────

    [Fact]
    public void Plan_LightGunMountsOnFastAndHeavyHulls_AcrossSizes()
    {
        // "PW Gat Gun 1" (mask 0x0001) mounts on Scouts, Fighters and Bombers.
        var plan = MatrixPlanner.Plan(
            ["Scout", "Fighter", "Bomber"],
            ["PW Gat Gun 1"],
            [5, 10, 15]);
        Assert.Equal(9, plan.Count);
        Assert.All(plan, c => Assert.Null(c.Error));   // every combo runnable
    }

    [Fact]
    public void Plan_HeavyCannonMountsOnlyOnTheHeavyHull_OthersGetASpecificError()
    {
        // "PE AutoCan 3" (mask 0x4000) hosts on the Bomber's heavy hardpoint but
        // NOT on the Scout/Fighter (no 0x4000 hardpoint) — the real capability-mask
        // dimension the matrix is built to surface, per-combination, never a crash.
        var plan = MatrixPlanner.Plan(
            ["Scout", "Fighter", "Bomber"],
            ["PE AutoCan 3"],
            [5, 10, 15]);

        var scout = plan.Where(c => c.HullName == "Scout");
        var fighter = plan.Where(c => c.HullName == "Fighter");
        var bomber = plan.Where(c => c.HullName == "Bomber");

        Assert.Equal(3, scout.Count());
        Assert.Equal(3, fighter.Count());
        Assert.Equal(3, bomber.Count());

        // Fast hulls: every size is a specific per-combination compatibility error.
        Assert.All(scout, c => Assert.Contains("does not mount", c.Error));
        Assert.All(fighter, c => Assert.Contains("does not mount", c.Error));
        // Heavy hull: fully runnable.
        Assert.All(bomber, c => Assert.Null(c.Error));
    }

    // ── Strict resolution / validation ─────────────────────────────────────

    [Fact]
    public void Plan_UnknownHullAbortsWithASpecificError()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MatrixPlanner.Plan(["definitely-not-a-real-hull-xyzzy"], ["PW Gat Gun 1"], [5]));
        Assert.Contains("no hull matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_NonWeaponPartNameIsRejected()
    {
        // The matrix only accepts real weapon parts: resolving a real non-weapon
        // part (whatever it is in the corpus) must abort with a not-a-weapon error.
        var nonWeapon = Catalog.AllParts().First(p => p.Category != "Weapon");
        var ex = Assert.Throws<ArgumentException>(() =>
            MatrixPlanner.Plan(["Scout"], [nonWeapon.Name], [5]));
        Assert.Contains("not a weapon", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_NonPositiveSizeIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MatrixPlanner.Plan(["Scout"], ["PW Gat Gun 1"], [0]));
        Assert.Throws<ArgumentException>(() =>
            MatrixPlanner.Plan(["Scout"], ["PW Gat Gun 1"], [-3]));
    }

    [Fact]
    public void Plan_EmptyHullsOrSizesIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MatrixPlanner.Plan([], ["PW Gat Gun 1"], [5]));
        Assert.Throws<ArgumentException>(() => MatrixPlanner.Plan(["Scout"], ["PW Gat Gun 1"], []));
    }
}
