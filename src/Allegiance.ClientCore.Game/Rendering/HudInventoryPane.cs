using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The native inventory-row family (WinTrek/inventoryimage.cpp:299-346).
/// Each kind has its own native <c>inv*.mdl</c> row layout and icon.</summary>
public enum HudInventoryRowKind
{
    Weapon,          // invsweapon.mdl — fixed/turret weapon mount
    Observers,       // invobserver.mdl — "Observers: N" (inventoryimage.cpp:141-169,320-332)
    Magazine,        // invsmissile.mdl — missile launcher
    Chaff,           // invchaff.mdl — chaff dispenser
    Shield,          // invshield.mdl
    Cloak,           // invcloak.mdl
    Afterburner,     // invafterburn.mdl
    Dispenser,       // invsmine.mdl — mine/probe dispenser
    Cargo,           // cargo part (native part row at a negative mount)
    EmptyWeapon,     // invemptyweapon.mdl — eligible empty weapon slot
    EmptyEquipment,  // invempty<kind>.mdl — eligible empty equipment slot
    EmptyCargo,      // invemptycargo.mdl — empty cargo slot
}

/// <summary>One resolved native inventory row. Graphics-independent: the
/// renderers decide glyphs/art, this carries the native semantic columns
/// (key, name, selected/active state, out-of-ammo flag, ready-state and its
/// stats text or progress fractions).</summary>
public readonly record struct HudInventoryRow(
    HudInventoryRowKind Kind,
    string Key,
    string Name,
    bool Selected,
    bool Active,
    bool OutOfAmmo,
    string? StatsText,
    int ReadyState,
    float MountedFraction,
    float? ArmedFraction,
    float? Quantity)
{
    public bool IsWeapon => Kind == HudInventoryRowKind.Weapon;
    public bool IsEmpty => Kind is HudInventoryRowKind.EmptyWeapon or HudInventoryRowKind.EmptyEquipment or HudInventoryRowKind.EmptyCargo;
}

/// <summary>The native equipment/inventory pane (dialog.mdl:5385-5393):
/// own-ship name header plus the ordered rows below it.</summary>
public sealed record HudInventoryPane(string ShipName, IReadOnlyList<HudInventoryRow> Rows)
{
    public static HudInventoryPane Empty { get; } = new("", Array.Empty<HudInventoryRow>());
}

/// <summary>
/// Pure derivation of the native flight equipment/inventory pane
/// (<c>WinTrek/inventoryimage.cpp:299-346</c>, <c>WinTrek/consoledata.cpp:684-985</c>).
/// Takes the granted loadout + cargo, the static core catalogue, the own hull,
/// the side's global attributes and the live resource fractions, and produces
/// the resolved rows a renderer presents. No MonoGame types, no gameplay change.
/// </summary>
public static class HudInventoryPaneBuilder
{
    /// <summary>HUD-7 — the build gate for the native pane: only while F4 is
    /// open, the source ship is flying (native clears the pane in the hangar,
    /// inventoryimage.cpp:356-393) and alive. The gate replaces the former
    /// every-frame build while hidden.</summary>
    public static bool ShouldBuild(bool inventoryVisible, bool docked, bool dead)
        => inventoryVisible && !docked && !dead;

    // Native hotkey vocabulary (WinTrek/inventoryimage.cpp:238-241).
    private static readonly string[] WeaponKeys = ["1.", "2.", "3.", "4."];
    private static readonly string[] TurretKeys = ["Y.", "U.", "I.", "O.", "", "", "", ""];

    private readonly record struct Ctx(
        ShopCatalog? Catalog,
        DataHullTypeIGC Hull,
        DataHullTypeIGC SourceHull,
        GlobalAttributeSet Attributes,
        float FuelFraction,
        float EnergyFraction,
        float ShieldFraction,
        float AmmoFraction,
        bool GroupFire,
        int SelectedMount,
        float? CloakStrengthPercent,
        Func<EquipmentType, float, float>? ArmedFraction);

