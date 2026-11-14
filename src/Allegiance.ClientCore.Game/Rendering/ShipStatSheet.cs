using System;
using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Which object a <see cref="ShipStat"/> describes.</summary>
public enum StatScope
{
    /// <summary>A hull-type value (native hull info pane).</summary>
    Hull,
    /// <summary>A whole-ship value that depends on the mounted list (mass).</summary>
    Ship,
    /// <summary>One mounted part / equipment slot.</summary>
    Slot,
}

/// <summary>
/// Which way a change reads for the pilot. Native has no such notion — the
/// loadout info panes only print the current and factory-spec values — so this
/// is a Modern presentation aid (the P1011 story's "better/worse" rule).
/// </summary>
public enum StatDirection
{
    HigherIsBetter,
    LowerIsBetter,
    /// <summary>Categorical / no defensible ordering.</summary>
    Neutral,
}

/// <summary>Row provenance: a value native prints, or an extra (estimate).</summary>
public enum StatGroup
{
    /// <summary>Native hull info pane row, labelled exactly as native.</summary>
    HullNative,
    /// <summary>Extra hull/ship value that native does not print (estimate).</summary>
    HullEstimate,
    /// <summary>Native part info pane row, labelled exactly as native.</summary>
    SlotNative,
    /// <summary>Extra part value that native does not print (estimate).</summary>
    SlotEstimate,
}

/// <summary>
/// The stable identity of one stat row, used to pair two sheets in
/// <see cref="ShipStatDelta.Between"/>. A slot row is keyed by its equipment
/// type + mount (never by part id), so replacing the part in a slot yields
/// OLD and NEW values under the same key — the per-offer delta the V2 screen
/// needs. Hull keys are <see cref="StatScope.Hull"/> with
/// <see cref="EquipmentType.ChaffLauncher"/> and mount 0 unused.
/// </summary>
public readonly record struct StatKey(StatScope Scope, EquipmentType SlotType, int Mount, string Field)
{
    public static StatKey Hull(string field) => new(StatScope.Hull, default, 0, field);

    public static StatKey Ship(string field) => new(StatScope.Ship, default, 0, field);

    public static StatKey Slot(EquipmentType type, int mount, string field)
        => new(StatScope.Slot, type, mount, field);
}

/// <summary>The <see cref="StatKey.Field"/> names (the only thing tests and views should hard-code).</summary>
public static class ShipStatField
{
    // Hull rows (native labels).
    public const string MaxSpeed = "maxSpeed";
    public const string Acceleration = "acceleration";
    public const string TurnRate = "turnRate";
    public const string HitPoints = "hitPoints";
    public const string Energy = "energy";
    public const string ScanRange = "scanRange";
    public const string Signature = "signature";
    public const string DefenseClass = "defenseClass";
    // Hull extras (estimates).
    public const string TurnRateYaw = "turnRateYaw";
    public const string TurnRatePitch = "turnRatePitch";
    public const string TurnRateRoll = "turnRateRoll";
    public const string EnergyRecharge = "energyRecharge";
    public const string Fuel = "fuel";
    public const string AmmoCapacity = "ammoCapacity";
    public const string CargoMagazine = "cargoMagazine";
    public const string CargoDispenser = "cargoDispenser";
    public const string CargoChaff = "cargoChaff";
    public const string Mass = "mass";

    // Slot rows (native labels per equipment type; names are cited in the builder).
    public const string DamagePerSecond = "damagePerSecond";
    public const string DamagePerShot = "damagePerShot";
    public const string ShotsPerSecond = "shotsPerSecond";
    public const string Range = "range";
    public const string ProjectileSpeed = "projectileSpeed";
    public const string DamageClass = "damageClass";
    public const string ShieldStrength = "shieldStrength";
    public const string ShieldRecharge = "shieldRecharge";
    public const string DefenseType = "defenseType";
    public const string MissileDamage = "missileDamage";
    public const string LockTime = "lockTime";
    public const string MaxLock = "maxLock";
    public const string ProbeHitPoints = "probeHitPoints";
    public const string Lifespan = "lifespan";
    public const string Visibility = "visibility";
    public const string MineDamage = "mineDamage";
    public const string MineRadius = "mineRadius";
    public const string ChaffStrength = "chaffStrength";
    public const string Cloaking = "cloaking";
    public const string CloakDuration = "cloakDuration";
    public const string AfterburnerTopSpeed = "afterburnerTopSpeed";
    public const string AfterburnerDuration = "afterburnerDuration";
}

