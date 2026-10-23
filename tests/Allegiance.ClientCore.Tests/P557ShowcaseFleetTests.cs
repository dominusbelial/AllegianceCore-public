using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P557 — failing-first UNIT tests for the showcase-scenario core, all pure
/// over the REAL <c>PCore001.igc</c> corpus (no live server):
/// <list type="bullet">
/// <item>fleet parsing + validation (ResolveFleet) — a real fleet resolves to
/// real hull ids; unknown / drone-only / unarmed hulls and bad counts throw
/// specific errors;</item>
/// <item>per-hull native-preferred loadout resolution (P555/P556) — a fighter
/// carries a real mountable loadout incl. its gun, and (post-P555) the scout
/// carries its native preferred <c>Sm Shield 1</c>;</item>
/// <item>showcase composition — a readable mix of ≥ 3 distinct hull types /
/// roles totalling ~15×scale per side, deterministic;</item>
/// <item>mine-dispenser selection — a mine dispenser that really mounts
/// (native preferred mine first, else a fallback), honestly null when none;</item>
/// <item>minefield placement default — placed in front of the enemy arrival
/// aleph, between the aleph and the sector centre;</item>
/// <item>wingman behaviour decisions — the native 2500 u mine rule, the missile
/// lock + evasion gate (shipIGC.cpp:2584-2594), and the flag helpers.</item>
/// </list>
/// </summary>
public class P557ShowcaseFleetTests
{
    private static StaticCore Core => Catalog.RepresentativeCore();

