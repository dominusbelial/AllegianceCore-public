using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P125 — pure-domain (no sockets) proofs of the station combat-destructibility
/// math: <see cref="Station.ReceiveDamage"/> implements the real
/// <c>CstationIGC::ReceiveDamage</c> shield-absorbs-then-overflows-to-armor
/// formula (<c>src/Igc/stationIGC.cpp:116-208</c>), and
/// <see cref="Station.Regenerate"/> implements <c>CstationIGC::Update</c>'s
/// regen block (<c>stationIGC.h:189-213</c>). Everything is data-derived from the
/// real <c>PCore001.igc</c> static core — capacities (<c>MaxArmorHitPoints</c> /
/// <c>MaxShieldHitPoints</c> / regen rates / defense indices, decoded in
/// <c>DataStationTypeIGC.cs:32-35,42-43</c>) and the damage matrix
/// (<c>DataConstantsIGC.DamageConstants</c>, the same table
/// <see cref="Ship.ReceiveDamage"/> reads). No hardcoded numbers.
/// </summary>
/// <remarks>
/// <para>The target station is synthetic (a real <see cref="DataStationTypeIGC"/>
/// from the core <c>with</c>-modified to chosen capacities/defense indices + a
/// crafted <see cref="DataStationIGC"/>), the same injection seam the asteroid
/// unit tests use — so the shield/armor overflow ratios can be asserted exactly
/// to 3 decimals. The defense indices are chosen from a real damage type whose
/// matrix cells for both are strictly positive, guaranteeing a real
/// <c>DamageConstant</c> lookup (never the degenerate neutral fallback).</para>
/// <para>Verified behaviours: (1) a shield-up hit reduces <see cref="Station.ShieldFraction"/>
/// by exactly <c>amount·dtmShield/maxShield</c> leaving armor untouched;
/// (2) an overflowing hit converts the surplus to hull damage by the exact native
/// ratio <c>(overflow·maxShield·dtmArmor)/(maxArmor·dtmShield)</c>
/// (<c>stationIGC.cpp:186</c>) and clamps the shield to 0; (3) a lethal hit marks
/// the station dead and returns <see cref="DamageResult.Killed"/>, leaving
/// removal to the caller (the combat pass) — the documented contract; (4) same-side
/// friendly fire (the simplified gate) returns <see cref="DamageResult.NoDamage"/>
/// with no state change; (5) a shield-less station type takes <b>no damage at all</b>
/// (native <c>stationIGC.cpp:203-204</c> — confirmed no hull fallback); (6) a
/// <c>c_sabmPedestal</c> station is indestructible; (7) regeneration follows the
/// armor-then-shield sequencing (<c>stationIGC.h:195-208</c>); (8) the repair
/// (negative-amount) path heals the hull fraction toward 1.0
/// (<c>stationIGC.cpp:147-156</c>), dead code in practice but faithfully ported.
/// </remarks>
public sealed class StationCombatTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A real damage type id from a real ordnance record whose armor and
    /// shield defense cells are BOTH strictly positive for the given indices, so
    /// a real (non-degenerate) matrix value drives the shield-absorb and overflow
    /// math.</summary>
    private static byte PositiveCellsDamageType(StaticCore core, byte defArmor, byte defShield)
    {
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            if (Dtm(core, pt.DamageType, defArmor) > 0f && Dtm(core, pt.DamageType, defShield) > 0f)
                return pt.DamageType;
        }
        throw new InvalidOperationException($"no projectile damage type with positive cells for armor {defArmor} and shield {defShield}");
    }

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9801, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    /// <summary>A synthetic station: a real station type from the core
    /// <c>with</c>-modified to chosen capacities/defense indices/sabm, wrapped
    /// with a crafted <see cref="DataStationIGC"/>. Placed in sector 0 via
    /// <see cref="Mission.PlaceStation"/> (the same synthetic injection seam the
    /// asteroid unit tests use). The order of <c>with</c> clauses is irrelevant;
    /// every mutated field is set explicitly.</summary>
    private static Station MkStation(
        StaticCore core, Sector sector, short id, short side,
        float maxArmor, float maxShield, byte defArmor, byte defShield,
        float armorRegen, float shieldRegen, short sabm)
    {
        var baseType = core.StationTypes.Values.First();
        var type = baseType with
        {
            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = maxShield,
            DefenseTypeArmor = defArmor,
            DefenseTypeShield = defShield,
            ArmorRegeneration = armorRegen,
            ShieldRegeneration = shieldRegen,
            SabmCapabilities = sabm,
        };
        var spot = new Vector3(0f, 0f, -500f);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
        return new Station(data, type);
    }

    // ══ domain: shield absorb → overflow → kill ═════════════════════════════

    /// <summary>⭐ A shield-up hit on a real mission reduces
    /// <see cref="Station.ShieldFraction"/> by exactly
    /// <c>amount·dtmShield/maxShield</c> and leaves the armor untouched
    /// (<c>stationIGC.cpp:171-177</c>) — verification point 1 of the task.</summary>
    [Fact]
    public void Shield_up_hit_reduces_shield_fraction_and_leaves_armor_untouched()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;   // two indices with positive cells for a real type
        float ms = 500f, ma = 2000f;
        var station = MkStation(core, sector, 8101, 0, ma, ms, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);
        Assert.Equal(1f, station.ShieldFraction, 3);
        Assert.Equal(1f, station.ArmorFraction, 3);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        Assert.True(ds > 0f);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // ENEMY side — cross-side hit deals damage
        mission.PlaceShip(launcher, sector);       // gives it Mission (the DamageConstant route)

        // amount chosen so absorbed = amount·ds/ms = 0.5 (< 1): shield absorbs, no overflow.
        float amount = 0.5f * ms / ds;
        var res = station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.ShieldDamage, res);
        Assert.Equal(0.5f, station.ShieldFraction, 3);
        Assert.Equal(1f, station.ArmorFraction, 3);   // hull untouched
        Assert.False(station.Dead);
        Console.WriteLine($"P125-DOMAIN-SHIELD: hit {amount:F2} @ dtmShield {ds:F3} → ShieldFraction 1.0→0.500, armor still 1.0");
    }

    /// <summary>⭐ An overflowing hit depletes the shield to 0 and converts the
    /// surplus to hull damage by the exact native ratio
    /// <c>(overflow·maxShield·dtmArmor)/(maxArmor·dtmShield)</c>
    /// (<c>stationIGC.cpp:186</c>), decreasing <see cref="Station.ArmorFraction"/>
    /// by exactly that — verification point 2 of the task.</summary>
    [Fact]
    public void Overflowing_hit_depletes_shield_and_converts_surplus_to_hull_by_exact_native_ratio()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        float ms = 500f, ma = 2000f;
        var station = MkStation(core, sector, 8201, 0, ma, ms, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        float da = Dtm(core, dtype, defArmor);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // enemy
        mission.PlaceShip(launcher, sector);

        // amount chosen so absorbed = amount·ds/ms = 1.5 (> 1): overflow 0.5.
        float amount = 1.5f * ms / ds;
        float overflow = 0.5f;
        float dmg = (overflow * ms * da) / (ma * ds);   // exact native ratio
        Assert.True(dmg > 0f && dmg < 1f, $"overflow must chip (not kill) the armor; dmg={dmg:F3}");

        var res = station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.HullDamage, res);
        Assert.Equal(0f, station.ShieldFraction, 3);      // shield bottoms out (stationIGC.cpp:200)
        Assert.Equal(1f - dmg, station.ArmorFraction, 3); // exact overflow ratio
        Assert.False(station.Dead);
        Console.WriteLine($"P125-DOMAIN-OVERFLOW: overflow {overflow:F2} × maxShield {ms:F0} × dtmArmor {da:F3} / (maxArmor {ma:F0} × dtmShield {ds:F3}) " +
                          $"→ armor 1.0→{1f - dmg:F3}; shield 1.0→0.0");
    }

    /// <summary>⭐ A lethal overflowing hit clamps <see cref="Station.ArmorFraction"/>
    /// to 0, returns <see cref="DamageResult.Killed"/> and marks the station
    /// <see cref="Station.Dead"/> — but does NOT remove it from the sector (that
    /// is the combat pass's job, since a station is not a Model). A second call
    /// past death does not double-kill (native <c>dr</c>-stays-default,
    /// <c>stationIGC.cpp:191-194</c>) — the same caller-owns-removal contract the
    /// asteroid established (P102).</summary>
    [Fact]
    public void Lethal_hit_marks_dead_returns_killed_and_leaves_removal_to_caller()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        float ms = 500f, ma = 2000f;
        var station = MkStation(core, sector, 8301, 0, ma, ms, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);
        Assert.Contains(station, sector.StationRuntimes);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        float da = Dtm(core, dtype, defArmor);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // enemy
        mission.PlaceShip(launcher, sector);

        // Amount guaranteeing absorbed ≫ 1 so overflow dmg ≥ 1 → armor reaches 0.
        float amount = 10f * ms / ds;              // absorbed = 10, overflow = 9
        var res = station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.Killed, res);
        Assert.Equal(0f, station.ArmorFraction, 3);
        Assert.True(station.Dead);
        // Removal is the combat pass's job: a direct ReceiveDamage alone leaves it
        // in the sector's runtime list.
        Assert.Contains(station, sector.StationRuntimes);

        // Post-death: no double-kill.
        var resAgain = station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.HullDamage, resAgain);
        Assert.Equal(0f, station.ArmorFraction, 3);
        Console.WriteLine("P125-DOMAIN-KILL: lethal overflow → armor 1.0→0.0, Killed, removal left to the combat pass, no double-kill");
    }

    // ══ domain: friendly fire / no-shield / pedestal gates ══════════════════

    /// <summary>⭐ SAME-SIDE friendly fire on a station returns
    /// <see cref="DamageResult.NoDamage"/> with no state change — the
    /// friendly-fire gate simplified to same-side-blocked (native combines it with
    /// <c>!bAllowFriendlyFire &amp;&amp; AlliedSides(...)</c>,
    /// <c>stationIGC.cpp:128-130</c>; no alliance concept here, the P112
    /// <c>bAllowAlliedRip</c> precedent). Verification point 5 of the task.</summary>
    [Fact]
    public void Same_side_friendly_fire_deals_no_damage()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        var station = MkStation(core, sector, 8401, 0, 2000f, 500f, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 0;                       // SAME side as the station
        mission.PlaceShip(launcher, sector);

        float amount = 10f * 500f / ds;            // would otherwise obliterate it
        var res = station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.NoDamage, res);
        Assert.Equal(1f, station.ArmorFraction, 3);
        Assert.Equal(1f, station.ShieldFraction, 3);
        Assert.False(station.Dead);
        Console.WriteLine("P125-DOMAIN-FF: same-side launcher → NoDamage, armor+shield untouched (friendly-fire gate)");
    }

    /// <summary>⭐ A shield-less station type (<c>MaxShieldHitPoints == 0</c>)
    /// takes <b>no damage at all</b> — the native falls to <c>c_drNoDamage</c> in
    /// that case (<c>stationIGC.cpp:161,203-204</c>), NOT a hull-damage fallback.
    /// Confirmed and asserted, so the deliberate simplification is visible.</summary>
    [Fact]
    public void Shield_less_station_takes_no_damage_not_a_hull_fallback()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        var station = MkStation(core, sector, 8501, 0, 2000f, maxShield: 0f, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // enemy — should still be unable to hurt a shield-less base
        mission.PlaceShip(launcher, sector);

        var res = station.ReceiveDamage(dtype, 10f * 500f / ds, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.NoDamage, res);
        Assert.Equal(1f, station.ArmorFraction, 3);   // hull UNTOUCHED — confirmed no fallback
        Assert.Equal(1f, station.ShieldFraction, 3);
        Assert.False(station.Dead);
        Console.WriteLine("P125-DOMAIN-NOSHIELD: MaxShieldHitPoints=0 → NoDamage (hull untouched, native stationIGC.cpp:203-204, no fallback)");
    }

    /// <summary>⭐ A <c>c_sabmPedestal</c> station is indestructible — the
    /// capability-bit invulnerability gate (<c>stationIGC.cpp:127</c>,
    /// <see cref="Station.SabmPedestal"/>) returns NoDamage for any enemy hit.</summary>
    [Fact]
    public void Flag_pedestal_station_is_indestructible()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        var station = MkStation(core, sector, 8601, 0, 2000f, 500f, defArmor, defShield, 0f, 5f, sabm: Station.SabmPedestal);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // enemy
        mission.PlaceShip(launcher, sector);

        var res = station.ReceiveDamage(dtype, 10f * 500f / ds, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.NoDamage, res);
        Assert.Equal(1f, station.ArmorFraction, 3);
        Assert.Equal(1f, station.ShieldFraction, 3);
        Assert.False(station.Dead);
        Console.WriteLine("P125-DOMAIN-PEDESTAL: c_sabmPedestal station → NoDamage (indestructible)");
    }

    // ══ domain: repair path ════════════════════════════════════════════════

    /// <summary>⭐ The negative-amount repair path heals the hull fraction toward
    /// 1.0 (<c>stationIGC.cpp:147-156</c>) — faithfully ported even though no
    /// repair source exists yet (dead code in practice, the same precedent as
    /// <see cref="Ship.ReceiveDamage"/>'s repair branch, Ship.cs:906-914).</summary>
    [Fact]
    public void Negative_amount_repairs_hull_fraction()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        float ms = 500f, ma = 2000f;
        var station = MkStation(core, sector, 8701, 0, ma, ms, defArmor, defShield, 0f, 5f, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        float da = Dtm(core, dtype, defArmor);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;                       // enemy — needed only so the matrix resolves
        mission.PlaceShip(launcher, sector);

        // Chip the armor first (overflow 1.5 → some hull damage), then heal.
        float amount = 1.5f * ms / ds;
        float dmg = (0.5f * ms * da) / (ma * ds);
        station.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        float damaged = 1f - dmg;
        Assert.True(damaged < 1f && damaged > 0f, $"armor must be chipped first; got {damaged:F3}");

        // Repair: negative amount heals by |amount|·dtmArmor/maxArmor toward 1.0.
        float heal = 0.4f * ma / da;
        var res = station.ReceiveDamage(dtype, -heal, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(DamageResult.NoDamage, res);
        Assert.Equal(Math.Min(1f, damaged + 0.4f), station.ArmorFraction, 3);
        Console.WriteLine($"P125-DOMAIN-REPAIR: negative amount healed armor {damaged:F3} → {station.ArmorFraction:F3} (toward 1.0)");
    }

    // ══ domain: regeneration ═══════════════════════════════════════════════

    /// <summary>⭐ Shield regeneration over real ticks: with the armor full, a
    /// damaged shield regrows toward 1.0 at exactly <c>dt·ShieldRegeneration/maxShield</c>
    /// per call (<c>stationIGC.h:201-208</c>) — verification point 4 of the task.</summary>
    [Fact]
    public void Shield_regenerates_toward_max_at_documented_rate()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        float ms = 500f, shieldRegen = 25f;
        var station = MkStation(core, sector, 8901, 0, 2000f, ms, defArmor, defShield, 0f, shieldRegen, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;
        mission.PlaceShip(launcher, sector);

        // Knock the shield to 0.5 (armor stays full): absorbed = 0.5.
        station.ReceiveDamage(dtype, 0.5f * ms / ds, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.Equal(0.5f, station.ShieldFraction, 3);
        Assert.Equal(1f, station.ArmorFraction, 3);

        // One regen step of dt = 2 s: shield gains 2·25/500 = 0.1.
        station.Regenerate(2f);
        Assert.Equal(0.6f, station.ShieldFraction, 3);
        station.Regenerate(2f);
        Assert.Equal(0.7f, station.ShieldFraction, 3);
        // Many steps cap at 1.0 (defensive clamp; native's asymptotic approach).
        for (int i = 0; i < 20 && station.ShieldFraction < 1f; i++) station.Regenerate(2f);
        Assert.Equal(1f, station.ShieldFraction, 3);
        Console.WriteLine($"P125-DOMAIN-REGEN: shield 0.5 → 0.6 → 0.7 (2×2s×{shieldRegen}/{ms}) → 1.0 at documented rate");
    }

    /// <summary>⭐ Native regen sequencing: while the armor is not full, ONLY the
    /// armor regenerates and the shield does NOT (<c>stationIGC.h:195-200</c>); the
    /// shield resumes only once the armor is back to full (<c>stationIGC.h:201-208</c>).</summary>
    [Fact]
    public void Armor_regenerates_first_and_shield_waits_until_armor_full()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        const byte defArmor = 8, defShield = 9;
        float ms = 500f, ma = 2000f, armorRegen = 100f, shieldRegen = 25f;
        var station = MkStation(core, sector, 8902, 0, ma, ms, defArmor, defShield, armorRegen, shieldRegen, 0);
        mission.PlaceStation(station, sector);

        byte dtype = PositiveCellsDamageType(core, defArmor, defShield);
        float ds = Dtm(core, dtype, defShield);
        float da = Dtm(core, dtype, defArmor);
        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 1;
        mission.PlaceShip(launcher, sector);

        // Big overflow: armor chipped to ~0.7, shield 0. Overflow = absorbed−1; choose absorbed 1.5 → dmg=0.3·? compute exactly.
        // dmg = (0.5·ms·da)/(ma·ds); we just need armor not-full and shield 0.
        station.ReceiveDamage(dtype, 1.5f * ms / ds, mission.LastUpdate, Vector3.Zero, station.Center, launcher);
        Assert.True(station.ArmorFraction < 1f, $"armor must be chipped, got {station.ArmorFraction:F3}");
        Assert.Equal(0f, station.ShieldFraction, 3);

        // Regenerate: armor regrows; shield stays 0 because armor isn't full yet.
        float preArmor = station.ArmorFraction;
        station.Regenerate(2f);
        float expectedArmor = Math.Min(1f, preArmor + 2f * armorRegen / ma);
        Assert.Equal(expectedArmor, station.ArmorFraction, 3);
        Assert.Equal(0f, station.ShieldFraction, 3);     // shield waited

        // Once the armor is full the shield resumes growing.
        for (int i = 0; i < 50 && station.ArmorFraction < 1f; i++) station.Regenerate(2f);
        Assert.Equal(1f, station.ArmorFraction, 3);
        station.Regenerate(2f);
        Assert.True(station.ShieldFraction > 0f, "shield must begin regenerating once armor is full");
        Console.WriteLine($"P125-DOMAIN-REGENSEQ: armor regrows first (shield frozen), then shield regrows — native stationIGC.h:195-208");
    }
}