/// <summary>
/// One row of a <see cref="ShipStatSheet"/>: a numeric value or a categorical
/// text value (native's damage/defense classes), plus what it means for a
/// delta.
/// </summary>
public readonly record struct ShipStat(
    StatKey Key,
    string Label,
    double? Value,
    string? Text,
    string Unit,
    StatDirection Direction,
    StatGroup Group)
{
    /// <summary>True when native does not print this row (Modern-only extra).</summary>
    public bool IsEstimate => Group is StatGroup.HullEstimate or StatGroup.SlotEstimate;
}

/// <summary>One mounted part and its mount (weapon hardpoint index; 0 otherwise).</summary>
public readonly record struct MountedStatPart(DataPartTypeIGC Part, int Mount = 0);

/// <summary>
/// One gun's output — the native weapon info pane's numbers plus the damage
/// type it prints ("damage class"). <see cref="DamagePerShot"/> is the raw
/// projectile power + blast power; <see cref="DamagePerSecond"/> applies native's
/// <c>rate × ga[c_gaDamageGuns]</c> exactly (load.cpp:1072-1073).
/// </summary>
public readonly record struct GunOutput(
    int Mount,
    short PartId,
    string Name,
    byte DamageType,
    string DamageClass,
    double DamagePerShot,
    double ShotsPerSecond,
    double DamagePerSecond,
    double Range,
    double Speed,
    bool UsesAmmo);

/// <summary>
/// The core type tables a stat sheet needs for ordnance-derived numbers
/// (projectile/missile/mine/probe/chaff). A thin abstraction so the sheet stays
/// pure and a unit test can feed a fixture core; <see cref="StaticCoreShipStatLookup"/>
/// is the production adapter over the loaded <see cref="StaticCore"/>.
/// </summary>
public interface IShipStatLookup
{
    bool TryGetProjectile(short id, out DataProjectileTypeIGC value);
    bool TryGetMissile(short id, out DataMissileTypeIGC value);
    bool TryGetMine(short id, out DataMineTypeIGC value);
    bool TryGetProbe(short id, out DataProbeTypeIGC value);
    bool TryGetChaff(short id, out DataChaffTypeIGC value);
}

/// <summary>Production adapter: the parsed <c>PCore001.igc</c> tables (StaticCore).</summary>
public sealed class StaticCoreShipStatLookup(StaticCore core) : IShipStatLookup
{
    private readonly StaticCore _core = core ?? throw new ArgumentNullException(nameof(core));

    public bool TryGetProjectile(short id, out DataProjectileTypeIGC value) => _core.ProjectileTypes.TryGetValue(id, out value);
    public bool TryGetMissile(short id, out DataMissileTypeIGC value) => _core.MissileTypes.TryGetValue(id, out value);
    public bool TryGetMine(short id, out DataMineTypeIGC value) => _core.MineTypes.TryGetValue(id, out value);
    public bool TryGetProbe(short id, out DataProbeTypeIGC value) => _core.ProbeTypes.TryGetValue(id, out value);
    public bool TryGetChaff(short id, out DataChaffTypeIGC value) => _core.ChaffTypes.TryGetValue(id, out value);
}

/// <summary>
/// A lookup that resolves nothing — used when a caller has no core (or a test
/// wants hull-only rows): ordnance rows then carry no damage/range numbers but
/// keep their native labels and the values that do not need a type table.
/// </summary>
public sealed class EmptyShipStatLookup : IShipStatLookup
{
    public static EmptyShipStatLookup Instance { get; } = new();

    public bool TryGetProjectile(short id, out DataProjectileTypeIGC value) { value = default; return false; }
    public bool TryGetMissile(short id, out DataMissileTypeIGC value) { value = default; return false; }
    public bool TryGetMine(short id, out DataMineTypeIGC value) { value = default; return false; }
    public bool TryGetProbe(short id, out DataProbeTypeIGC value) { value = default; return false; }
    public bool TryGetChaff(short id, out DataChaffTypeIGC value) { value = default; return false; }
}