    public static HudInventoryPane Build(
        ShopCatalog? catalog,
        DataHullTypeIGC? hull,
        IReadOnlyList<ExpandedPartData> loadout,
        IReadOnlyList<ExpandedPartData> cargo,
        GlobalAttributeSet attributes,
        float fuelFraction,
        float energyFraction,
        float shieldFraction,
        float ammoFraction,
        bool groupFire,
        int selectedMount,
        int selectedCargoMount,
        string ownShipName,
        bool isPilot = true,
        DataHullTypeIGC? sourceHull = null,
        float? cloakStrengthPercent = null,
        int observerCount = 0,
        // P786 — the own launcher arming clocks (native GetArmedFraction,
        // consoledata.cpp:1189-1202): a launcher row's ReadyState 2 + its
        // arming-bar fill. Null keeps the pre-P786 behaviour (mounted
        // launchers resolve straight to ready).
        Func<EquipmentType, float, float>? armedFraction = null)
    {
        var rows = new List<HudInventoryRow>();
        if (hull is not { } h)
            return new HudInventoryPane(ownShipName, rows);

        var mounted = loadout ?? Array.Empty<ExpandedPartData>();
        var hold = cargo ?? Array.Empty<ExpandedPartData>();
        var all = mounted.Concat(hold).ToList();
        // Native Rebuild (inventoryimage.cpp:289-291) runs the pane off the
        // source ship; the stats formulas that read a hull use
        // GetSourceShip()->GetHullType() (consoledata.cpp:855,976) — the
        // carrier's hull when the local pilot is a turret gunner.
        var ctx = new Ctx(catalog, h, sourceHull ?? h, attributes, fuelFraction,
            energyFraction, shieldFraction, ammoFraction, groupFire, selectedMount, cloakStrengthPercent,
            armedFraction);

        int maxFixed = Math.Min(h.MaxFixedWeapons, h.Hardpoints.Length);
        int maxWeapons = Math.Min(h.MaxWeapons, h.Hardpoints.Length);

        // Fixed weapons — WinTrek/inventoryimage.cpp:242-246.
        for (int mount = 0; mount < maxFixed; mount++)
            AddPart(rows, ctx, all, EquipmentType.Weapon, mount,
                isPilot && mount < WeaponKeys.Length ? WeaponKeys[mount] : "");

        // Observers — inventoryimage.cpp:320-332 (ProcessObservers), between the
        // fixed-weapon rows and the turret rows, only when the count is > 0.
        // invobserver.mdl:1-12: white "Observers: N" in the key column, no icon,
        // no key.
        if (observerCount > 0)
            rows.Add(new HudInventoryRow(HudInventoryRowKind.Observers, "",
                "Observers: " + Round(observerCount), false, false, false, null, 0, 0, null, null));

        // Turrets — inventoryimage.cpp:248-252 (ProcessTurret). The gunner-name
        // row needs per-turret occupant data the port's wire does not carry.
        for (int mount = maxFixed; mount < maxWeapons; mount++)
            AddPart(rows, ctx, all, EquipmentType.Weapon, mount,
                isPilot && mount - maxFixed < TurretKeys.Length ? TurretKeys[mount - maxFixed] : "");

        // Equipment — inventoryimage.cpp:254-259.
        AddPart(rows, ctx, all, EquipmentType.Magazine, 0, "5.");
        AddPart(rows, ctx, all, EquipmentType.ChaffLauncher, 0, "6.");
        AddPart(rows, ctx, all, EquipmentType.Shield, 0, "7.");
        AddPart(rows, ctx, all, EquipmentType.Cloak, 0, "8.");
        AddPart(rows, ctx, all, EquipmentType.Afterburner, 0, "9.");
        AddPart(rows, ctx, all, EquipmentType.Dispenser, 0, "0.");

        // Cargo — inventoryimage.cpp:261-268; ShowCargoSlot dedup :169-195.
        AddCargoRows(rows, ctx, hold, selectedCargoMount);

        return new HudInventoryPane(ownShipName, rows);
    }

    private static void AddPart(
        List<HudInventoryRow> rows, in Ctx ctx, IReadOnlyList<ExpandedPartData> all,
        EquipmentType et, int mount, string key)
    {
        // Native ProcessPart gate (inventoryimage.cpp:106-114): cargo mounts
        // (mount < 0) always pass; a fixed slot must exist (mask != 0).
        if (mount >= 0 && SlotMask(ctx.Hull, et, mount) == 0) return;

        var entry = MountedAt(ctx, all, et, mount);
        if (entry is { } mountedPart)
        {
            rows.Add(ResolvePartRow(ctx, mountedPart, key, ctx.ArmedFraction));
        }
        else if (ShipHasPartForMount(ctx, all, et, mount))
        {
            rows.Add(EmptyRow(et, key));
        }
    }

