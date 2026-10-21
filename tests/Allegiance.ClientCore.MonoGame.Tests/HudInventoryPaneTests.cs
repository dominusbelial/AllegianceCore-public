using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-7 — pins the native inventory-pane rules resolved by
/// <see cref="HudInventoryPaneBuilder"/> against the native citations in
/// <c>WinTrek/consoledata.cpp</c> and <c>WinTrek/inventoryimage.cpp</c>.
/// </summary>
public class HudInventoryPaneTests
{
    private const short WeaponId = 100, ShieldId = 101, AfterburnerId = 102, CloakId = 103, AmmoPackId = 104, FuelPackId = 105;
    private const short MissileLauncherId = 200, ChaffLauncherId = 201;
    private const short ProjectileId = 7, MissileTypeId = 500;

    private static DataPartTypeIGC Part(short id, string name, DataPartTypeIGC.Subtype type, short partMask = 1) =>
        new(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: name, Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 1f, Signature: 0f,
            PartId: id, SuccessorPartId: -1, PartMask: partMask, InventoryLineMdl: "", Type: type);

    private static DataHullTypeIGC Hull(float speed = 200f, float thrust = 50000f, float recharge = 5f) => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "Fig Adv", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 100f, Signature: 0f, Speed: speed,
        MaxTurnRates: [4f, 4f, 4f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: thrust, SideMultiplier: 1f, BackMultiplier: 1f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 20f, MaxEnergy: 200f, RechargeRate: recharge,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 200, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 3, MaxFixedWeapons: 2, HitPoints: 500f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 1, CapacityDispenser: 1, CapacityChaffLauncher: 1,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "",
        PmEquipment: [1, 1, 1, 1, 1, 1, 1, 1],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0,
        Hardpoints:
        [
            new(0, 0, "", "1", 1, true),
            new(0, 0, "", "2", 1, true),
            new(0, 0, "", "T", 1, false),
        ]);

    private static ShopCatalog Catalog() => new(
        Hulls: [Hull()],
        Parts:
        [
            Part(WeaponId, "Gatling", new DataPartTypeIGC.Weapon(0.1f, 0.125f, 0f, 0f, 3, ProjectileId, 0, 0, 0)),
            Part(ShieldId, "Shield", new DataPartTypeIGC.Shield(10f, 500f, 0, 0, 0)),
            Part(AfterburnerId, "Booster", new DataPartTypeIGC.Afterburner(4f, 10000f, 1f, 1f, 0, 0)),
            Part(CloakId, "Cloak", new DataPartTypeIGC.Cloak(10f, 1f, 1f, 1f, 0, 0)),
            Part(AmmoPackId, "Ammo Pack", new DataPartTypeIGC.Pack(0, 100)),
            Part(FuelPackId, "Fuel Pack", new DataPartTypeIGC.Pack(1, 100)),
        ],
        ProjectileTypes: new Dictionary<short, DataProjectileTypeIGC>
        {
            [ProjectileId] = new([], 0f, 0f, "", "", 10f, 5f, 0f, 1000f, 0.8f, ProjectileId, 0, false, false, 0f, 0),
        },
        MissileTypes: new Dictionary<short, DataMissileTypeIGC>
        {
            [MissileTypeId] = Missile(),
        },
        Launchers:
        [
            new LauncherOffer(Part(MissileLauncherId, "Missile", new DataPartTypeIGC.MissileLauncher(MissileTypeId)), 4, LauncherKind.Missile),
            new LauncherOffer(Part(ChaffLauncherId, "Chaff", new DataPartTypeIGC.ChaffLauncher(600)), 2, LauncherKind.Chaff),
        ]);