/// <summary>
/// P1011 — the pure ship stat sheet: a hull type + the mounted part types + the
/// side's <see cref="GlobalAttributeSet"/> in, the ship's stats out, with no
/// <c>GraphicsDevice</c> and no (P1010) view-model dependency. This is the data
/// half of the Loadout V2 stats bar and of the per-offer hover delta; the
/// renderer (P1014) only draws it.
///
/// <para><b>Source of truth.</b> Native's loadout screen has exactly two stat
/// panes, both in <c>WinTrek/load.cpp</c>: the hull "performance info"
/// (<c>UpdatePerformanceInfo</c>, <c>:986-1054</c>) and the part info pane
/// (<c>UpdatePartInfo</c>, <c>:1250-1305</c> plus the per-type
/// <c>Update*PartInfo</c> bodies). Every row whose <see cref="StatGroup"/> is
/// native (not <see cref="ShipStat.IsEstimate"/>) uses that pane's label
/// verbatim — the labels themselves are painted in the artwork backgrounds
/// decoded from <c>hullinfobmp.mdl</c>/<c>partInfo*bmp.mdl</c>, and the
/// per-type class strings are in <c>partinfo.mdl</c>/<c>hullinfo.mdl</c>.</para>
///
/// <para><b>Global attributes.</b> The side GA is applied exactly as the sim's
/// <c>Ship.Effective*</c> helpers do — they are the port of native's
/// <c>MyHullType</c> (<c>Igc/shipIGC.cpp:4529-4580</c>), the same object the
/// native loadout screen reads through <c>m_phullType->Get*()</c>:
/// <c>speed×GA[MaxSpeed]</c>, <c>thrust×GA[Thrust]</c>,
/// <c>turnRate×GA[TurnRate]</c>, <c>scannerRange×GA[ScanRange]</c>,
/// <c>maxEnergy×GA[MaxEnergy]</c>, <c>hitPoints×GA[MaxArmorShip]</c>;
/// <c>rechargeRate</c>, <c>maxFuel</c>, <c>maxAmmo</c> and the capacities carry
/// no GA factor. Display signature is divided by <c>GA[Signature]</c>
/// (<c>load.cpp:1014</c>, <c>:1271</c>).</para>
/// </summary>
public sealed class ShipStatSheet
{
    /// <summary>Native <c>pi</c> (<c>Igc/igc.h</c>) — the deg/s conversion in <c>load.cpp:991-1006</c>.</summary>
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private ShipStatSheet(
        DataHullTypeIGC hull,
        GlobalAttributeSet attributes,
        IReadOnlyList<MountedStatPart> parts,
        IShipStatLookup lookup)
    {
        Hull = hull;
        Attributes = attributes;
        Parts = parts;
        Lookup = lookup;
    }

    public DataHullTypeIGC Hull { get; }
    public GlobalAttributeSet Attributes { get; }
    public IReadOnlyList<MountedStatPart> Parts { get; }
    public IShipStatLookup Lookup { get; }

    /// <summary>Every row, in native layout order (hull, ship, then slots).</summary>
    public IReadOnlyList<ShipStat> Stats { get; private init; } = Array.Empty<ShipStat>();

    /// <summary>The guns, in mount order (the native per-weapon info values).</summary>
    public IReadOnlyList<GunOutput> Guns { get; private init; } = Array.Empty<GunOutput>();

    /// <summary>Ship mass = hull mass + every mounted part's mass (native <c>AddPart</c>, <c>shipIGC.cpp:1468</c>).</summary>
    public double Mass { get; private init; }

    /// <summary>
    /// Display signature in percent: <c>(hull.signature + Σ part.signature) / GA[Signature] × 100</c>
    /// (<c>load.cpp:1014-1015,1271-1275</c>; the sum is native
    /// <c>SetSignature(hull)</c> + each part's <c>ChangeSignature</c>,
    /// <c>shipIGC.cpp:1440</c> + <c>weaponIGC.h:109</c>/<c>shieldIGC.h:72</c>/&amp;c).
    /// </summary>
    public double SignaturePercent { get; private init; }

    /// <summary>Native hull-pane MAX SPEED (<c>hull.speed × GA[MaxSpeed]</c>).</summary>
    public double MaxSpeed { get; private init; }

    /// <summary>Native hull-pane ACCELERATION (<c>thrust × GA[Thrust] / mass</c>, <c>load.cpp:990</c>).</summary>
    public double Acceleration { get; private init; }

    /// <summary>Native hull-pane TURN RATE: the mean of the three axes in deg/s (<c>load.cpp:991-1006</c>).</summary>
    public double TurnRateDegrees { get; private init; }

    /// <summary>Native hull-pane HIT POINTS (<c>hull.hitPoints × GA[MaxArmorShip]</c>).</summary>
    public double HullHitPoints { get; private init; }

    /// <summary>Native hull-pane ENERGY (<c>hull.maxEnergy × GA[MaxEnergy]</c>).</summary>
    public double Energy { get; private init; }

    /// <summary>Native hull-pane SCAN RANGE (<c>hull.scannerRange × GA[ScanRange]</c>).</summary>
    public double ScanRange { get; private init; }