    private static HudInventoryRow EmptyRow(EquipmentType et, string key) => et switch
    {
        EquipmentType.Weapon => new(HudInventoryRowKind.EmptyWeapon, key, "<small weapon mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Magazine => new(HudInventoryRowKind.EmptyEquipment, key, "<missile mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.ChaffLauncher => new(HudInventoryRowKind.EmptyEquipment, key, "<chaff mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Dispenser => new(HudInventoryRowKind.EmptyEquipment, key, "<mine mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Shield => new(HudInventoryRowKind.EmptyEquipment, key, "<shield mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Cloak => new(HudInventoryRowKind.EmptyEquipment, key, "<cloak mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Afterburner => new(HudInventoryRowKind.EmptyEquipment, key, "<afterburner mount>", false, false, false, null, 0, 0, null, null),
        EquipmentType.Pack => new(HudInventoryRowKind.EmptyEquipment, key, "<empty>", false, false, false, null, 0, 0, null, null),
        _ => new(HudInventoryRowKind.EmptyEquipment, key, "<empty>", false, false, false, null, 0, 0, null, null),
    };

    private static void AddCargoRows(
        List<HudInventoryRow> rows, in Ctx ctx, IReadOnlyList<ExpandedPartData> cargo,
        int selectedCargoMount)
    {
        // Native: no cargo rows in a lifepod (inventoryimage.cpp:173-175).
        if (ctx.Hull.HasCapability(DataHullTypeIGC.HabmLifepod)) return;

        AddCargoRow(rows, ctx, cargo, selectedCargoMount, "S.");
        for (int offset = 1; offset < 5; offset++)
        {
            int mount = selectedCargoMount - offset;
            AddCargoRow(rows, ctx, cargo, mount, "");
        }
    }

    private static void AddCargoRow(
        List<HudInventoryRow> rows, in Ctx ctx, IReadOnlyList<ExpandedPartData> cargo,
        int mount, string key)
    {
        if (!ShowCargoSlot(cargo, mount)) return;
        var entry = CargoEntryAt(cargo, mount);
        if (entry is not { } e)
        {
            // Empty cargo slot (native invemptycargo.mdl: "<cargo mount>").
            rows.Add(new(HudInventoryRowKind.EmptyCargo, key, "<cargo mount>", false, false, false, null, 0, 0, null, null));
            return;
        }
        var part = Resolve(ctx.Catalog, e.PartId);
        if (part is not { } p)
        {
            rows.Add(new(HudInventoryRowKind.EmptyCargo, key, "<cargo mount>", false, false, false, null, 0, 0, null, null));
            return;
        }
        float count = CargoCount(cargo, p, ctx.Hull);
        rows.Add(new(HudInventoryRowKind.Cargo, key, p.Name, false, false, false, Round(count), 0, 0, null, count));
    }

    /// <summary>Native ShowCargoSlot (inventoryimage.cpp:169-195): duplicate
    /// pack / magazine / dispenser instances collapse to the highest-mount
    /// instance; the selected mount is shown first.</summary>
    private static bool ShowCargoSlot(IReadOnlyList<ExpandedPartData> cargo, int mount)
    {
        var entry = CargoEntryAt(cargo, mount);
        if (entry is not { } e) return true;   // an empty slot still shows
        for (int m = mount + 1; m < 0; m++)
        {
            var prev = CargoEntryAt(cargo, m);
            if (prev is { } pe && pe.PartId == e.PartId) return false;
        }
        return true;
    }

    private static float CargoCount(IReadOnlyList<ExpandedPartData> cargo, DataPartTypeIGC part, DataHullTypeIGC hull)
    {
        // Native PartWrapper::GetCount (consoledata.cpp:768-800): same-type cargo
        // amounts aggregate; packs normalize to the hull's max ammo/fuel.
        float sum = 0f;
        foreach (var e in cargo)
        {
            if (!CargoSelector.IsCargoMount(e.MountId)) continue;
            if (e.PartId != part.PartId) continue;
            sum += e.Amount;
        }
        if (part.EquipmentType == EquipmentType.Pack && part.Type is DataPartTypeIGC.Pack pack)
        {
            // c_packAmmo = 0, c_packFuel = 1 (Igc/igc.h:549-550).
            sum /= pack.PackType == 0 ? Math.Max(1, (int)hull.MaxAmmo) : Math.Max(1f, hull.MaxFuel);
        }
        return sum;
    }

    private static HudInventoryRow ResolvePartRow(in Ctx ctx, ExpandedPartData entry, string key,
        Func<EquipmentType, float, float>? armedFraction)
    {
        var part = Resolve(ctx.Catalog, entry.PartId);
        float mountedFraction = entry.FractionMounted / (float)ExpandedPartData.FullyMounted;
        if (part is not { } p)
        {
            return new(HudInventoryRowKind.Cargo, key, $"#{entry.PartId}", false, false, false, null,
                mountedFraction < 1f ? 1 : 3, mountedFraction, null, entry.Amount);
        }

        var et = p.EquipmentType;
        int readyState = ReadyState(entry, p, mountedFraction, et, armedFraction, out float? armed);
        bool outOfAmmo = OutOfAmmo(entry, et, ctx.AmmoFraction, ctx.FuelFraction);

        bool selected = false, active = false;
        if (et == EquipmentType.Weapon)
        {
            selected = WeaponSelected(entry, ctx.Hull, ctx.GroupFire, ctx.SelectedMount);
            active = selected;
        }

        string? stats = readyState == 3 ? StatsText(ctx, p, entry) : null;
        var kind = KindFor(et, entry);
        return new HudInventoryRow(kind, key, p.Name, selected, active, outOfAmmo, stats, readyState,
            mountedFraction, armed, QuantityFor(entry, et, readyState));
    }

    private static bool OutOfAmmo(ExpandedPartData entry, EquipmentType et, float ammoFraction, float fuelFraction)
    {
        // Native PartWrapper::IsOutOfAmmo (consoledata.cpp:1132-1155).
        return et switch
        {
            EquipmentType.Weapon => ammoFraction <= 0f,
            EquipmentType.Afterburner => fuelFraction <= 0f,
            EquipmentType.Magazine or EquipmentType.ChaffLauncher or EquipmentType.Dispenser => entry.Amount <= 0,
            _ => false,
        };
    }

    private static bool WeaponSelected(ExpandedPartData entry, DataHullTypeIGC hull, bool groupFire, int selectedMount)
    {
        // Native PartWrapper::IsSelected (consoledata.cpp:1080-1119): fixed mounts
        // only; group fire or the selected mount. Turret weapons are never
        // "selected" by the pilot (gunners own that), so they stay false.
        int mount = entry.MountId;
        if (mount < 0 || mount >= hull.MaxFixedWeapons) return false;
        return groupFire || mount == selectedMount;
    }

    private static int ReadyState(ExpandedPartData entry, DataPartTypeIGC part, float mountedFraction,
        EquipmentType et, Func<EquipmentType, float, float>? armedFraction, out float? armed)
    {
        // Native PartWrapper::GetReadyState (consoledata.cpp:1157-1177):
        // 0 = not mounted / cargo, 1 = mounting, 2 = launcher arming, 3 = ready.
        // Case 2 needs the live GetArmedFraction (consoledata.cpp:1189-1202,
        // magazineigc.h:156-165 / dispenserigc.h:165-175) — P786 replays the
        // native client's own launcher clocks from the wire events (see
        // OwnLauncherArming); without a clock the fraction reads 1 and a fully
        // mounted launcher resolves straight to ready, as before.
        armed = null;
        int mount = entry.MountId;
        if (mount < 0) return 0;
        if (mountedFraction < 1f) return 1;
        // IlauncherIGC::IsLauncher = OT_magazine | OT_dispenser (igc.h:3993-3999)
        // — chaff launchers are NOT launchers and never arm.
        if (armedFraction is { } af && et is EquipmentType.Magazine or EquipmentType.Dispenser)
        {
            float fraction = af(et, mountedFraction);
            if (fraction < 1f)
            {
                armed = fraction;
                return 2;
            }
        }
        return 3;
    }

    private static string? StatsText(in Ctx ctx, DataPartTypeIGC part, ExpandedPartData entry)
    {
        switch (part.Type)
        {
            case DataPartTypeIGC.Weapon weapon:
            {
                if (ctx.Catalog?.ProjectileTypes is not { } projs
                    || !projs.TryGetValue(weapon.ProjectileTypeId, out var projectile)) return null;
                // Native PartWrapper::GetRange (consoledata.cpp:690-710).
                float range = projectile.Speed * projectile.Lifespan;
                range *= Ga(ctx, weapon.CAmmoPerShot != 0 ? GlobalAttributeSet.SpeedAmmo : GlobalAttributeSet.LifespanEnergy);
                // Native PartWrapper::GetDamage (consoledata.cpp:722-737).
                float damage = (projectile.Power + projectile.BlastPower) * Ga(ctx, GlobalAttributeSet.DamageGuns);
                // Native PartWrapper::GetRate (consoledata.cpp:748-762).
                float rate = weapon.DtimeBurst > 0f ? 1f / weapon.DtimeBurst : 0f;
                return $"{Round(range)}m / {Round(damage)} X {Round(rate)}";
            }
            case DataPartTypeIGC.MissileLauncher launcher:
            {
                if (ctx.Catalog?.MissileTypes is not { } mts
                    || !mts.TryGetValue(launcher.ExpendableTypeId, out var m)) return null;
                // Native PartWrapper::GetRange missile branch (consoledata.cpp:711-719).
                float range = m.Lifespan * (m.InitialSpeed + 0.5f * m.Lifespan * m.Acceleration);
                // Native PartWrapper::GetDamage missile branch (consoledata.cpp:722-737).
                float damage = (m.Power + m.BlastPower) * Ga(ctx, GlobalAttributeSet.DamageMissiles);
                return $"{Round(entry.Amount)} / {Round(range)}m / {Round(damage)}";
            }
            case DataPartTypeIGC.Shield shield:
            {
                // Native GetShieldStrength / GetRechargeTime (consoledata.cpp:911-961).
                float strength = ctx.ShieldFraction * shield.MaxStrength;
                float recharge = shield.RateRegen > 0f ? (shield.MaxStrength - strength) / shield.RateRegen : 0f;
                return $"{Round(strength)} / {Round(recharge)} sec";
            }
            case DataPartTypeIGC.Afterburner ab:
            {
                // Native GetAfterburnerTopSpeed / GetAfterburnerTimeLeft
                // (consoledata.cpp:848-889). Top speed reads the source-ship
                // hull (GetSourceShip()->GetHullType(), consoledata.cpp:855)
                // through the GA-scaled GetMaxSpeed()/GetThrust()
                // (shipIGC.cpp:4529,4544-4546); endurance keeps the local
                // ship's fuel (GetShip()->GetFuel(), consoledata.cpp:876).
                float maxSpeed = ctx.SourceHull.Speed * Ga(ctx, GlobalAttributeSet.MaxSpeed);
                float thrust = ctx.SourceHull.Thrust * Ga(ctx, GlobalAttributeSet.Thrust);
                float maxAbThrust = ab.MaxThrust * Ga(ctx, GlobalAttributeSet.Thrust);
                float topSpeed = thrust > 0f ? maxSpeed * (1f + maxAbThrust / thrust) : maxSpeed;
                float endurance = ab.FuelConsumption <= 0f ? -1f
                    : (ctx.FuelFraction * ctx.Hull.MaxFuel) / (ab.FuelConsumption * ab.MaxThrust);
                return $"{Round(topSpeed)}mps / {Endurance(endurance)}";
            }
            case DataPartTypeIGC.Cloak cloak:
            {
                // Native GetCloakTimeLeft (consoledata.cpp:967-985): the source
                // ship's recharge rate (GetSourceShip()->GetHullType()
                // ->GetRechargeRate(), consoledata.cpp:976; shipIGC.cpp:4565-4568)
                // drains the LOCAL ship's energy. HUD-20 adds the leading live
                // percentage native prints: ModelData::GetCloaking() =
                // 100·(1−GetCloaking()) for the own or parent ship only
                // (consoledata.cpp:480-490), streamed owner-only by
                // OwnShipCloakUpdate(220). invcloak.mdl:10-23 is
                // `NumberString(GetCloaking(Me)) + "% / " + CloakTimeLeft + " sec"`.
                // Without a received cloak update the percentage is unknown, so
                // keep today's endurance-only text rather than invent a 0.
                float consumption = cloak.EnergyConsumption;
                float regen = ctx.SourceHull.RechargeRate;
                float endurance = consumption <= regen ? -1f
                    : (ctx.EnergyFraction * ctx.Hull.MaxEnergy) / (consumption - regen);
                string enduranceText = $"{Endurance(endurance)} sec";
                return ctx.CloakStrengthPercent is { } strength
                    ? HudFormat.FormatNativeNumber(strength) + "% / " + enduranceText
                    : enduranceText;
            }
            case DataPartTypeIGC.ChaffLauncher:
            case DataPartTypeIGC.MineLauncher:
            case DataPartTypeIGC.ProbeLauncher:
                return Round(entry.Amount);
            default:
                return null;
        }
    }

    private static float Ga(in Ctx ctx, int index) => ctx.Attributes.Get(index);

    /// <summary>Native <c>NumberString</c> rounding: <c>(int)(value + 0.5)</c>
    /// (engine/modeler.cpp:1996-2009).</summary>
    private static string Round(float value) => ((int)(value + 0.5f)).ToString();

    private static string Endurance(float seconds) => seconds < 0f ? "Inf." : Round(seconds);

    private static HudInventoryRowKind KindFor(EquipmentType et, ExpandedPartData entry)
    {
        if (entry.MountId >= 0x80) return HudInventoryRowKind.Cargo;
        return et switch
        {
            EquipmentType.Weapon => HudInventoryRowKind.Weapon,
            EquipmentType.Magazine => HudInventoryRowKind.Magazine,
            EquipmentType.ChaffLauncher => HudInventoryRowKind.Chaff,
            EquipmentType.Shield => HudInventoryRowKind.Shield,
            EquipmentType.Cloak => HudInventoryRowKind.Cloak,
            EquipmentType.Afterburner => HudInventoryRowKind.Afterburner,
            EquipmentType.Dispenser => HudInventoryRowKind.Dispenser,
            _ => HudInventoryRowKind.Cargo,
        };
    }

    private static float? QuantityFor(ExpandedPartData entry, EquipmentType et, int readyState)
    {
        // The out-of-ammo red "A" is only meaningful for a weapon/afterburner/
        // launcher; the numeric quantity column applies to counts, not weapon
        // stats. Weapons carry no quantity numeral in the native row.
        if (et == EquipmentType.Weapon) return null;
        if (readyState is 1 or 2) return null;
        return entry.Amount;
    }

    private static ExpandedPartData? MountedAt(in Ctx ctx, IReadOnlyList<ExpandedPartData> all, EquipmentType et, int mount)
    {
        foreach (var e in all)
        {
            if (CargoSelector.IsCargoMount(e.MountId)) continue;
            if (e.MountId != mount) continue;
            var part = Resolve(ctx.Catalog, e.PartId);
            if (part is { } p && p.EquipmentType == et) return e;
        }
        return null;
    }

    private static bool ShipHasPartForMount(in Ctx ctx, IReadOnlyList<ExpandedPartData> all, EquipmentType et, int mount)
    {
        // Native ShipHasPartForMount (inventoryimage.cpp:84-101): any known part
        // of this equipment type that the hull can mount on this slot.
        foreach (var e in all)
        {
            var part = Resolve(ctx.Catalog, e.PartId);
            if (part is not { } p) continue;
            if (p.EquipmentType != et) continue;
            if (mount < 0 || ctx.Hull.CanMountPart(p, mount)) return true;
        }
        return false;
    }

    private static int SlotMask(DataHullTypeIGC hull, EquipmentType et, int mount)
    {
        if (et == EquipmentType.Weapon)
            return mount < hull.Hardpoints.Length ? hull.Hardpoints[mount].PartMask : 0;
        int idx = (int)et;
        return mount == 0 && idx < hull.PmEquipment.Length ? hull.PmEquipment[idx] : 0;
    }

    private static ExpandedPartData? CargoEntryAt(IReadOnlyList<ExpandedPartData> cargo, int mount)
    {
        foreach (var e in cargo)
        {
            if (!CargoSelector.IsCargoMount(e.MountId)) continue;
            if (CargoSelector.SignedMount(e.MountId) != mount) continue;
            return e;
        }
        return null;
    }

    private static DataPartTypeIGC? Resolve(ShopCatalog? catalog, short partId)
    {
        if (catalog is null) return null;
        foreach (var p in catalog.Parts) if (p.PartId == partId) return p;
        if (catalog.Launchers is { } launchers)
            foreach (var l in launchers) if (l.Part.PartId == partId) return l.Part;
        return null;
    }
}