    private static DataMissileTypeIGC Missile() => new(
        Color: [], Radius: 0f, Rotation: 0f, ModelName: "", TextureName: "",
        LoadTime: 0f, Lifespan: 2f, Signature: 0f, Price: 0, TimeToBuild: 0,
        LauncherModelName: "", LauncherIconName: "", LauncherName: "m", LauncherDescription: "",
        LauncherGroupId: 0, LauncherTtbmRequired: [], LauncherTtbmEffects: [],
        LauncherSignature: 0f, LauncherMass: 1f, LauncherPartMask: 1, LauncherExpendableSize: 1,
        HitPoints: 0f, DefenseType: 0, ExpendableTypeId: MissileTypeId, EabmCapabilities: 0,
        IconName: "", Acceleration: 100f, TurnRate: 0f, InitialSpeed: 200f,
        LockTime: 0f, ReadyTime: 0f, MaxLock: 1f, ChaffResistance: 0f, Dispersion: 0f,
        LockAngle: 0f, Power: 20f, BlastPower: 10f, BlastRadius: 0f, Width: 0f,
        DamageType: 0, Directional: false, LaunchSound: 0, AmbientSound: 0);

    private static HudInventoryPane Build(
        IReadOnlyList<ExpandedPartData> loadout, IReadOnlyList<ExpandedPartData> cargo,
        float fuel = 0.9f, float energy = 0.8f, float shield = 0.5f, float ammo = 0.6f,
        bool groupFire = false, int selectedMount = 0, int selectedCargoMount = -1,
        float? cloakStrengthPercent = null, int observerCount = 0) =>
        HudInventoryPaneBuilder.Build(Catalog(), Hull(), loadout, cargo, GlobalAttributeSet.Identity,
            fuel, energy, shield, ammo, groupFire, selectedMount, selectedCargoMount, "Fig Adv",
            cloakStrengthPercent: cloakStrengthPercent, observerCount: observerCount);

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void Build_gate_requires_visible_flying_and_alive(bool visible, bool docked, bool dead, bool expected)
    {
        // HUD-7b: the pane is built only while F4 is open and the ship is
        // flying/alive (native clears it in the hangar, inventoryimage.cpp:356-393).
        Assert.Equal(expected, HudInventoryPaneBuilder.ShouldBuild(visible, docked, dead));
    }

    [Fact]
    public void Weapon_rows_follow_native_range_damage_rate_and_selection()
    {
        var loadout = new[] { new ExpandedPartData(WeaponId, 1, 0, ExpandedPartData.FullyMounted) };
        var pane = Build(loadout, Array.Empty<ExpandedPartData>());
        var weapon = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Weapon);
        // Range = speed(1000) * lifespan(0.8) * GA(SpeedAmmo, since CAmmoPerShot=3) = 800m.
        // Damage = (10 + 5) * GA(DamageGuns=1) = 15.
        // Rate = 1 / 0.125 = 8.
        Assert.Equal("800m / 15 X 8", weapon.StatsText);
        Assert.True(weapon.Selected);       // selectedMount 0
        Assert.True(weapon.Active);
        Assert.False(weapon.OutOfAmmo);
        Assert.Equal("1.", weapon.Key);

        var unselected = Build(loadout, Array.Empty<ExpandedPartData>(), selectedMount: 1).Rows.Single(r => r.Kind == HudInventoryRowKind.Weapon);
        Assert.False(unselected.Selected);