    /// <summary>Build a sheet. <paramref name="attributes"/> defaults to the multiplicative identity.</summary>
    public static ShipStatSheet Build(
        DataHullTypeIGC hull,
        IReadOnlyList<MountedStatPart>? parts = null,
        GlobalAttributeSet? attributes = null,
        IShipStatLookup? lookup = null)
    {
        var ga = attributes ?? GlobalAttributeSet.Identity;
        var mounted = parts ?? Array.Empty<MountedStatPart>();
        var r = lookup ?? EmptyShipStatLookup.Instance;

        var ordered = OrderParts(mounted);
        var sheet = new ShipStatSheet(hull, ga, ordered, r);
        return sheet.Compute();
    }

    /// <summary>
    /// A new sheet with the part at <paramref name="slotType"/>/
    /// <paramref name="mount"/> replaced (or removed when
    /// <paramref name="replacement"/> is null) — the per-offer hover input, and
    /// the same call the hull swap uses. Other parts keep their order.
    /// </summary>
    public ShipStatSheet WithPart(EquipmentType slotType, int mount, DataPartTypeIGC? replacement)
    {
        var list = new List<MountedStatPart>(Parts.Count + 1);
        bool placed = false;
        foreach (var p in Parts)
        {
            if (SlotOf(p) == (slotType, mount))
            {
                if (!placed && replacement is { } rep)
                {
                    list.Add(new MountedStatPart(rep, mount));
                    placed = true;
                }
                continue;
            }
            list.Add(p);
        }
        if (!placed && replacement is { } add)
            list.Add(new MountedStatPart(add, mount));
        return Build(Hull, list, Attributes, Lookup);
    }

    /// <summary>
    /// A new sheet for a different hull, mechanically rebuilding the same part
    /// list against it. Which parts still fit the new hull is the caller's (P1012)
    /// decision — native's hull switch drops the ones that do not
    /// (<c>ReplaceLoadout</c> mounts only what passes <c>CanMount</c>,
    /// <c>clintlib.h:1806-1848</c>).
    /// </summary>
    public ShipStatSheet WithHull(DataHullTypeIGC hull) => Build(hull, Parts, Attributes, Lookup);

    /// <summary>The row for <paramref name="key"/>, or null.</summary>
    public ShipStat? Find(StatKey key)
    {
        foreach (var s in Stats)
            if (s.Key == key) return s;
        return null;
    }

    /// <summary>A required row (throws when the key is absent — a test guard).</summary>
    public ShipStat Stat(StatKey key) => Find(key)
        ?? throw new InvalidOperationException($"stat '{key}' is not on this sheet");

    /// <summary>The row for a hull-scope field (see <see cref="ShipStatField"/>).</summary>
    public ShipStat Stat(string hullField) => Stat(StatKey.Hull(hullField));

    private static (EquipmentType Type, int Mount) SlotOf(in MountedStatPart p)
        => (p.Part.EquipmentType, p.Part.EquipmentType == EquipmentType.Weapon ? p.Mount : 0);

    /// <summary>
    /// Native slot layout order (load.cpp:1976-2078 walks the ship's part list and
    /// finds the slot by equipment type/mount): weapons by hardpoint, then the
    /// single-socket types in the hull screen's own order
    /// (magazine, dispenser, chaff, shield, cloak, afterburner, pack).
    /// </summary>
    private static List<MountedStatPart> OrderParts(IReadOnlyList<MountedStatPart> parts)
    {
        var list = new List<MountedStatPart>(parts.Count);
        for (int mount = 0; mount < 8; mount++)
            foreach (var p in parts)
                if (p.Part.EquipmentType == EquipmentType.Weapon && p.Mount == mount)
                    list.Add(p);
        foreach (var et in new[]
                 {
                     EquipmentType.Magazine, EquipmentType.Dispenser, EquipmentType.ChaffLauncher,
                     EquipmentType.Shield, EquipmentType.Cloak, EquipmentType.Afterburner, EquipmentType.Pack,
                 })
        {
            bool taken = false;    // native: one mount (0) per non-weapon type, first wins
            foreach (var p in parts)
            {
                if (p.Part.EquipmentType != et) continue;
                if (taken) continue;
                list.Add(new MountedStatPart(p.Part, 0));
                taken = true;
            }
        }
        return list;
    }