    // ── Fleet parsing + validation ────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveFleet_Resolves_Real_Hull_And_Validates()
    {
        Require.Artwork();
        // A real fighter + a real scout both resolve to real ids with mounts.
        var fleet = Showcase.ResolveFleet(Core,
            new[]
            {
                ("Fighter", 5, 0),
                ("Scout", 3, 1),
            }, "a");
        Assert.Equal(2, fleet.Count);
        Assert.All(fleet, e => { Assert.True(e.Count >= 1); Assert.True(e.Tech >= 0); });
        Assert.True(Core.HullTypes.ContainsKey(fleet[0].HullId));
        Assert.True(Core.HullTypes.ContainsKey(fleet[1].HullId));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveFleet_Unknown_Hull_Throws()
    {
        Require.Artwork();
        var ex = Assert.Throws<ArgumentException>(() =>
            Showcase.ResolveFleet(Core, new[] { ("No Such Hull XYZ", 1, 0) }, "a"));
        Assert.Contains("no hull matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveFleet_DroneOrLifepod_Hull_Throws()
    {
        Require.Artwork();
        // Find a drone/lifepod hull in the real corpus and expect rejection.
        var droneish = Core.HullTypes.Values
            .Cast<DataHullTypeIGC?>()
            .FirstOrDefault(h => h is { } hh && (hh.GroupId < 0 || hh.HasCapability(DataHullTypeIGC.HabmLifepod)));
        if (droneish is null) return;   // corpus has none — rule is vacuous
        var ex = Assert.Throws<ArgumentException>(() =>
            Showcase.ResolveFleet(Core, new[] { (droneish.Value.Name, 1, 0) }, "a"));
        Assert.Contains("player-buyable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveFleet_Zero_Count_Throws()
    {
        Require.Artwork();
        var ex = Assert.Throws<ArgumentException>(() =>
            Showcase.ResolveFleet(Core, new[] { ("Fighter", 0, 0) }, "a"));
        Assert.Contains("count 0", ex.Message);
    }

    // ── Per-hull native preferred loadout (P555/P556) ────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NativePreferredLoadout_Fighter_Has_Mountable_Gun()
    {
        Require.Artwork();
        var hull = Catalog.ResolveHull("Fighter");
        var tl = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0);
        Assert.NotEmpty(tl.Parts);
        var gun = tl.Parts.FirstOrDefault(p =>
            Core.PartTypes.TryGetValue(p.PartId, out var pt) && pt.EquipmentType == EquipmentType.Weapon);
        Assert.NotEqual(0, gun.PartId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NativePreferredLoadout_Scout_Has_Native_Sm_Shield_After_P555()
    {
        Require.Artwork();
        // P555 fixed the hull equipment-mask decode so the scout's native
        // preferred `Sm Shield 1` now MOUNTS and is included.
        var hull = Catalog.ResolveHull("Scout");
        var tl = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0);
        var shield = tl.Parts.FirstOrDefault(p =>
            Core.PartTypes.TryGetValue(p.PartId, out var pt) && pt.Type is DataPartTypeIGC.Shield);
        Assert.NotEqual(0, shield.PartId);
        Assert.Contains("Shield", Catalog.PartName(shield.PartId));
    }

    // ── Showcase composition ──────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ComposeShowcaseFleet_Produces_Readable_Mixed_Fleet()
    {
        Require.Artwork();
        var fleet = Showcase.ComposeShowcaseFleet(Core, 1);
        // Default 15 per side.
        Assert.Equal(15, fleet.Sum(e => e.Count));
        // A readable showcase mixes several distinct hulls/roles.
        Assert.True(fleet.Count >= 3,
            $"expected >= 3 distinct hull slots, got {fleet.Count}: {string.Join(", ", fleet.Select(f => f.HullName))}");
        var roles = fleet.Select(f => Showcase.ClassifyHull(Core.HullTypes[f.HullId])).Distinct().ToList();
        Assert.True(roles.Count >= 3,
            $"expected >= 3 distinct roles, got {string.Join(", ", roles)}");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ComposeShowcaseFleet_Scales_Exactly()
    {
        Require.Artwork();
        var fleet = Showcase.ComposeShowcaseFleet(Core, 2);
        Assert.Equal(30, fleet.Sum(e => e.Count));
        var fleet3 = Showcase.ComposeShowcaseFleet(Core, 1);
        Assert.Equal(15, fleet3.Sum(e => e.Count));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ComposeShowcaseFleet_Is_Deterministic()
    {
        Require.Artwork();
        var a = Showcase.ComposeShowcaseFleet(Core, 1);
        var b = Showcase.ComposeShowcaseFleet(Core, 1);
        Assert.Equal(a.Select(e => (e.HullId, e.Count)), b.Select(e => (e.HullId, e.Count)));
    }

    // ── Mine-dispenser selection ──────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PickMineDispenser_Returns_A_Mounting_Mine_Dispenser_Or_Null()
    {
        Require.Artwork();
        int withMine = 0, total = 0;
        foreach (var h in Core.HullTypes.Values)
        {
            if (!Showcase.IsPlayerBuyable(Core, h)) continue;
            total++;
            var (partId, _) = Showcase.PickMineDispenser(Core, h);
            if (partId is { } pid)
            {
                withMine++;
                // The returned part really is a mine dispenser and really mounts.
                Assert.True(Showcase.IsMineLauncher(Core, pid));
                Assert.True(Core.TryBuildExpendableLauncher(pid, out var part));
                Assert.True(h.CanMountPart(in part));
            }
        }
        // Sanity: at least some eligible combat hulls carry a mine dispenser.
        Assert.True(withMine > 0, "no eligible hull mounts a mine dispenser — showcase mine demo impossible");
        Assert.True(withMine <= total);
    }

    // ── Minefield placement default ───────────────────────────────────────

    [Fact]
    public void MinefieldPlaced_InFront_Of_Arrival_Aleph()
    {
        var aleph = new Vector3(0, 0, 0);
        var centre = new Vector3(1000, 0, 0);
        var p = Showcase.PlaceInFrontOfArrivalAleph(aleph, centre, 400f);
        // Along the aleph→centre axis, 400 u out — the arriving fleet flies into it.
        Assert.InRange(p.X, 300f, 500f);
        Assert.Equal(0f, p.Y, 3);
        Assert.Equal(0f, p.Z, 3);
        // Between the aleph and the centre (never overshooting).
        Assert.True((p - aleph).Length() < (centre - aleph).Length());
    }

    [Fact]
    public void MinefieldPlacement_Never_Overshoots_Centre()
    {
        var aleph = new Vector3(0, 0, 0);
        var centre = new Vector3(100, 0, 0);
        var p = Showcase.PlaceInFrontOfArrivalAleph(aleph, centre, 5000f);   // huge offset
        Assert.InRange(p.X, 0f, 100f);   // clamped to 60% of the span
    }

    // ── Wingman behaviour decisions ───────────────────────────────────────

    [Fact]
    public void Short_lived_mines_are_not_pre_laid_at_an_empty_home_aleph()
    {
        // Real PCore001 lifespans: Mine Pack 2 = 5 s, Prox Mine 2 = 18 s, Caltrop = 600 s.
        var core = Core;
        Assert.False(Showcase.PreLayMineAtAleph(true, true, core.MineTypes[13].Lifespan));
        Assert.False(Showcase.PreLayMineAtAleph(true, true, core.MineTypes[9].Lifespan));
        Assert.True(Showcase.PreLayMineAtAleph(true, true, core.MineTypes[1].Lifespan));
        Assert.False(Showcase.PreLayMineAtAleph(false, true, core.MineTypes[1].Lifespan));
        Assert.False(Showcase.PreLayMineAtAleph(true, false, core.MineTypes[1].Lifespan));
    }

    [Fact]
    public void EnemyWithinMineRange_Uses_Native_2500u()
    {
        Assert.True(Showcase.EnemyWithinMineRange(2499f));
        Assert.False(Showcase.EnemyWithinMineRange(2500f));   // native: distance < 2500
        Assert.False(Showcase.EnemyWithinMineRange(4000f));
    }

    [Fact]
    public void DecideWingmanMissile_Requires_Full_Lock()
    {
        // lock < 1.0 never fires regardless of evasion.
        Assert.False(Showcase.DecideWingmanMissile(0.9f, new Vector3(0, 0, 30), new Vector3(1, 0, 0), new Vector3(0, 1, 0), 1.0f));
    }

    [Fact]
    public void DecideWingmanMissile_Blocks_Too_Evasive_Target()
    {
        // Target moving straight down the shooter's forward axis: no lateral
        // evasion, so it fires (evasion 0 < max).
        Assert.True(Showcase.DecideWingmanMissile(1.0f, new Vector3(0, 0, 300), new Vector3(1, 0, 0), new Vector3(0, 1, 0), 1.0f));
        // Target crossing laterally fast (high orth-right) raises evasion above
        // max (max = 20 + 1*30 = 50): a 1000-u/s crossing target is blocked.
        Assert.False(Showcase.DecideWingmanMissile(1.0f, new Vector3(1000, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), 1.0f));
    }

    [Fact]
    public void Wingman_Flag_Helpers()
    {
        Assert.True(Showcase.UsesMines(Showcase.WbbmUseMines));
        Assert.False(Showcase.UsesMines(Showcase.WbbmUseMissiles));
        Assert.True(Showcase.UsesMissiles(Showcase.DefaultScenarioWingman));
        Assert.True(Showcase.UsesMines(Showcase.DefaultScenarioWingman));
        Assert.True(Showcase.StrafeEnabled(Showcase.WbbmStrafe));
    }
}
