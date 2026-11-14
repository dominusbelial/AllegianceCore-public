using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1011 — the pure <see cref="ShipStatSheet"/> / <see cref="StatDelta"/>.
/// Fixtures use the real <c>PCore001.igc</c> hulls/parts (Adv Scout 285, Adv
/// Fighter 288, Bomber 124) with hand-computed expected values: acceleration =
/// thrust/mass (load.cpp:990), turn rate = mean of the three axes in deg/s
/// (load.cpp:991-1006), weapon damage/sec = (power+blastPower)×(1/dtimeBurst)
/// (load.cpp:1072-1073), shield RECHARGE = strength/regeneration
/// (load.cpp:1193-1194), missile range = initialSpeed·lifespan + ½·accel·lifespan²
/// (load.cpp:1103-1109), chaff strength curve (load.cpp:1178-1184).
/// </summary>
public class P1011ShipStatSheetTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly IShipStatLookup Lookup = new StaticCoreShipStatLookup(
        Catalog.Core ?? throw new InvalidOperationException("the loaded shop catalogue carries no core"));

    private static DataHullTypeIGC Hull(short id) => Catalog.Hulls.First(h => h.HullId == id);

    private static DataPartTypeIGC Part(short partId)
    {
        foreach (var p in Catalog.Parts) if (p.PartId == partId) return p;
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Part;
        throw new InvalidOperationException($"part {partId} is not in the PCore001 catalogue");
    }

    private static MountedStatPart M(short partId, int mount = 0) => new(Part(partId), mount);

    private static ShipStatSheet Sheet(short hullId, params MountedStatPart[] parts)
        => ShipStatSheet.Build(Hull(hullId), parts, GlobalAttributeSet.Identity, Lookup);

    private static ShipStat SheetStat(ShipStatSheet sheet, EquipmentType type, int mount, string field)
        => sheet.Stat(StatKey.Slot(type, mount, field));

    private static void AssertNear(double? actual, double expected, int precision = 3)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, precision);
    }

    // ── fixtures (PCore001) ─────────────────────────────────────────────────
    // Adv Scout 285: mass 40, sig 0.5, speed 180, thrust 1200, turns 0.872665
    //   rad/s (50°/s), scan 3000, fuel 5, energy 1500, recharge 60, hp 225,
    //   ammo 800, defense 1 (Light), capacities 15/2/5.
    // Adv Fighter 288: mass 30, sig 1, speed 120, thrust 750, turns 1.0472
    //   rad/s (60°/s), scan 800, fuel 14, energy 1500, recharge 90, hp 450,
    //   ammo 900, defense 1 (Light), capacities 25/1/4.
    // Bomber 124: mass 50, sig 2, speed 60, thrust 750, turns 0.349066 rad/s
    //   (20°/s), scan 1600, fuel 5, energy 1500, recharge 90, hp 800, ammo
    //   1200, defense 2 (Medium), capacities 60/0/8.
    private static readonly MountedStatPart[] ScoutDefault =
    [
        M(80),          // Counter 1 (chaff, mass .25 sig 0)
        M(81),          // EWS Probe 1 (dispenser, mass 1 sig 0)
        M(33, 0),       // PW Gat Gun 1 (mass 1 sig 2)
        M(153),         // MRM Seeker 1 (magazine, mass 1 sig .5)
        M(21),          // Sm Shield 1 (mass 2 sig .5)
    ];

    private static readonly MountedStatPart[] FighterDefault =
    [
        M(174),         // Hvy Booster (afterburner, mass 2 sig 1.5)
        M(80),          // Counter 1 (mass .25)
        M(13),          // Mine Pack 1 (dispenser, mass 3)
        M(33, 0), M(33, 1), M(33, 2),
        M(21),          // Sm Shield 1 (mass 2 sig .5)
        M(150),         // SRM Dumbfire 1 (magazine, mass 1 sig .25)
    ];

    private static readonly MountedStatPart[] BomberDefault =
    [
        M(129, 0), M(129, 1),   // EW Pulse Laser on the two fixed hardpoints
        M(130, 2), M(130, 3),   // PW AutoCan 1 on the two turret hardpoints
        M(22),                  // Med Shield 1 (mass 4 sig .75)
        M(157),                 // SRM Anti-Base 1 (magazine, mass 3 sig 1)
        M(80),                  // Counter 1 (mass .25)
    ];

    // ── native label sets ───────────────────────────────────────────────────

    /// <summary>⭐ Native hull info pane labels, verbatim (hullinfobmp.mdl; load.cpp:986-1054).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_rows_use_the_native_hull_info_labels_in_native_order()
    {
        Require.Artwork();
        var sheet = Sheet(285);

        var labels = sheet.Stats
            .Where(s => s.Key.Scope == StatScope.Hull && s.Group == StatGroup.HullNative)
            .Select(s => s.Label)
            .ToArray();

        Assert.Equal(
            new[] { "MAX SPEED", "ACCELERATION", "TURN RATE", "HIT POINTS", "ENERGY", "SCAN RANGE", "SIGNATURE", "DEFENSE CLASS" },
            labels);
        // Every native row carries a real value/text; nothing is left unresolved.
        Assert.All(sheet.Stats.Where(s => s.Group == StatGroup.HullNative),
            s => Assert.True(s.Value is not null || s.Text is not null, $"{s.Label} must have a value"));
    }

    /// <summary>⭐ Every part-info pane's labels, exactly as native (partInfo*bmp.mdl + partinfo.mdl).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_rows_use_the_native_part_info_labels_per_equipment_type()
    {
        Require.Artwork();

        Assert.Equal(new[] { "STRENGTH", "RECHARGE", "SIGNATURE", "DEFENSE TYPE" },
            SlotLabels(Sheet(285, M(21)), EquipmentType.Shield));
        Assert.Equal(new[] { "DAMAGE/SEC", "SHOTS/SEC", "SIGNATURE", "RANGE", "SPEED", "DAMAGE CLASS", "DAMAGE PER SHOT" },
            SlotLabels(Sheet(285, M(33, 0)), EquipmentType.Weapon));
        Assert.Equal(new[] { "DAMAGE", "SIGNATURE", "RANGE", "LOCK TIME", "MAX LOCK", "DAMAGE CLASS" },
            SlotLabels(Sheet(285, M(153)), EquipmentType.Magazine));
        Assert.Equal(new[] { "HIT POINTS", "RANGE", "LIFESPAN", "VISIBILITY" },
            SlotLabels(Sheet(285, M(81)), EquipmentType.Dispenser));
        Assert.Equal(new[] { "DAMAGE", "RADIUS", "LIFESPAN", "VISIBILITY" },
            SlotLabels(Sheet(285, M(13)), EquipmentType.Dispenser));
        Assert.Equal(new[] { "STRENGTH" }, SlotLabels(Sheet(285, M(80)), EquipmentType.ChaffLauncher));
        Assert.Equal(new[] { "TOP SPEED", "DURATION", "SIGNATURE" },
            SlotLabels(Sheet(288, M(174)), EquipmentType.Afterburner));
    }

    private static string[] SlotLabels(ShipStatSheet sheet, EquipmentType type)
        => sheet.Stats.Where(s => s.Key.Scope == StatScope.Slot && s.Key.SlotType == type)
            .Select(s => s.Label).ToArray();

    // ── 3 hulls × 2 loadouts ────────────────────────────────────────────────

    /// <summary>⭐ Adv Scout (285) BARE: the hull's own numbers, no parts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_scout_bare_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(285);

        Assert.Equal(40.0, sheet.Mass, 3);
        Assert.Equal(50.0, sheet.SignaturePercent, 3);          // 0.5 / 1 × 100
        Assert.Equal(180.0, sheet.MaxSpeed, 3);
        Assert.Equal(30.0, sheet.Acceleration, 3);              // 1200 / 40
        Assert.Equal(50.0, sheet.TurnRateDegrees, 3);           // 0.872665 rad/s = 50°/s
        Assert.Equal(225.0, sheet.HullHitPoints, 3);
        Assert.Equal(1500.0, sheet.Energy, 3);
        Assert.Equal(3000.0, sheet.ScanRange, 3);
        Assert.Equal("Light", sheet.Stat(ShipStatField.DefenseClass).Text);
        Assert.Equal(60.0, sheet.Stat(ShipStatField.EnergyRecharge).Value!.Value, 3);
        Assert.Equal(5.0, sheet.Stat(ShipStatField.Fuel).Value!.Value, 3);
        Assert.Equal(800.0, sheet.Stat(ShipStatField.AmmoCapacity).Value!.Value, 3);
        Assert.Equal(15.0, sheet.Stat(ShipStatField.CargoMagazine).Value!.Value, 3);
        Assert.Equal(2.0, sheet.Stat(ShipStatField.CargoDispenser).Value!.Value, 3);
        Assert.Equal(5.0, sheet.Stat(ShipStatField.CargoChaff).Value!.Value, 3);
        Assert.Empty(sheet.Guns);
    }

    /// <summary>⭐ Adv Scout (285) DEFAULT: native preferred parts (Counter, EWS Probe,
    /// Gat Gun, MRM Seeker, Sm Shield). mass 45.25, signature 350 %.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_scout_default_loadout_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(285, ScoutDefault);

        Assert.Equal(45.25, sheet.Mass, 3);                     // 40 + .25 + 1 + 1 + 1 + 2
        Assert.Equal(350.0, sheet.SignaturePercent, 3);         // (0.5 + 3) / 1 × 100
        Assert.Equal(26.519, sheet.Acceleration, 3);            // 1200 / 45.25

        var shield = SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.ShieldStrength);
        AssertNear(shield.Value, 150.0);
        AssertNear(SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.ShieldRecharge).Value, 75.0);   // 150 / (2 × 1)
        AssertNear(SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.Signature).Value, 50.0);        // 0.5 × 100
        Assert.Equal("normal", SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.DefenseType).Text);

        // PW Gat Gun 1: power 3.2, dtimeBurst 0.1, ammo round (speed 600, lifespan 1).
        Assert.Single(sheet.Guns);
        var gun = sheet.Guns[0];
        Assert.Equal(0, gun.Mount);
        Assert.Equal(33, gun.PartId);
        AssertNear(gun.DamagePerShot, 3.2);
        AssertNear(gun.ShotsPerSecond, 10.0);
        AssertNear(gun.DamagePerSecond, 32.0);                  // 3.2 × 10 × 1
        AssertNear(gun.Range, 600.0);                           // 600 × 1
        AssertNear(gun.Speed, 600.0);
        Assert.Equal((byte)2, gun.DamageType);
        Assert.Equal("light", gun.DamageClass);
        AssertNear(SheetStat(sheet, EquipmentType.Weapon, 0, ShipStatField.DamagePerSecond).Value, 32.0);

        // MRM Seeker 1: power 60, initialSpeed 25, accel 60, lifespan 6, lock 2, maxLock .75.
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.MissileDamage).Value, 60.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.Range).Value, 1230.0);         // 25×6 + ½×60×36
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.LockTime).Value, 2.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.MaxLock).Value, 75.0);
        Assert.Equal("light-medium", SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.DamageClass).Text);

        // EWS Probe 1: no projectile → scannerRange 500 × GA[ScanRange]; visibility .3 × 100.
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.Range).Value, 500.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.Visibility).Value, 30.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.ProbeHitPoints).Value, 25.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.Lifespan).Value, 3600.0);

        // Counter 1: chaffStrength 1.5 → 1 − 1/(2×1.5) = 66.67 %.
        AssertNear(SheetStat(sheet, EquipmentType.ChaffLauncher, 0, ShipStatField.ChaffStrength).Value, 66.667, 2);
    }

    /// <summary>⭐ Adv Fighter (288) BARE.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_fighter_bare_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(288);

        Assert.Equal(30.0, sheet.Mass, 3);
        Assert.Equal(100.0, sheet.SignaturePercent, 3);
        Assert.Equal(120.0, sheet.MaxSpeed, 3);
        Assert.Equal(25.0, sheet.Acceleration, 3);              // 750 / 30
        Assert.Equal(60.0, sheet.TurnRateDegrees, 3);           // 1.0472 rad/s = 60°/s
        Assert.Equal(450.0, sheet.HullHitPoints, 3);
        Assert.Equal(1500.0, sheet.Energy, 3);
        Assert.Equal(800.0, sheet.ScanRange, 3);
        Assert.Equal("Light", sheet.Stat(ShipStatField.DefenseClass).Text);
        Assert.Empty(sheet.Guns);
    }

    /// <summary>⭐ Adv Fighter (288) DEFAULT: 3 Gat Guns, Hvy Booster, Counter,
    /// Mine Pack, Sm Shield, SRM Dumbfire — mass 41.25, signature 925 %.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_fighter_default_loadout_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(288, FighterDefault);

        Assert.Equal(41.25, sheet.Mass, 3);                     // 30 + 2 + .25 + 3 + 3 + 2 + 1
        Assert.Equal(925.0, sheet.SignaturePercent, 3);         // (1 + 8.25) × 100
        Assert.Equal(18.182, sheet.Acceleration, 3);            // 750 / 41.25

        Assert.Equal(3, sheet.Guns.Count);
        Assert.Equal(new[] { 0, 1, 2 }, sheet.Guns.Select(g => g.Mount).ToArray());
        Assert.All(sheet.Guns, g => AssertNear(g.DamagePerSecond, 32.0));

        // Hvy Booster: maxThrust 1350, raw hull thrust 750 → 120 × (1 + 1.8) = 336;
        // duration = hull fuel 14 / (1350 × .00094435) ≈ 10.98 s.
        AssertNear(SheetStat(sheet, EquipmentType.Afterburner, 0, ShipStatField.AfterburnerTopSpeed).Value, 336.0);
        AssertNear(SheetStat(sheet, EquipmentType.Afterburner, 0, ShipStatField.AfterburnerDuration).Value, 10.98, 2);

        // Mine Pack 1: power 700, MineRadius 75, lifespan 5, signature .15 → 15 %.
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.MineDamage).Value, 700.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.MineRadius).Value, 75.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.Lifespan).Value, 5.0);
        AssertNear(SheetStat(sheet, EquipmentType.Dispenser, 0, ShipStatField.Visibility).Value, 15.0);

        // SRM Dumbfire 1: power 100, 25×4 + ½×100×16 = 900, no lock.
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.MissileDamage).Value, 100.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.Range).Value, 900.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.LockTime).Value, 0.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.MaxLock).Value, 0.0);
    }

    /// <summary>⭐ Bomber (124) BARE.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bomber_bare_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(124);

        Assert.Equal(50.0, sheet.Mass, 3);
        Assert.Equal(200.0, sheet.SignaturePercent, 3);
        Assert.Equal(60.0, sheet.MaxSpeed, 3);
        Assert.Equal(15.0, sheet.Acceleration, 3);              // 750 / 50
        Assert.Equal(20.0, sheet.TurnRateDegrees, 3);           // 0.349066 rad/s = 20°/s
        Assert.Equal(800.0, sheet.HullHitPoints, 3);
        Assert.Equal(1500.0, sheet.Energy, 3);
        Assert.Equal(1600.0, sheet.ScanRange, 3);
        Assert.Equal("Medium", sheet.Stat(ShipStatField.DefenseClass).Text);
        Assert.Empty(sheet.Guns);
    }

    /// <summary>⭐ Bomber (124) DEFAULT: two fixed EW Pulse Lasers, two turret
    /// PW AutoCans, Med Shield, SRM Anti-Base, Counter — mass 61.25, 1175 %.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bomber_default_loadout_hand_computed_fixture()
    {
        Require.Artwork();
        var sheet = Sheet(124, BomberDefault);

        Assert.Equal(61.25, sheet.Mass, 3);                     // 50 + 2 + 2 + 4 + 3 + .25
        Assert.Equal(1175.0, sheet.SignaturePercent, 3);        // (2 + 9.75) × 100
        Assert.Equal(12.245, sheet.Acceleration, 3);            // 750 / 61.25

        Assert.Equal(4, sheet.Guns.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, sheet.Guns.Select(g => g.Mount).ToArray());
        Assert.Equal("EW Pulse Laser", sheet.Guns[0].Name);
        Assert.Equal("PW AutoCan 1", sheet.Guns[2].Name);

        // EW Pulse Laser: power 5, burst .1 (energy round: speed 1250, lifespan .5).
        AssertNear(sheet.Guns[0].DamagePerSecond, 50.0);
        AssertNear(sheet.Guns[0].Range, 625.0);                 // 1250 × .5
        AssertNear(sheet.Guns[0].Speed, 1250.0);
        Assert.Equal("very light", sheet.Guns[0].DamageClass);  // damage type 5
        Assert.False(sheet.Guns[0].UsesAmmo);

        // PW AutoCan 1: power 9.79, burst .083 (ammo round: speed 1000, lifespan 1.5).
        AssertNear(sheet.Guns[2].DamagePerSecond, 117.952, 2);
        AssertNear(sheet.Guns[2].ShotsPerSecond, 12.048, 2);
        AssertNear(sheet.Guns[2].Range, 1500.0);
        Assert.Equal("very light", sheet.Guns[2].DamageClass);  // damage type 11

        // Med Shield 1: strength 300, regeneration 5 → RECHARGE 60 s.
        AssertNear(SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.ShieldStrength).Value, 300.0);
        AssertNear(SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.ShieldRecharge).Value, 60.0);

        // SRM Anti-Base 1: power 400, 50×5 + ½×60×25 = 1000, class "stations".
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.MissileDamage).Value, 400.0);
        AssertNear(SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.Range).Value, 1000.0);
        Assert.Equal("stations", SheetStat(sheet, EquipmentType.Magazine, 0, ShipStatField.DamageClass).Text);
    }

    // ── global attributes ───────────────────────────────────────────────────

    /// <summary>⭐ The side's GlobalAttributeSet is applied exactly like the sim's
    /// Ship.Effective* helpers (port of native MyHullType, shipIGC.cpp:4529-4580).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Global_attributes_apply_the_same_factors_as_the_sim()
    {
        Require.Artwork();
        var values = GlobalAttributeSet.Identity.ToArray();
        values[GlobalAttributeSet.MaxSpeed] = 1.25f;
        values[GlobalAttributeSet.Thrust] = 1.1f;
        values[GlobalAttributeSet.TurnRate] = 1.2f;
        values[GlobalAttributeSet.MaxArmorShip] = 1.5f;
        values[GlobalAttributeSet.MaxShieldShip] = 2.0f;
        values[GlobalAttributeSet.ShieldRegenerationShip] = 0.5f;
        values[GlobalAttributeSet.MaxEnergy] = 1.2f;
        values[GlobalAttributeSet.ScanRange] = 1.3f;
        values[GlobalAttributeSet.Signature] = 2.0f;
        values[GlobalAttributeSet.DamageGuns] = 1.1f;
        values[GlobalAttributeSet.SpeedAmmo] = 1.2f;
        values[GlobalAttributeSet.LifespanEnergy] = 1.4f;
        values[GlobalAttributeSet.DamageMissiles] = 1.5f;
        var ga = new GlobalAttributeSet(values);

        var plain = Sheet(285, ScoutDefault);
        var boosted = ShipStatSheet.Build(Hull(285), ScoutDefault, ga, Lookup);

        Assert.Equal(225.0, boosted.MaxSpeed, 3);               // 180 × 1.25
        Assert.Equal(29.171, boosted.Acceleration, 3);          // 1200 × 1.1 / 45.25
        Assert.Equal(60.0, boosted.TurnRateDegrees, 3);         // 50 × 1.2
        Assert.Equal(337.5, boosted.HullHitPoints, 3);          // 225 × 1.5
        Assert.Equal(1800.0, boosted.Energy, 3);                // 1500 × 1.2
        Assert.Equal(3900.0, boosted.ScanRange, 3);             // 3000 × 1.3
        Assert.Equal(175.0, boosted.SignaturePercent, 3);       // 3.5 / 2 × 100
        Assert.Equal(45.25, boosted.Mass, 3);                   // mass carries no GA factor
        Assert.Equal(60.0, boosted.Stat(ShipStatField.EnergyRecharge).Value!.Value, 3);  // no GA on recharge

        // Shield: strength × GA[MaxShieldShip]; RECHARGE = strength / (regen × GA factor).
        AssertNear(SheetStat(boosted, EquipmentType.Shield, 0, ShipStatField.ShieldStrength).Value, 300.0);
        AssertNear(SheetStat(boosted, EquipmentType.Shield, 0, ShipStatField.ShieldRecharge).Value, 300.0);
        AssertNear(SheetStat(boosted, EquipmentType.Shield, 0, ShipStatField.Signature).Value, 25.0);

        // Gun: DPS × GA[DamageGuns]; ammo round speed × GA[SpeedAmmo]; range follows speed.
        AssertNear(boosted.Guns[0].DamagePerSecond, 35.2);
        AssertNear(boosted.Guns[0].Speed, 720.0);
        AssertNear(boosted.Guns[0].Range, 720.0);

        // Missile damage × GA[DamageMissiles], range untouched.
        AssertNear(SheetStat(boosted, EquipmentType.Magazine, 0, ShipStatField.MissileDamage).Value, 90.0);
        AssertNear(SheetStat(boosted, EquipmentType.Magazine, 0, ShipStatField.Range).Value, 1230.0);

        // Probe scanner range × GA[ScanRange]; visibility ÷ GA[Signature].
        AssertNear(SheetStat(boosted, EquipmentType.Dispenser, 0, ShipStatField.Range).Value, 650.0);
        AssertNear(SheetStat(boosted, EquipmentType.Dispenser, 0, ShipStatField.Visibility).Value, 15.0);

        // GA off vs on is a real change; the bare identity sheet matches the sim's own one-line formulas.
        Assert.NotEqual(plain.MaxSpeed, boosted.MaxSpeed);
    }

    // ── deltas ──────────────────────────────────────────────────────────────

    /// <summary>⭐ Hovering an offer: replacing the Gat Gun with an EW Pulse Laser
    /// yields a same-key delta (absolute, sign, better/worse) plus a class change.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_swap_delta_reports_absolute_sign_and_direction()
    {
        Require.Artwork();
        var current = Sheet(285, ScoutDefault);
        var hovered = current.WithPart(EquipmentType.Weapon, 0, Part(129));

        var deltas = StatDelta.Between(current, hovered);
        StatDelta D(string field) => deltas.Single(d =>
            d.Key == StatKey.Slot(EquipmentType.Weapon, 0, field));

        var dps = D(ShipStatField.DamagePerSecond);
        Assert.Equal(32.0, dps.OldValue!.Value, 3);
        Assert.Equal(50.0, dps.NewValue!.Value, 3);             // pulse laser 5 × 10
        Assert.Equal(18.0, dps.Difference, 3);
        Assert.Equal(1, dps.Sign);
        Assert.Equal(StatOutcome.Better, dps.Outcome);

        // Both guns fire a 0.1 s burst, so SHOTS/SEC is identical.
        var shots = D(ShipStatField.ShotsPerSecond);
        Assert.Equal(StatOutcome.Same, shots.Outcome);
        var range = D(ShipStatField.Range);
        Assert.Equal(600.0, range.OldValue!.Value, 3);
        Assert.Equal(625.0, range.NewValue!.Value, 3);
        Assert.Equal(StatOutcome.Better, range.Outcome);
        var speed = D(ShipStatField.ProjectileSpeed);
        Assert.Equal(StatOutcome.Better, speed.Outcome);        // 600 → 1250

        // The native damage class is a name, not a number: reported as Changed.
        var cls = D(ShipStatField.DamageClass);
        Assert.Equal("light", cls.OldText);
        Assert.Equal("very light", cls.NewText);
        Assert.Equal(StatOutcome.Changed, cls.Outcome);
        Assert.Equal(0, cls.Sign);

        // Same gun mass (1) and same part signature (2): those rows do not move.
        Assert.Equal(StatOutcome.Same, deltas.Single(d => d.Key == StatKey.Ship(ShipStatField.Mass)).Outcome);
        Assert.Equal(StatOutcome.Same, deltas.Single(d => d.Key == StatKey.Hull(ShipStatField.Signature)).Outcome);

        var (better, worse) = StatDelta.Count(deltas);
        Assert.Equal(4, better);     // DPS, damage/shot, range, speed
        Assert.Equal(0, worse);
    }

    /// <summary>⭐ Lower-is-better rows invert the verdict: a smaller shield
    /// signature is better, a longer recharge time is worse.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lower_is_better_rows_report_better_and_worse_correctly()
    {
        Require.Artwork();
        var current = Sheet(285, ScoutDefault);
        var hovered = current.WithPart(EquipmentType.Shield, 0, Part(22));   // Med Shield 1

        var deltas = StatDelta.Between(current, hovered);
        StatDelta D(string field) => deltas.Single(d =>
            d.Key == StatKey.Slot(EquipmentType.Shield, 0, field));

        Assert.Equal(StatOutcome.Better, D(ShipStatField.ShieldStrength).Outcome);  // 150 → 300
        Assert.Equal(150.0, D(ShipStatField.ShieldStrength).Difference, 3);
        Assert.Equal(StatOutcome.Better, D(ShipStatField.ShieldRecharge).Outcome);  // 75 s → 60 s
        Assert.Equal(-15.0, D(ShipStatField.ShieldRecharge).Difference, 3);
        Assert.Equal(-1, D(ShipStatField.ShieldRecharge).Sign);

        // Signature and mass are lower-is-better: the heavier shield reads worse.
        Assert.Equal(StatOutcome.Worse, D(ShipStatField.Signature).Outcome);        // 50 % → 75 %
        Assert.Equal(StatOutcome.Worse, deltas.Single(d => d.Key == StatKey.Ship(ShipStatField.Mass)).Outcome);
        Assert.Equal(StatOutcome.Worse, deltas.Single(d => d.Key == StatKey.Hull(ShipStatField.Signature)).Outcome);
    }

    /// <summary>⭐ A hull switch rebases the same part list onto the new hull: the
    /// hull rows change with the right direction while slot rows stay put.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_switch_delta_uses_the_native_hull_rows()
    {
        Require.Artwork();
        var scout = Sheet(285, ScoutDefault);
        var fighter = scout.WithHull(Hull(288));

        var deltas = StatDelta.Between(scout, fighter);
        StatDelta H(string field) => deltas.Single(d => d.Key == StatKey.Hull(field));

        Assert.Equal(StatOutcome.Worse, H(ShipStatField.MaxSpeed).Outcome);      // 180 → 120
        Assert.Equal(StatOutcome.Better, H(ShipStatField.TurnRate).Outcome);     // 50 → 60 °/s
        Assert.Equal(StatOutcome.Better, H(ShipStatField.HitPoints).Outcome);    // 225 → 450
        Assert.Equal(StatOutcome.Worse, H(ShipStatField.Signature).Outcome);     // 350 % → 450 %
        Assert.Equal(-2200.0, H(ShipStatField.ScanRange).Difference, 3);         // 3000 → 800
        Assert.Equal(StatOutcome.Worse, H(ShipStatField.ScanRange).Outcome);
        Assert.Equal(-5.243, H(ShipStatField.Acceleration).Difference, 3);       // 1200/45.25 → 750/35.25
        Assert.Equal(StatOutcome.Worse, H(ShipStatField.Acceleration).Outcome);

        // Slot rows are unchanged — the same parts, a different hull.
        Assert.Equal(StatOutcome.Same,
            deltas.Single(d => d.Key == StatKey.Slot(EquipmentType.Shield, 0, ShipStatField.ShieldStrength)).Outcome);
    }

    /// <summary>A hull with more guns than the other reports the extra guns as Added rows.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Extra_guns_on_the_new_hull_are_added_rows()
    {
        Require.Artwork();
        var scout = Sheet(285, ScoutDefault);
        var fighter = Sheet(288, FighterDefault);

        var deltas = StatDelta.Between(scout, fighter);
        Assert.Contains(deltas, d => d.Outcome == StatOutcome.Added
            && d.Key == StatKey.Slot(EquipmentType.Weapon, 1, ShipStatField.DamagePerSecond));
        Assert.Contains(deltas, d => d.Outcome == StatOutcome.Added
            && d.Key == StatKey.Slot(EquipmentType.Weapon, 2, ShipStatField.DamagePerSecond));
        // Mount 0 exists on both sheets, so it is a real comparison.
        Assert.Contains(deltas, d => d.Key == StatKey.Slot(EquipmentType.Weapon, 0, ShipStatField.DamagePerSecond)
            && d.Outcome == StatOutcome.Same);
    }

    // ── robustness ──────────────────────────────────────────────────────────

    /// <summary>Without a core lookup the ordnance rows keep native labels but no numbers.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Missing_core_lookup_keeps_labels_and_nulls_numeric_ordnance_rows()
    {
        Require.Artwork();
        var sheet = ShipStatSheet.Build(Hull(285), ScoutDefault, GlobalAttributeSet.Identity, null);

        // The gun's slot identity survives without a core, but its damage numbers do not.
        Assert.Single(sheet.Guns);
        Assert.Equal(0, sheet.Guns[0].Mount);
        Assert.True(double.IsNaN(sheet.Guns[0].DamagePerSecond));
        var dps = SheetStat(sheet, EquipmentType.Weapon, 0, ShipStatField.DamagePerSecond);
        Assert.Equal("DAMAGE/SEC", dps.Label);
        Assert.Null(dps.Value);
        // Values that need no type table are still there.
        AssertNear(SheetStat(sheet, EquipmentType.Shield, 0, ShipStatField.ShieldStrength).Value, 150.0);
        AssertNear(sheet.Acceleration, 26.519);
    }

    /// <summary>Keys are unique — the delta pairing relies on it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stat_keys_are_unique_on_every_fixture()
    {
        Require.Artwork();
        foreach (var sheet in new[] { Sheet(285, ScoutDefault), Sheet(288, FighterDefault), Sheet(124, BomberDefault) })
        {
            var keys = sheet.Stats.Select(s => s.Key).ToArray();
            Assert.Equal(keys.Length, keys.Distinct().Count());
        }
    }
}