    private ShipStatSheet Compute()
    {
        var ga = Attributes;
        float g(int index) => ga.Get(index);

        double mass = Hull.Mass;
        double partSignature = 0;
        foreach (var p in Parts)
        {
            mass += p.Part.Mass;
            partSignature += p.Part.Signature;
        }

        double thrust = Hull.Thrust * g(GlobalAttributeSet.Thrust);
        double acceleration = mass > 0 ? thrust / mass : double.NaN;
        double yaw = Hull.MaxTurnRates[0] * g(GlobalAttributeSet.TurnRate);
        double pitch = Hull.MaxTurnRates[1] * g(GlobalAttributeSet.TurnRate);
        double roll = Hull.MaxTurnRates[2] * g(GlobalAttributeSet.TurnRate);
        double turnRate = (yaw + pitch + roll) * RadiansToDegrees / 3.0;
        double signature = (Hull.Signature + partSignature) / g(GlobalAttributeSet.Signature) * 100.0;

        var stats = new List<ShipStat>(32);
        var guns = new List<GunOutput>();

        // ── hull pane (native UpdatePerformanceInfo, load.cpp:986-1054) ──────
        Add(stats, StatKey.Hull(ShipStatField.MaxSpeed), "MAX SPEED", Hull.Speed * g(GlobalAttributeSet.MaxSpeed),
            null, "m/s", StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.Acceleration), "ACCELERATION", acceleration,
            null, "m/s²", StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.TurnRate), "TURN RATE", turnRate,
            null, "°/s", StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.HitPoints), "HIT POINTS",
            Hull.HitPoints * g(GlobalAttributeSet.MaxArmorShip),
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.Energy), "ENERGY",
            Hull.MaxEnergy * g(GlobalAttributeSet.MaxEnergy),
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.ScanRange), "SCAN RANGE",
            Hull.ScannerRange * g(GlobalAttributeSet.ScanRange),
            null, "m", StatDirection.HigherIsBetter, StatGroup.HullNative);
        Add(stats, StatKey.Hull(ShipStatField.Signature), "SIGNATURE", signature,
            null, "%", StatDirection.LowerIsBetter, StatGroup.HullNative);
        // Native prints the hull's defense type in the pane's fourth right row
        // (hullinfo.mdl DefenseClassString; load.cpp:1053-1054).
        AddText(stats, StatKey.Hull(ShipStatField.DefenseClass), "DEFENSE CLASS",
            HullDefenseClass(Hull.DefenseType), StatDirection.Neutral, StatGroup.HullNative);

        // ── hull/ship extras native does not print (Modern, estimates) ───────
        Add(stats, StatKey.Hull(ShipStatField.TurnRateYaw), "TURN RATE YAW", yaw * RadiansToDegrees,
            null, "°/s", StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.TurnRatePitch), "TURN RATE PITCH", pitch * RadiansToDegrees,
            null, "°/s", StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.TurnRateRoll), "TURN RATE ROLL", roll * RadiansToDegrees,
            null, "°/s", StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        // Hull recharge rate carries NO GA factor (MyHullType::GetRechargeRate, shipIGC.cpp:4578-4581).
        Add(stats, StatKey.Hull(ShipStatField.EnergyRecharge), "ENERGY RECHARGE", Hull.RechargeRate,
            null, "/s", StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.Fuel), "FUEL", Hull.MaxFuel,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.AmmoCapacity), "AMMO CAPACITY", Hull.MaxAmmo,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        // Native IhullTypeIGC::GetCapacity (hullTypeIGC.h:178-196) — per-launcher magazines.
        Add(stats, StatKey.Hull(ShipStatField.CargoMagazine), "CARGO (MAGAZINE)", Hull.CapacityMagazine,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.CargoDispenser), "CARGO (DISPENSER)", Hull.CapacityDispenser,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        Add(stats, StatKey.Hull(ShipStatField.CargoChaff), "CARGO (CHAFF)", Hull.CapacityChaffLauncher,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.HullEstimate);
        // Mass is hull + parts (native AddPart/DeletePart, shipIGC.cpp:1468,1496).
        Add(stats, StatKey.Ship(ShipStatField.Mass), "MASS", mass,
            null, string.Empty, StatDirection.LowerIsBetter, StatGroup.HullEstimate);

        // ── mounted parts (native UpdatePartInfo + per-type bodies) ───────────
        foreach (var p in Parts)
        {
            var part = p.Part;
            var slotKey = part.EquipmentType;
            switch (part.Type)
            {
                case DataPartTypeIGC.Weapon w:
                    AddWeapon(stats, guns, slotKey, p.Mount, part, w);
                    break;
                case DataPartTypeIGC.Shield s:
                    AddShield(stats, slotKey, part, s);
                    break;
                case DataPartTypeIGC.MissileLauncher ml:
                    AddMissile(stats, slotKey, part, ml.ExpendableTypeId);
                    break;
                case DataPartTypeIGC.MineLauncher mn:
                    AddMine(stats, slotKey, part, mn.ExpendableTypeId);
                    break;
                case DataPartTypeIGC.ProbeLauncher pl:
                    AddProbe(stats, slotKey, part, pl.ExpendableTypeId);
                    break;
                case DataPartTypeIGC.ChaffLauncher cl:
                    AddChaff(stats, slotKey, cl.ExpendableTypeId);
                    break;
                case DataPartTypeIGC.Cloak c:
                    AddCloak(stats, slotKey, c);
                    break;
                case DataPartTypeIGC.Afterburner a:
                    AddAfterburner(stats, slotKey, part, a);
                    break;
                case DataPartTypeIGC.Pack:
                    // Native UpdatePackPartInfo is empty (load.cpp:1226-1228).
                    break;
            }
        }

        return new ShipStatSheet(Hull, Attributes, Parts, Lookup)
        {
            Stats = stats,
            Guns = guns,
            Mass = mass,
            SignaturePercent = signature,
            MaxSpeed = Hull.Speed * g(GlobalAttributeSet.MaxSpeed),
            Acceleration = acceleration,
            TurnRateDegrees = turnRate,
            HullHitPoints = Hull.HitPoints * g(GlobalAttributeSet.MaxArmorShip),
            Energy = Hull.MaxEnergy * g(GlobalAttributeSet.MaxEnergy),
            ScanRange = Hull.ScannerRange * g(GlobalAttributeSet.ScanRange),
        };
    }

    private void AddWeapon(List<ShipStat> stats, List<GunOutput> guns, EquipmentType et, int mount,
        in DataPartTypeIGC part, in DataPartTypeIGC.Weapon w)
    {
        var ga = Attributes;
        double rate = w.DtimeBurst > 0 ? 1.0 / w.DtimeBurst : double.NaN;
        bool usesAmmo = w.CAmmoPerShot != 0;
        double damagePerShot = double.NaN;
        double dps = double.NaN;
        double range = double.NaN;
        double speed = double.NaN;
        byte damageType = 0;
        string damageClass = "NA";

        if (Lookup.TryGetProjectile(w.ProjectileTypeId, out var proj))
        {
            damageType = proj.DamageType;
            damageClass = WeaponDamageClass(proj.DamageType);
            damagePerShot = proj.Power + proj.BlastPower;
            // load.cpp:1072-1073: (power + blastPower) × (1/dtimeBurst) × GA[DamageGuns].
            dps = damagePerShot * rate * ga.Get(GlobalAttributeSet.DamageGuns);
            // load.cpp:1075-1081: ammo rounds get GA[SpeedAmmo] on speed; energy
            // rounds get GA[LifespanEnergy] on range. Speed is printed GA-scaled.
            speed = usesAmmo ? proj.Speed * ga.Get(GlobalAttributeSet.SpeedAmmo) : proj.Speed;
            range = speed * proj.Lifespan;
            if (!usesAmmo) range *= ga.Get(GlobalAttributeSet.LifespanEnergy);
        }

        var key = StatKey.Slot(et, mount, string.Empty);
        Add(stats, key with { Field = ShipStatField.DamagePerSecond }, "DAMAGE/SEC", dps,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.ShotsPerSecond }, "SHOTS/SEC", rate,
            null, "/s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Signature }, "SIGNATURE", PartSignaturePercent(part),
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Range }, "RANGE", range,
            null, "m", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.ProjectileSpeed }, "SPEED", speed,
            null, "m/s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        AddText(stats, key with { Field = ShipStatField.DamageClass }, "DAMAGE CLASS", damageClass,
            StatDirection.Neutral, StatGroup.SlotNative);
        // Extra: the raw per-shot power the DPS is derived from (estimate).
        Add(stats, key with { Field = ShipStatField.DamagePerShot }, "DAMAGE PER SHOT", damagePerShot,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotEstimate);

        guns.Add(new GunOutput(mount, part.PartId, part.Name, damageType, damageClass,
            damagePerShot, rate, dps, range, speed, usesAmmo));
    }

    private void AddShield(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC part, in DataPartTypeIGC.Shield s)
    {
        var ga = Attributes;
        double strength = s.MaxStrength * ga.Get(GlobalAttributeSet.MaxShieldShip);
        double rate = s.RateRegen * ga.Get(GlobalAttributeSet.ShieldRegenerationShip);
        // load.cpp:1193-1194 — native labels this RECHARGE and prints the SECONDS
        // to refill the shield (maxStrength / effective regeneration).
        double recharge = rate > 0 ? strength / rate : double.NaN;
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.ShieldStrength }, "STRENGTH", strength,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.ShieldRecharge }, "RECHARGE", recharge,
            null, "s", StatDirection.LowerIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Signature }, "SIGNATURE", PartSignaturePercent(part),
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
        AddText(stats, key with { Field = ShipStatField.DefenseType }, "DEFENSE TYPE",
            ShieldDefenseType(s.DefenseType), StatDirection.Neutral, StatGroup.SlotNative);
    }

    private void AddMissile(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC part, short expendableId)
    {
        var ga = Attributes;
        double damage = double.NaN, range = double.NaN, lockTime = double.NaN, maxLock = double.NaN;
        string damageClass = "NA";
        if (Lookup.TryGetMissile(expendableId, out var mt))
        {
            // load.cpp:1103-1113.
            damage = (mt.Power + mt.BlastPower) * ga.Get(GlobalAttributeSet.DamageMissiles);
            double lifespan = mt.Lifespan;
            range = mt.InitialSpeed * lifespan + 0.5 * (mt.Acceleration * lifespan * lifespan);
            lockTime = mt.LockTime;
            maxLock = mt.MaxLock * 100.0;
            damageClass = MagazineDamageClass(mt.DamageType);
        }
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.MissileDamage }, "DAMAGE", damage,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Signature }, "SIGNATURE", PartSignaturePercent(part),
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Range }, "RANGE", range,
            null, "m", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.LockTime }, "LOCK TIME", lockTime,
            null, "s", StatDirection.LowerIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.MaxLock }, "MAX LOCK", maxLock,
            null, "%", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        AddText(stats, key with { Field = ShipStatField.DamageClass }, "DAMAGE CLASS", damageClass,
            StatDirection.Neutral, StatGroup.SlotNative);
    }

    private void AddMine(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC part, short expendableId)
    {
        var ga = Attributes;
        double damage = double.NaN, radius = double.NaN, lifespan = double.NaN, visibility = double.NaN;
        if (Lookup.TryGetMine(expendableId, out var mt))
        {
            // load.cpp:1159-1170: power / MineRadius (the derived tail field —
            // native load.cpp's pdmt->radius; see DataMineTypeIGC.MineRadius,
            // P832d) / lifespan / signature.
            damage = mt.Power;
            radius = mt.MineRadius;
            lifespan = mt.Lifespan;
            visibility = mt.Signature / ga.Get(GlobalAttributeSet.Signature) * 100.0;
        }
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.MineDamage }, "DAMAGE", damage,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.MineRadius }, "RADIUS", radius,
            null, "m", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Lifespan }, "LIFESPAN", lifespan,
            null, "s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Visibility }, "VISIBILITY", visibility,
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
    }

    private void AddProbe(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC part, short expendableId)
    {
        var ga = Attributes;
        double scanRange = double.NaN, hitPoints = double.NaN, lifespan = double.NaN, visibility = double.NaN;
        if (Lookup.TryGetProbe(expendableId, out var pt))
        {
            // load.cpp:1131-1157.
            if (pt.ProjectileTypeId >= 0 && Lookup.TryGetProjectile(pt.ProjectileTypeId, out var proj))
            {
                scanRange = proj.Lifespan * proj.Speed / 1.2f;
                if (pt.Ammo != 0) scanRange *= ga.Get(GlobalAttributeSet.SpeedAmmo);
            }
            else
            {
                scanRange = pt.ScannerRange * ga.Get(GlobalAttributeSet.ScanRange);
            }
            hitPoints = pt.HitPoints;
            lifespan = pt.Lifespan;
            visibility = pt.Signature / ga.Get(GlobalAttributeSet.Signature) * 100.0;
        }
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.ProbeHitPoints }, "HIT POINTS", hitPoints,
            null, string.Empty, StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Range }, "RANGE", scanRange,
            null, "m", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Lifespan }, "LIFESPAN", lifespan,
            null, "s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Visibility }, "VISIBILITY", visibility,
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
    }

    private void AddChaff(List<ShipStat> stats, EquipmentType et, short expendableId)
    {
        double strength = double.NaN;
        if (Lookup.TryGetChaff(expendableId, out var ct))
        {
            // load.cpp:1178-1184 — native's effectiveness curve, printed ×100.
            double s = ct.ChaffStrength;
            double effectiveness = s <= 1.0 ? s / 2.0 : 1.0 - 1.0 / (2.0 * s);
            strength = effectiveness * 100.0;
        }
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.ChaffStrength }, "STRENGTH", strength,
            null, "%", StatDirection.HigherIsBetter, StatGroup.SlotNative);
    }

    private void AddCloak(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC.Cloak c)
    {
        double recharge = Hull.RechargeRate;
        double maxEnergy = Hull.MaxEnergy * Attributes.Get(GlobalAttributeSet.MaxEnergy);
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.Cloaking }, "CLOAKING", c.MaxCloaking * 100.0,
            null, "%", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        // load.cpp:1215-1222: unlimited (-1, native prints "unlimited") when the
        // cloak's draw does not exceed the hull's recharge rate.
        if (c.EnergyConsumption <= recharge)
        {
            AddText(stats, key with { Field = ShipStatField.CloakDuration }, "DURATION",
                "unlimited", StatDirection.Neutral, StatGroup.SlotNative);
        }
        else
        {
            double duration = maxEnergy / (c.EnergyConsumption - recharge);
            Add(stats, key with { Field = ShipStatField.CloakDuration }, "DURATION", duration,
                null, "s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        }
    }

    private void AddAfterburner(List<ShipStat> stats, EquipmentType et, in DataPartTypeIGC part,
        in DataPartTypeIGC.Afterburner a)
    {
        var ga = Attributes;
        double hullSpeed = Hull.Speed * ga.Get(GlobalAttributeSet.MaxSpeed);
        double hullThrustNoGa = Hull.Thrust;                       // native: raw DataHullTypeIGC.thrust (load.cpp:1236)
        double topSpeed = double.NaN;
        if (hullThrustNoGa > 0)
            topSpeed = hullSpeed * (1.0 + a.MaxThrust / hullThrustNoGa); // load.cpp:1238
        double draw = a.MaxThrust * a.FuelConsumption;
        double duration = draw > 0 ? Hull.MaxFuel / draw : double.NaN;   // load.cpp:1241
        var key = StatKey.Slot(et, 0, string.Empty);
        Add(stats, key with { Field = ShipStatField.AfterburnerTopSpeed }, "TOP SPEED", topSpeed,
            null, "m/s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.AfterburnerDuration }, "DURATION", duration,
            null, "s", StatDirection.HigherIsBetter, StatGroup.SlotNative);
        Add(stats, key with { Field = ShipStatField.Signature }, "SIGNATURE", PartSignaturePercent(part),
            null, "%", StatDirection.LowerIsBetter, StatGroup.SlotNative);
    }

    /// <summary>Part signature in percent (native load.cpp:1271-1275).</summary>
    private double PartSignaturePercent(in DataPartTypeIGC part)
        => part.Signature / Attributes.Get(GlobalAttributeSet.Signature) * 100.0;

    private static void Add(List<ShipStat> stats, StatKey key, string label, double value,
        string? text, string unit, StatDirection direction, StatGroup group)
        => stats.Add(new ShipStat(key, label, double.IsNaN(value) ? null : value, text, unit, direction, group));

    private static void AddText(List<ShipStat> stats, StatKey key, string label, string text,
        StatDirection direction, StatGroup group)
        => stats.Add(new ShipStat(key, label, null, text, string.Empty, direction, group));

    /// <summary>
    /// Native hullinfo.mdl <c>DefenseClassString</c> (the value native prints in the
    /// hull pane's eighth row; load.cpp:1053-1054).
    /// </summary>
    public static string HullDefenseClass(byte defenseType) => defenseType switch
    {
        0 => "Asteroid",
        1 => "Light",
        2 => "Medium",
        3 => "Heavy",
        4 => "Super Heavy",
        5 => "Utility",
        6 => "Light Base",
        7 => "Heavy Base",
        8 => "Ship Shield",
        9 => "Base Shield",
        _ => "NA",
    };

    /// <summary>Native partinfo.mdl <c>shieldDefenseTypeString</c> (:365-370).</summary>
    public static string ShieldDefenseType(byte defenseType) => defenseType == 8 ? "normal" : "NA";

    /// <summary>Native partinfo.mdl <c>weaponDamageClassString</c> (:315-333) — indexed by the projectile's damage type.</summary>
    public static string WeaponDamageClass(byte damageType) => damageType switch
    {
        1 => "anti-utility",
        2 => "light",
        3 => "heavy",
        4 => "light-medium",
        5 => "very light",
        6 => "medium-heavy",
        7 => "medium-heavy",
        8 => "stations",
        9 => "asteroids",
        10 => "repair",
        11 => "very light",
        12 => "heavy shields",
        13 => "light shields",
        14 => "special",
        15 => "heavy",
        16 => "very light",
        17 => "stations",
        18 => "light & medium",
        _ => "NA",
    };

    /// <summary>Native partinfo.mdl <c>magazineDamageClassString</c> (:410-425).</summary>
    public static string MagazineDamageClass(byte damageType) => damageType switch
    {
        1 => "anti-utility",
        2 => "light",
        3 => "heavy",
        4 => "light-medium",
        5 => "very light",
        6 => "medium-heavy",
        8 => "stations",
        9 => "asteroids",
        _ => "NA",
    };
}