        var grouped = Build(loadout, Array.Empty<ExpandedPartData>(), groupFire: true, selectedMount: 1).Rows.Single(r => r.Kind == HudInventoryRowKind.Weapon);
        Assert.True(grouped.Selected);
    }

    [Fact]
    public void Weapon_out_of_ammo_and_afterburner_fuel_drive_the_red_A_flag()
    {
        var weapon = Build([new(WeaponId, 1, 0, ExpandedPartData.FullyMounted)], [], ammo: 0f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Weapon);
        Assert.True(weapon.OutOfAmmo);

        var afterburner = Build([new(AfterburnerId, 1, 0, ExpandedPartData.FullyMounted)], [], fuel: 0f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Afterburner);
        Assert.True(afterburner.OutOfAmmo);
    }

    [Fact]
    public void Shield_strength_and_recharge_match_native_formulas()
    {
        // Strength = 0.5 * 500 = 250; recharge = (500-250)/10 = 25 sec.
        var shield = Build([new(ShieldId, 1, 0, ExpandedPartData.FullyMounted)], [], shield: 0.5f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Shield);
        Assert.Equal("250 / 25 sec", shield.StatsText);
    }

    [Fact]
    public void Afterburner_top_speed_and_endurance_match_native_formulas()
    {
        // Top speed = 200 * (1 + 10000/50000) = 240 mps.
        // Endurance = 0.9*100 / (4*10000) = 90/40000 = 0.00225 -> rounds to 0.
        var ab = Build([new(AfterburnerId, 1, 0, ExpandedPartData.FullyMounted)], [], fuel: 0.9f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Afterburner);
        Assert.Equal("240mps / 0", ab.StatsText);
    }

    [Fact]
    public void Afterburner_top_speed_uses_GA_scaled_max_speed_and_thrust()
    {
        // GetMaxSpeed() = hull.Speed * c_gaMaxSpeed (shipIGC.cpp:4529-4531);
        // GetThrust() = hull.Thrust * c_gaThrust (shipIGC.cpp:4544-4546). With
        // MaxSpeed=2, Thrust=3 the ratio stays 0.2 but the GA-scaled max speed
        // doubles: 200*2*(1 + 10000*3/(50000*3)) = 480 mps (not 240 with raw
        // speed, not 640 with raw thrust and GA ab-thrust).
        var values = GlobalAttributeSet.Identity.ToArray();
        values[GlobalAttributeSet.MaxSpeed] = 2f;
        values[GlobalAttributeSet.Thrust] = 3f;
        var ga = new GlobalAttributeSet(values);
        var ab = HudInventoryPaneBuilder.Build(Catalog(), Hull(),
                [new(AfterburnerId, 1, 0, ExpandedPartData.FullyMounted)], [], ga,
                0.9f, 0.8f, 0.5f, 0.6f, false, 0, -1, "Fig Adv")
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Afterburner);
        Assert.Equal("480mps / 0", ab.StatsText);
    }

    [Fact]
    public void Afterburner_and_cloak_stats_use_the_source_ship_hull()
    {
        // Native GetAfterburnerTopSpeed / GetCloakTimeLeft read the source ship's
        // hull (consoledata.cpp:855,976) — the carrier when gunning. The local
        // hull is the turret; the carrier hull drives top speed and cloak recharge.
        var local = Hull(speed: 200f, thrust: 50000f, recharge: 5f);
        var carrier = Hull(speed: 300f, thrust: 60000f, recharge: 20f);
        var pane = HudInventoryPaneBuilder.Build(Catalog(), local,
            [
                new(AfterburnerId, 1, 0, ExpandedPartData.FullyMounted),
                new(CloakId, 1, 0, ExpandedPartData.FullyMounted),
            ], [], GlobalAttributeSet.Identity,
            0.9f, 0.8f, 0.5f, 0.6f, false, 0, -1, "Fig Adv", sourceHull: carrier);

        var ab = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Afterburner);
        var cloak = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Cloak);
        // Carrier: 300*(1+10000/60000) = 350 mps (local would be 240 mps).
        Assert.Equal("350mps / 0", ab.StatsText);
        // Carrier recharge 20 >= cloak consumption 10 -> Inf. (local recharge 5
        // would yield (0.8*200)/(10-5) = 32 sec).
        Assert.Equal("Inf. sec", cloak.StatsText);
    }

    [Fact]
    public void Cloak_endurance_uses_energy_minus_recharge_and_Inf_for_sustainable_cloaks()
    {
        // (0.8*200)/(10-5) = 160/5 = 32 sec.
        var cloak = Build([new(CloakId, 1, 0, ExpandedPartData.FullyMounted)], [], energy: 0.8f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Cloak);
        Assert.Equal("32 sec", cloak.StatsText);
    }

    [Fact]
    public void Cloak_row_prints_native_strength_percentage_and_endurance()
    {
        // HUD-20 — invcloak.mdl:10-23 is
        // `NumberString(GetCloaking(Me)) + "% / " + CloakTimeLeft + " sec"`.
        // ModelData::GetCloaking = 100*(1-GetCloaking()) for the own/parent ship
        // (consoledata.cpp:480-490); NumberString rounds as (int)(v+0.5).
        var cloak = Build([new(CloakId, 1, 0, ExpandedPartData.FullyMounted)], [],
            energy: 0.8f, cloakStrengthPercent: 37.4f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Cloak);
        Assert.Equal("37% / 32 sec", cloak.StatsText);
    }

    [Fact]
    public void Cloak_row_without_a_strength_update_keeps_the_endurance_only_text()
    {
        // Before the first OwnShipCloakUpdate(220) the strength is unknown; the
        // row must not invent a 0%.
        var cloak = Build([new(CloakId, 1, 0, ExpandedPartData.FullyMounted)], [], energy: 0.8f)
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Cloak);
        Assert.Equal("32 sec", cloak.StatsText);
    }

    [Fact]
    public void Observers_row_sits_between_fixed_weapons_and_turrets()
    {
        // HUD-20 — native ProcessObservers adds one invobserver row only when the
        // count is > 0, after the fixed-weapon loop and before the turret loop
        // (inventoryimage.cpp:320-332). Hull: 2 fixed mounts + 1 turret mount.
        var loadout = new[]
        {
            new ExpandedPartData(WeaponId, 1, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(WeaponId, 1, 2, ExpandedPartData.FullyMounted),
        };
        var rows = Build(loadout, [], observerCount: 2).Rows;
        int observers = rows.ToList().FindIndex(r => r.Kind == HudInventoryRowKind.Observers);
        int firstFixed = rows.ToList().FindIndex(r => r.Kind == HudInventoryRowKind.Weapon);
        int lastFixed = rows.ToList().FindLastIndex(r => r.Key is "1." or "2.");
        int firstTurret = rows.ToList().FindIndex(r => r.Key == "Y.");
        Assert.True(observers > lastFixed, "the observers row follows the fixed-weapon rows");
        Assert.True(observers < firstTurret, "the observers row precedes the turret rows");
        Assert.Equal("Observers: 2", rows[observers].Name);
        Assert.Equal("", rows[observers].Key);
        Assert.Null(rows[observers].StatsText);
        Assert.True(firstFixed >= 0);

        // 0 observers -> no row at all.
        Assert.DoesNotContain(Build(loadout, []).Rows, r => r.Kind == HudInventoryRowKind.Observers);
    }

    [Fact]
    public void Missile_launcher_range_damage_and_count_match_native_formulas()
    {
        // Range = 2 * (200 + 0.5*2*100) = 2*300 = 600m.
        // Damage = (20+10)*1 = 30; count = amount 4.
        var launcher = Build([new(MissileLauncherId, 4, 0, ExpandedPartData.FullyMounted)], [])
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Magazine);
        Assert.Equal("4 / 600m / 30", launcher.StatsText);
    }

    [Fact]
    public void Mounting_progress_selects_ready_state_one()
    {
        // FractionMounted 120 / 240 = 0.5 < 1 -> ReadyState 1 (mounting bar).
        var row = Build([new(WeaponId, 1, 0, 120)], [])
            .Rows.Single(r => r.Kind == HudInventoryRowKind.Weapon);
        Assert.Equal(1, row.ReadyState);
        Assert.Equal(0.5f, row.MountedFraction, 3);
        Assert.Null(row.StatsText);
    }

    [Fact]
    public void Row_order_and_hotkeys_follow_inventoryimage_rebuild()
    {
        var loadout = new[]
        {
            new ExpandedPartData(WeaponId, 1, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(ShieldId, 1, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(AfterburnerId, 1, 0, ExpandedPartData.FullyMounted),
        };
        // Native only emits a slot when it is mounted or has a compatible cargo
        // part (ShipHasPartForMount, inventoryimage.cpp:84-101). An empty cargo
        // mount always shows (invemptycargo.mdl).
        var pane = Build(loadout, []);
        Assert.Equal(new[] { "1.", "7.", "9." },
            pane.Rows.Where(r => !r.IsEmpty).Select(r => r.Key).ToArray());
        Assert.Contains(pane.Rows, r => r is { Kind: HudInventoryRowKind.EmptyCargo, Key: "S." });
        Assert.Equal("Fig Adv", pane.ShipName);
    }

    [Fact]
    public void Empty_slot_affordances_only_appear_when_a_compatible_part_is_held()
    {
        // A hull with no mounted shield but an ammo pack (not a shield) has no
        // shield affordance; once a shield part is in cargo, the empty row shows.
        var noShield = Build([], [new(AmmoPackId, 5, CargoSelector.EncodeMount(-1), ExpandedPartData.FullyMounted)]);
        Assert.DoesNotContain(noShield.Rows, r => r.Kind == HudInventoryRowKind.EmptyEquipment && r.Name == "<shield mount>");

        var withShield = Build([], [new(ShieldId, 1, CargoSelector.EncodeMount(-1), ExpandedPartData.FullyMounted)]);
        Assert.Contains(withShield.Rows, r => r.Kind == HudInventoryRowKind.EmptyEquipment && r.Name == "<shield mount>");
    }

    [Fact]
    public void HudModel_exposes_the_resolved_pane_and_hides_it_without_inventory_visibility()
    {
        var pane = Build([new(WeaponId, 1, 0, ExpandedPartData.FullyMounted)], [], ammo: 0.6f);
        var frame = new HudFrame(.8f, .6f, .6f, .9f, .8f, 1f, 120f, -1f, false, false, true, 0,
            false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);
        var model = HudModel.Create(frame, new() { InventoryVisible = true, InventoryPane = pane });
        Assert.True(model.Elements.Single(e => e.Id == "Flight equipment inventory pane").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Equipment weapon range").Visible);
        Assert.True(model.Elements.Single(e => e.Id == "Equipment selected and active state").Visible);
        Assert.False(model.Elements.Single(e => e.Id == "Inventory turret gunner identity").Visible);
        Assert.False(model.Elements.Single(e => e.Id == "Launcher arming progress").Visible);

        var closed = HudModel.Create(frame, new() { InventoryVisible = false, InventoryPane = pane });
        Assert.False(closed.Elements.Single(e => e.Id == "Flight equipment inventory pane").Visible);
        Assert.False(closed.Elements.Single(e => e.Id == "Equipment weapon range").Visible);
    }

    [Fact]
    public void Cargo_pack_amounts_normalize_to_hull_pools_and_duplicates_collapse()
    {
        // Two ammo-pack entries (50 + 50 = 100) normalize by MaxAmmo 200 -> 0.5,
        // shown once (the higher mount -1 wins the dedup).
        var cargo = new[]
        {
            new ExpandedPartData(AmmoPackId, 50, CargoSelector.EncodeMount(-1), ExpandedPartData.FullyMounted),
            new ExpandedPartData(AmmoPackId, 50, CargoSelector.EncodeMount(-2), ExpandedPartData.FullyMounted),
        };
        var pane = Build([], cargo, selectedCargoMount: -1);
        var cargoRow = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Cargo);
        Assert.Equal(0.5f, cargoRow.Quantity!.Value, 3);
        Assert.Single(pane.Rows, r => r.Kind == HudInventoryRowKind.Cargo);
    }
}
