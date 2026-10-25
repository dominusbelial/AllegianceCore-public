using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P814 — bot turret gunners. Two slices proved against the REAL PCore001.igc
/// corpus (no wire, no server):
/// <list type="number">
/// <item><b>Loadout turret arming</b> — a turret-capable hull's turret mounts
/// get legal weapons, so a bot carrier has crewed seats for gunners to board
/// (native BoardShip requires an EQUIPPED slot, FedSrv.CPP:2377-2392). This is
/// the loadout prerequisite the whole task rests on.</item>
/// <item><b>Gunner policy + aim/lead</b> — who may host/serve, the free-seat
/// arithmetic, and the pure aim (shared-Core SolveForImpact) + turret slew
/// (Orientation.TurnTo) that the gunner AI runs on.</item>
/// </list>
/// </summary>
public sealed class P814GunnerTests(ITestOutputHelper output)
{
    private static readonly short[] Civs = [25, 3, 35];

    private static (StaticCore core, TechTreeBitMask techs, short[] docks) FullTechs(short civ)
    {
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new ResearchTree(core, civ));
        foreach (var step in state.Tree.Generate("PCore001").Steps) state.Apply(step);
        return (core, state.Techs, state.Stations.Order().ToArray());
    }

    // ── loadout: turret mounts armed ─────────────────────────────────────

    [SkippableFact]
    public void ArmTurretMounts_fills_every_turret_mount_with_a_fitting_weapon()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        DataHullTypeIGC hull = default;
        DataPartTypeIGC gun = default;
        bool found = false;
        foreach (var h in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
                     && h.MaxWeapons > h.MaxFixedWeapons && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            var g = core.PartTypes.Values.FirstOrDefault(p => p.Type is DataPartTypeIGC.Weapon w
                && h.CanMountPart(p, 0) && w.EnergyPerShot <= h.MaxEnergy && w.CAmmoPerShot <= h.MaxAmmo);
            if (g.Type is null) continue;
            hull = h; gun = g; found = true; break;
        }
        Assert.True(found, "no turret-capable buyable hull with a mount-0 gun in PCore001");
        int turretCount = Math.Min(hull.MaxWeapons, Math.Min(8, hull.Hardpoints.Length)) - hull.MaxFixedWeapons;
        Assert.True(turretCount > 0, "expected a turret-capable hull");

        var parts = new List<ExpandedPartData> { new(gun.PartId, 1, 0, ExpandedPartData.FullyMounted) };
        float e = hull.MaxEnergy - ((DataPartTypeIGC.Weapon)gun.Type).EnergyPerShot;
        int a = hull.MaxAmmo - (int)((DataPartTypeIGC.Weapon)gun.Type).CAmmoPerShot;
        long spent = hull.Price + TechLoadoutSelector.PartPrice(gun, 1);
        var legal = core.PartTypes.Values.Where(p => p.Type is DataPartTypeIGC.Weapon).ToArray();

        TechLoadoutSelector.ArmTurretMounts(core, hull, legal, parts, ref e, ref a, ref spent, 5_000_000);

        // Every turret mount now has a weapon that CanMountPart there.
        int armed = 0;
        for (int m = hull.MaxFixedWeapons; m < Math.Min(hull.MaxWeapons, Math.Min(8, hull.Hardpoints.Length)); m++)
        {
            var wp = parts.FirstOrDefault(p => p.MountId == m);
            Assert.True(wp.PartId != 0, $"turret mount {m} of hull {hull.HullId} was not armed");
            Assert.True(hull.CanMountPart(TechLoadoutSelector.Part(core, wp.PartId), m));
            armed++;
        }
        Assert.Equal(turretCount, armed);
        // Pool was respected: cumulative turret energy/ammo within the hull.
        var turretGuns = parts.Skip(1).Select(p => (DataPartTypeIGC.Weapon)TechLoadoutSelector.Part(core, p.PartId).Type).ToArray();
        Assert.True(turretGuns.Sum(w => w.EnergyPerShot) <= hull.MaxEnergy);
        Assert.True(turretGuns.Sum(w => w.CAmmoPerShot) <= hull.MaxAmmo);
        // The fixed gun is untouched.
        Assert.Equal(gun.PartId, parts[0].PartId);
    }

    [SkippableFact]
    public void Strike_volley_counts_fixed_mounts_only_not_unmanned_turrets()
    {
        // A turret fires only with a gunner aboard (weaponIGC.h:236-254): the finale's
        // station-damage requirement must not credit a turret bomber's turret guns, or
        // the strike commits too few bombers (P745 regression found in P814b review).
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        foreach (var h in core.HullTypes.Values.Where(h => Showcase.IsPlayerBuyable(core, h)
                     && h.MaxWeapons > h.MaxFixedWeapons && h.Hardpoints.Length > h.MaxFixedWeapons
                     && h.Hardpoints[0].Fixed).OrderBy(h => h.HullId))
        {
            int turret = h.MaxFixedWeapons;
            var g = core.PartTypes.Values.FirstOrDefault(p => p.Type is DataPartTypeIGC.Weapon w
                && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r) && r.Power > 0
                && h.CanMountPart(p, 0) && h.CanMountPart(p, turret));
            if (g.Type is null) continue;
            var fixedOnly = StrikeVolley.FromParts(core, h.HullId,
                [new(g.PartId, 1, 0, ExpandedPartData.FullyMounted)]);
            var withTurret = StrikeVolley.FromParts(core, h.HullId,
                [new(g.PartId, 1, 0, ExpandedPartData.FullyMounted), new(g.PartId, 1, (byte)turret, ExpandedPartData.FullyMounted)]);
            Assert.NotNull(fixedOnly);
            Assert.Equal(fixedOnly!.Mounts.Length, withTurret!.Mounts.Length);
            return;
        }
        Assert.Fail("no turret hull whose fixed and turret mounts share a gun in PCore001");
    }

    [SkippableFact]
    public void ArmTurretMounts_leaves_a_fixed_only_hull_unchanged()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes.Values.First(h => h.MaxWeapons == h.MaxFixedWeapons && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed);
        var gun = core.PartTypes.Values.FirstOrDefault(p => p.Type is DataPartTypeIGC.Weapon w
            && hull.CanMountPart(p, 0) && w.EnergyPerShot <= hull.MaxEnergy && w.CAmmoPerShot <= hull.MaxAmmo);
        Assert.True(gun.Type is not null);
        var parts = new List<ExpandedPartData> { new(gun.PartId, 1, 0, ExpandedPartData.FullyMounted) };
        float e = hull.MaxEnergy - ((DataPartTypeIGC.Weapon)gun.Type).EnergyPerShot;
        int a = hull.MaxAmmo - (int)((DataPartTypeIGC.Weapon)gun.Type).CAmmoPerShot;
        long spent = hull.Price + TechLoadoutSelector.PartPrice(gun, 1);
        TechLoadoutSelector.ArmTurretMounts(core, hull, core.PartTypes.Values.ToArray(), parts, ref e, ref a, ref spent, 5_000_000);
        Assert.Single(parts);   // nothing added for a fixed-only hull
    }

    [SkippableTheory]
    [InlineData((short)25)]
    [InlineData((short)3)]
    [InlineData((short)35)]
    public void SelectVaried_arms_turrets_whenever_a_turret_carrier_is_chosen(short civ)
    {
        Require.Artwork();
        var (core, techs, docks) = FullTechs(civ);
        long budget = 5_000_000;
        int seen = 0;
        foreach (var dock in docks)
        {
            for (int i = 0; i < 60; i++)
            {
                var state = LoadoutVariety.Begin((short)i);
                var sel = TechLoadoutSelector.SelectVaried(core, civ, Showcase.HullRole.Heavy, techs, dock, state, budget: budget);
                if (sel is null) continue;
                var h = core.HullTypes[sel.HullId];
                if (h.MaxWeapons <= h.MaxFixedWeapons) continue;   // not a turret carrier
                seen++;
                int end = Math.Min(h.MaxWeapons, Math.Min(8, h.Hardpoints.Length));
                for (int m = h.MaxFixedWeapons; m < end; m++)
                {
                    var wp = sel.Parts.FirstOrDefault(p => p.MountId == m);
                    Assert.True(wp.PartId != 0,
                        $"civ={civ} dock={dock} hull {sel.HullId} ('{sel.HullName}') turret mount {m} unarmed: {sel.Reason}");
                    Assert.True(h.CanMountPart(TechLoadoutSelector.Part(core, wp.PartId), m));
                }
                Assert.True(TechLoadoutSelector.Legal(core, civ, techs, dock, sel.HullId, sel.Parts), sel.Reason);
            }
            LoadoutVariety.Reset();
        }
        Assert.True(seen > 0, "expected at least one turret-capable Heavy selection across civs/docks");
    }

    // ── gunner policy ────────────────────────────────────────────────────

    [Fact]
    public void Gunner_policy_host_and_serve_roles()
    {
        Assert.True(GunnerPolicy.CanHost(Showcase.HullRole.Bomber));
        Assert.True(GunnerPolicy.CanHost(Showcase.HullRole.Heavy));
        Assert.False(GunnerPolicy.CanHost(Showcase.HullRole.Fighter));
        Assert.False(GunnerPolicy.CanHost(Showcase.HullRole.Scout));

        Assert.True(GunnerPolicy.CanServeAsGunner(Showcase.HullRole.Fighter));
        Assert.True(GunnerPolicy.CanServeAsGunner(Showcase.HullRole.Interceptor));
        Assert.False(GunnerPolicy.CanServeAsGunner(Showcase.HullRole.Bomber));
        Assert.False(GunnerPolicy.CanServeAsGunner(Showcase.HullRole.Heavy));
        Assert.False(GunnerPolicy.CanServeAsGunner(Showcase.HullRole.Scout));
    }

    [Fact]
    public void Gunner_policy_should_seek_board_respects_min_free_pilots_and_enable()
    {
        var on = new GunnerPolicy.Tuning(Enable: true, MinFreePilots: 2);
        var off = new GunnerPolicy.Tuning(Enable: false, MinFreePilots: 2);
        Assert.True(GunnerPolicy.ShouldSeekBoard(Showcase.HullRole.Fighter, 5, on));
        Assert.False(GunnerPolicy.ShouldSeekBoard(Showcase.HullRole.Fighter, 2, on));  // at the floor
        Assert.False(GunnerPolicy.ShouldSeekBoard(Showcase.HullRole.Fighter, 5, off)); // disabled
        Assert.False(GunnerPolicy.ShouldSeekBoard(Showcase.HullRole.Scout, 5, on));    // not a gunner role
    }

    [SkippableFact]
    public void Turret_capacity_and_free_seats()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var carrier = core.HullTypes.Values.First(h => h.MaxWeapons > h.MaxFixedWeapons);
        int cap = GunnerPolicy.TurretCapacity(carrier);
        Assert.True(cap > 0);
        Assert.Equal(cap, GunnerPolicy.FreeSeats(cap, 0));
        Assert.Equal(cap - 1, GunnerPolicy.FreeSeats(cap, 1));
        Assert.Equal(0, GunnerPolicy.FreeSeats(cap, cap));          // full
        Assert.Equal(0, GunnerPolicy.FreeSeats(cap, cap + 5));      // never negative
        Assert.Equal(0, GunnerPolicy.TurretCapacity(core.HullTypes.Values.First(h => h.MaxWeapons == h.MaxFixedWeapons)));
        // Recruit limit is bounded by the free seats and the per-carrier cap.
        Assert.Equal(1, GunnerPolicy.RecruitLimit(1, new GunnerPolicy.Tuning(MaxGunnersPerCarrier: 2)));
        Assert.Equal(2, GunnerPolicy.RecruitLimit(5, new GunnerPolicy.Tuning(MaxGunnersPerCarrier: 2)));
    }

    [Fact]
    public void TryLead_returns_intercept_for_crossing_target_and_none_for_fleeing_one()
    {
        var shooter = new Vector3(0, 0, 0);
        var target = new Vector3(0, 0, 900);
        // Crossing target at a tangent: an intercept exists.
        bool ok = GunnerPolicy.TryLead(shooter, Vector3.Zero, target, new Vector3(150, 0, 0), 600f,
            out var aim, out float t);
        Assert.True(ok);
        Assert.True(t > 0);
        Assert.True(Math.Abs(aim.Length() - 1f) < 1e-4);   // unit aim direction
        // Target receding faster than the shot: no valid intercept.
        bool noOk = GunnerPolicy.TryLead(shooter, Vector3.Zero, target, new Vector3(0, 0, 901f), 600f,
            out _, out _);
        Assert.False(noOk);
    }

    [Fact]
    public void SlewToward_converges_onto_the_aim_and_AimControls_are_unit()
    {
        var o = new Orientation();
        o.SetForward(new Vector3(1, 0, 0));   // facing +X
        var aim = new Vector3(0, 0, 1);        // aim +Z (90° off)
        var t = new GunnerPolicy.Tuning(MaxSlewRate: 75f * MathF.PI / 180f, FireAngleRad: 0.04f);
        float remaining = 1f;
        int steps = 0;
        // Slew at the native max rate over the control cadence until on target.
        while (steps < 200 && remaining > t.FireAngleRad)
        {
            remaining = GunnerPolicy.SlewToward(o, aim, 0.0125f, t);   // tiny dt → fine granularity
            steps++;
        }
        Assert.True(remaining <= t.FireAngleRad, $"turret did not converge (remaining {remaining} rad after {steps} steps)");
        // AimControls are unit-bounded so ExecuteTurretMove's circle constraint holds.
        var c = GunnerPolicy.AimControls(o, aim);
        Assert.True(c.Yaw * c.Yaw + c.Pitch * c.Pitch <= 1f + 1e-4f);
    }

    [Fact]
    public void FireGate_requires_angle_and_range()
    {
        var t = new GunnerPolicy.Tuning(FireAngleRad: 0.088f);
        Assert.True(GunnerPolicy.FireGate(0.05f, 500f, 1000f, t));    // on target + in range
        Assert.False(GunnerPolicy.FireGate(0.20f, 500f, 1000f, t));   // sweeping
        Assert.False(GunnerPolicy.FireGate(0.05f, 1100f, 1000f, t));  // out of range
    }

    // ── P814b — visible-element bombers fly turret carriers ───────────────

    private static int TurretSeats(StaticCore core, short hullId)
        => core.HullTypes.TryGetValue(hullId, out var h) ? Math.Max(0, h.MaxWeapons - h.MaxFixedWeapons) : 0;

    [SkippableFact]
    public void P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats()
    {
        // The TurretCarrier preference must never pick a FIXED-ONLY hull when the
        // core already fielded a turret-capable one at Best — and ideally it turns
        // a fixed-only Stealth Bomber into a turret carrier. Proved against the
        // real PCore001 corpus across the showcase civs and docks.
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        int turretOnlyAtBest = 0, turnedToTurret = 0, compared = 0;
        foreach (short civ in Civs)
        {
            var (_, techs, docks) = FullTechs(civ);
            foreach (var dock in docks)
            {
                foreach (var target in core.StationTypes.Values.Where(s => s.ClassId == 0))
                {
                    var best = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber, techs, dock, target);
                    var turret = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber, techs, dock, target,
                        strikePref: TechLoadoutSelector.StrikeHullPref.TurretCarrier);
                    if (best is null || turret is null) continue;
                    compared++;
                    int bestSeats = TurretSeats(core, best.HullId), turretSeats = TurretSeats(core, turret.HullId);
                    if (bestSeats > 0)
                        Assert.True(turretSeats > 0,
                            $"civ={civ} dock={dock}: Best picked turret carrier {best.HullName}({bestSeats}) but TurretCarrier chose fixed-only {turret.HullName}");
                    else if (turretSeats > 0) turnedToTurret++;
                    else turretOnlyAtBest++;
                    Assert.True(turretSeats >= bestSeats,
                        $"civ={civ} dock={dock}: TurretCarrier must not LOSE turret seats vs Best ({best.HullName}:{bestSeats} -> {turret.HullName}:{turretSeats})");
                }
            }
        }
        output.WriteLine($"P814b: compared {compared} bomber selections; Best was already turret-capable in {compared - turnedToTurret - turretOnlyAtBest}; "
            + $"TurretCarrier turned a fixed-only Stealth Bomber into a turret carrier {turnedToTurret}x; both fixed-only (no turret bomber within margin) {turretOnlyAtBest}x");
        // The showcase's visible bombers must have a turret carrier to seat gunners —
        // prove at least one real (civ, dock, garrison) combo benefits from the pref.
        Assert.True(turnedToTurret > 0,
            "P814b regression: no (civ,dock,target) made the visible bomber a turret carrier — the live gunner test can never board");
    }

    [SkippableFact]
    public void P814b_showcase_mix_adds_a_gunship_slot_when_a_heavy_exists()
    {
        // Scope 2 — the showcase gets a Heavy/gunship slot when the core can
        // field one, so a turret carrier exists even before any bomber relaunch.
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        bool heavyExists = Showcase.RepresentativeHullOfRole(core, Showcase.HullRole.Heavy) is { };
        // Gunners are opt-in: with them off the mix is the pre-P814 one (no Heavy slot).
        var previous = Environment.GetEnvironmentVariable("ALLEGIANCE_GUNNERS");
        Environment.SetEnvironmentVariable("ALLEGIANCE_GUNNERS", "off");
        GunnerPolicy.ResetCache();
        var offFleet = Showcase.ComposeShowcaseFleetCount(core, 30);
        Environment.SetEnvironmentVariable("ALLEGIANCE_GUNNERS", "on");
        GunnerPolicy.ResetCache();
        IReadOnlyList<Showcase.FleetEntry> fleet;
        try { fleet = Showcase.ComposeShowcaseFleetCount(core, 30); }   // scale 2 — no rounding-to-zero
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_GUNNERS", previous);
            GunnerPolicy.ResetCache();
        }
        Assert.DoesNotContain(offFleet, e => Showcase.ClassifyHull(core.HullTypes[e.HullId]) == Showcase.HullRole.Heavy);
        Assert.Equal(30, fleet.Sum(e => e.Count));
        var heavySlots = fleet.Where(e => Showcase.ClassifyHull(core.HullTypes[e.HullId]) == Showcase.HullRole.Heavy).ToList();
        if (heavyExists)
        {
            Assert.NotEmpty(heavySlots);
            Assert.All(heavySlots, e => Assert.True(
                core.HullTypes[e.HullId].MaxWeapons > core.HullTypes[e.HullId].MaxFixedWeapons,
                $"showcase Heavy slot {e.HullName} is not turret-capable"));
        }
        // The roles are still a readable mix.
        var roles = fleet.Select(e => Showcase.ClassifyHull(core.HullTypes[e.HullId])).Distinct().ToList();
        Assert.True(roles.Count >= 3);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Showcase_heavy_slot_prefers_a_gunship_over_a_capital_ship()
    {
        // The Heavy role also covers capital ships (non-fighter / carrier hulls); the
        // showcase's gunship slot must pick a fighter-class turret hull when one exists.
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var techs = core.ComputeRequiredTechUnion();
        int checkedCivs = 0;
        foreach (short civ in core.Civilizations.Keys)
        {
            bool civHasGunship = core.HullTypes.Values.Any(h => Showcase.IsPlayerBuyable(core, h)
                && TechLoadoutSelector.Classify(h) == Showcase.HullRole.Heavy
                && h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmCarrier));
            var pick = TechLoadoutSelector.SelectAtLocal(core, civ, Showcase.HullRole.Heavy, techs, null);
            if (!civHasGunship || pick is null) continue;
            var hull = core.HullTypes[pick.HullId];
            Assert.True(hull.HasCapability(DataHullTypeIGC.HabmFighter) && !hull.HasCapability(DataHullTypeIGC.HabmCarrier),
                $"civ {civ} heavy slot picked capital ship '{hull.Name}' although it has a gunship");
            checkedCivs++;
        }
        output.WriteLine($"civs checked: {checkedCivs}");
    }
}
