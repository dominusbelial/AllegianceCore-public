using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P475 — the station-durability half of the side-global-attribute (GA) wiring:
/// a side's <see cref="GlobalAttributeSet"/> now scales a station's max armor,
/// max shield and both regeneration rates through four new effective getters on
/// <see cref="Station"/> that reuse the P446/P467 shared side resolution
/// (<c>SideGlobalAttributes()</c>, identity when the station has no resolvable
/// mission). Native wraps these in GA multipliers at the <c>MyStationType</c>
/// getters (<c>stationIGC.cpp:596,607,617,622</c>). Fractions stay the stored
/// state (<c>ArmorFraction</c>/<c>ShieldFraction</c>), so a GA change rescales
/// the absolute maxima without touching the fractions — exactly native, and
/// why the <c>STATIONS_UPDATE</c> wire fraction needs no client change.
/// <para>Each test is data-derived from the real PCore001 static core and
/// compares the GA-boosted outcome to the identity (1.0) baseline to isolate
/// the multiplier. Every test failed on the pre-P475 tree first (recorded in
/// the P475 report): before wiring the boosted and identity outcomes were
/// identical, so the ratio/difference assertions below failed.</para>
/// </summary>
public sealed class P475SideGaStationDurabilityTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> Map = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private const byte DefArmor = 8, DefShield = 9;   // two indices with positive cells for a real type

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>A real damage type id from a real ordnance record whose armor and
    /// shield defense cells are BOTH strictly positive for <see cref="DefArmor"/>/
    /// <see cref="DefShield"/>, so a real (non-degenerate) matrix value drives the
    /// shield-absorb and overflow math.</summary>
    private static byte PositiveCellsDamageType(StaticCore core)
    {
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            if (Dtm(core, pt.DamageType, DefArmor) > 0f && Dtm(core, pt.DamageType, DefShield) > 0f)
                return pt.DamageType;
        }
        throw new InvalidOperationException("no projectile damage type with positive cells for armor 8 / shield 9");
    }

    /// <summary>Build a started two-side mission. Both sides use the first civ
    /// (Dreghklar), whose station-GA base (indices 4-7) is identity 1.0 — so the
    /// <paramref name="factors"/> applied to side 0 are the ONLY GA difference,
    /// isolating the multiplier exactly (the P467b recipe). Side 1 is the enemy
    /// (launcher) side.</summary>
    private static Mission NewMission(params (int idx, float val)[] factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId), new SideSpec(1, "Enemy", civ.CivilizationId)],
            new NullIgcSite());
        m.Stage = MissionStage.Started;
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    /// <summary>A synthetic station: a real station type from the core
    /// <c>with</c>-modified to chosen capacities/defense indices, wrapped with a
    /// crafted <see cref="DataStationIGC"/> on <paramref name="side"/>. Placed in
    /// the mission's sector 0 via <see cref="Mission.PlaceStation"/> (which wires
    /// <see cref="Station.Mission"/>), so its side-GA resolves.</summary>
    private static Station NewStation(Mission m, short id, short side,
        float maxArmor, float maxShield, float shieldRegen, float armorRegen = 0f)
    {
        var baseType = Core.Value.StationTypes.Values.First();
        var type = baseType with
        {
            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = maxShield,
            DefenseTypeArmor = DefArmor,
            DefenseTypeShield = DefShield,
            ArmorRegeneration = armorRegen,
            ShieldRegeneration = shieldRegen,
            SabmCapabilities = 0,
        };
        var spot = new Vector3(0f, 0f, -500f);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: m.Sectors[0].Id, SideId: side, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
        var station = new Station(data, type);
        m.PlaceStation(station, m.Sectors[0]);
        return station;
    }

    /// <summary>A ship on the enemy (side 1) — needed only so the station's
    /// <see cref="Station.ReceiveDamage"/> resolves its damage matrix via the
    /// launcher's mission (DamageConstant).</summary>
    private static Ship NewEnemy(Mission m)
    {
        var hull = Core.Value.HullTypes.Values.First(h => h.HitPoints > 0f);
        var ship = new Ship(9901, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    // ── GA plumbing: indices, identity, no-mission ───────────────────────

    /// <summary>⭐ The four station-durability attribute indices are the
    /// documented native constants (igc.h:591-595), and a station with no
    /// resolvable mission reproduces the raw values exactly — so the whole
    /// station half is a no-op for the default (no-mission) case.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_indices_match_native_and_no_mission_is_raw()
    {
        Require.Artwork();
        Assert.Equal(4, GlobalAttributeSet.MaxArmorStation);
        Assert.Equal(5, GlobalAttributeSet.ArmorRegenerationStation);
        Assert.Equal(6, GlobalAttributeSet.MaxShieldStation);
        Assert.Equal(7, GlobalAttributeSet.ShieldRegenerationStation);

        // No mission (constructed directly, never placed): every effective getter
        // reproduces the raw type value.
        var baseType = Core.Value.StationTypes.Values.First();
        var type = baseType with
        {
            MaxArmorHitPoints = 999f, MaxShieldHitPoints = 888f,
            ArmorRegeneration = 77f, ShieldRegeneration = 66f,
        };
        var data = new DataStationIGC(
            Position: [0f, 0f, -500f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: 0, SideId: 0, StationId: 9123,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SOLO");
        var solo = new Station(data, type);
        Assert.Equal(999f, solo.EffectiveMaxArmor(), 6);
        Assert.Equal(888f, solo.EffectiveMaxShield(), 6);
        Assert.Equal(77f, solo.EffectiveArmorRegen(), 6);
        Assert.Equal(66f, solo.EffectiveShieldRegen(), 6);
    }

    /// <summary>⭐ The four effective getters scale the raw type value by the
    /// owning side's GA factor.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Effective_getters_scale_by_side_ga()
    {
        Require.Artwork();
        var m = NewMission(
            (GlobalAttributeSet.MaxArmorStation, 1.5f),
            (GlobalAttributeSet.MaxShieldStation, 2f),
            (GlobalAttributeSet.ArmorRegenerationStation, 2f),
            (GlobalAttributeSet.ShieldRegenerationStation, 3f));
        var st = NewStation(m, 9210, 0, 2000f, 500f, shieldRegen: 25f, armorRegen: 100f);
        Assert.Equal(2000f * 1.5f, st.EffectiveMaxArmor(), 3);
        Assert.Equal(500f * 2f, st.EffectiveMaxShield(), 3);
        Assert.Equal(100f * 2f, st.EffectiveArmorRegen(), 3);
        Assert.Equal(25f * 3f, st.EffectiveShieldRegen(), 3);
    }

    // ── MaxArmorStation: overflow damage denominator ─────────────────────

    /// <summary>Apply the same overflowing hit (absorbed = 1.5, so overflow =
    /// 0.5) to a fresh full station on a side whose <c>MaxArmorStation</c> is
    /// <paramref name="gaMul"/>; returns the hull fraction it lost. Native:
    /// <c>dmg = (overflow·maxShield·dtmArmor)/(maxArmor·dtmShield)</c>
    /// (<c>stationIGC.cpp:186</c>) with <c>maxArmor = GetMaxArmorHitPoints()</c>
    /// (raw·GA). <c>MaxShieldStation</c> is left identity so only the armor
    /// denominator scales.</summary>
    private static float ArmorOverflowDrop(float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.MaxArmorStation, gaMul));
        const float ms = 500f, ma = 2000f;
        var st = NewStation(m, 9220, 0, ma, ms, shieldRegen: 0f);
        var launcher = NewEnemy(m);
        byte dtype = PositiveCellsDamageType(Core.Value);
        float ds = Dtm(Core.Value, dtype, DefShield);
        float amount = 1.5f * ms / ds;          // absorbed = 1.5 (>1) → overflow 0.5
        float f0 = st.ArmorFraction;
        st.ReceiveDamage(dtype, amount, m.LastUpdate, Vector3.Zero, st.Center, launcher);
        return f0 - st.ArmorFraction;
    }

    /// <summary>⭐ A side with <c>MaxArmorStation = 2</c> doubles the overflow
    /// damage DENOMINATOR: the same overflowing hit chips half the hull fraction
    /// (native <c>GetMaxArmorHitPoints() · c_gaMaxArmorStation</c>). Pre-fix both
    /// missions used the raw denominator, so the drops were identical and the
    /// ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxArmorStation_scales_the_overflow_damage_denominator()
    {
        Require.Artwork();
        float idDrop = ArmorOverflowDrop(1f);
        float boostDrop = ArmorOverflowDrop(2f);
        Assert.True(idDrop > 0.05f, $"identity overflow must chip the armor; got {idDrop:F4}");
        Assert.InRange(boostDrop / idDrop, 0.44f, 0.56f);   // ~0.5 (1/F with F=2)
        Console.WriteLine($"P475-ARMORMAX: identity drop {idDrop:F4}; GA2 drop {boostDrop:F4}; ratio {boostDrop / idDrop:F3} (expect ~0.5)");
    }

    // ── MaxShieldStation: absorption capacity ────────────────────────────

    /// <summary>Apply the same fully-absorbed hit to a fresh full shield on a
    /// side whose <c>MaxShieldStation</c> is <paramref name="gaMul"/>; returns the
    /// shield fraction consumed (hull untouched). Native:
    /// <c>absorbed = amount·dtmShield / GetMaxShieldHitPoints()</c>
    /// (<c>stationIGC.cpp:172</c>, max = raw·GA). <c>MaxArmorStation</c> is left
    /// identity so only the shield capacity scales.</summary>
    private static float ShieldAbsorbDrop(float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.MaxShieldStation, gaMul));
        const float ms = 500f;
        var st = NewStation(m, 9230, 0, 2000f, ms, shieldRegen: 0f);
        var launcher = NewEnemy(m);
        byte dtype = PositiveCellsDamageType(Core.Value);
        float ds = Dtm(Core.Value, dtype, DefShield);
        float amount = 0.5f * ms / ds;          // identity absorbed = 0.5 (<1, no overflow)
        float sf0 = st.ShieldFraction;
        st.ReceiveDamage(dtype, amount, m.LastUpdate, Vector3.Zero, st.Center, launcher);
        return sf0 - st.ShieldFraction;
    }

    /// <summary>⭐ A side with <c>MaxShieldStation = 2</c> raises absorption
    /// capacity: the same hit consumes HALF the shield fraction while the hull
    /// still takes no damage (native <c>GetMaxShieldHitPoints() ·
    /// c_gaMaxShieldStation</c>). Pre-fix both missions used the raw max, so the
    /// drops were identical and the ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxShieldStation_raises_shield_absorption_capacity()
    {
        Require.Artwork();
        float idDrop = ShieldAbsorbDrop(1f);
        float boostDrop = ShieldAbsorbDrop(2f);
        Assert.InRange(boostDrop / idDrop, 0.44f, 0.56f);   // ~0.5
        Console.WriteLine($"P475-SHLDMAX: identity drop {idDrop:F4}; GA2 drop {boostDrop:F4}; ratio {boostDrop / idDrop:F3} (expect ~0.5)");
    }

    // ── ShieldRegenerationStation: shield regen speed ────────────────────

    /// <summary>Knock a fresh shield to 0.5 (fully absorbed), then run
    /// <c>dt = 2</c> s of <see cref="Station.Regenerate"/> on a side whose
    /// <c>ShieldRegenerationStation</c> is <paramref name="gaMul"/>; returns the
    /// shield-fraction gain. Only the regen GA is set, so
    /// <c>gain = dt·(raw·GA)/(raw max)</c> — with the max GA at identity the gain
    /// scales linearly with the regen GA.</summary>
    private static float ShieldRegenGain(float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.ShieldRegenerationStation, gaMul));
        const float ms = 500f, shieldRegen = 25f;
        var st = NewStation(m, 9240, 0, 2000f, ms, shieldRegen);
        var launcher = NewEnemy(m);
        byte dtype = PositiveCellsDamageType(Core.Value);
        float ds = Dtm(Core.Value, dtype, DefShield);
        st.ReceiveDamage(dtype, 0.5f * ms / ds, m.LastUpdate, Vector3.Zero, st.Center, launcher);  // shield → 0.5, armor full
        Assert.Equal(0.5f, st.ShieldFraction, 3);
        float before = st.ShieldFraction;
        st.Regenerate(2f);                                  // armor full → shield regrows (stationIGC.h:201-208)
        return st.ShieldFraction - before;
    }

    /// <summary>⭐ A side with <c>ShieldRegenerationStation = 2</c> regenerates
    /// the station's shield twice as fast (native <c>GetShieldRegeneration() ·
    /// c_gaShieldRegenerationStation</c>). Pre-fix both missions regenerated at
    /// the raw rate, so the gains were identical and the ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShieldRegenerationStation_speeds_shield_regeneration()
    {
        Require.Artwork();
        float idGain = ShieldRegenGain(1f);
        float boostGain = ShieldRegenGain(2f);
        Assert.True(idGain > 1e-4f, $"identity shield regen must be measurable; got {idGain:E2}");
        Assert.InRange(boostGain / idGain, 1.8f, 2.2f);   // ~2
        Console.WriteLine($"P475-SHLDREGEN: identity gain {idGain:F4}; GA2 gain {boostGain:F4}; ratio {boostGain / idGain:F3} (expect ~2)");
    }

    // ── ArmorRegenerationStation: armor regen speed ──────────────────────

    /// <summary>Chip the armor (overflow hit → armor not full, shield 0), then run
    /// <c>dt = 2</c> s of regen on a side whose <c>ArmorRegenerationStation</c> is
    /// <paramref name="gaMul"/>; returns the armor-fraction gain (armor regrows
    /// FIRST, <c>stationIGC.h:195-200</c>). Only the regen GA is set, so the gain
    /// scales linearly with it.</summary>
    private static float ArmorRegenGain(float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.ArmorRegenerationStation, gaMul));
        const float ms = 500f, ma = 2000f, armorRegen = 100f;
        var st = NewStation(m, 9250, 0, ma, ms, shieldRegen: 0f, armorRegen);
        var launcher = NewEnemy(m);
        byte dtype = PositiveCellsDamageType(Core.Value);
        float ds = Dtm(Core.Value, dtype, DefShield);
        st.ReceiveDamage(dtype, 1.5f * ms / ds, m.LastUpdate, Vector3.Zero, st.Center, launcher);  // overflow → armor chipped, shield 0
        Assert.True(st.ArmorFraction < 1f, "armor must be chipped");
        float before = st.ArmorFraction;
        st.Regenerate(2f);
        return st.ArmorFraction - before;
    }

    /// <summary>⭐ A side with <c>ArmorRegenerationStation = 2</c> regenerates
    /// the station's armor twice as fast (native <c>GetArmorRegeneration() ·
    /// c_gaArmorRegenerationStation</c>). Pre-fix both missions regenerated at
    /// the raw rate, so the gains were identical and the ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ArmorRegenerationStation_speeds_armor_regeneration()
    {
        Require.Artwork();
        float idGain = ArmorRegenGain(1f);
        float boostGain = ArmorRegenGain(2f);
        Assert.True(idGain > 1e-4f, $"identity armor regen must be measurable; got {idGain:E2}");
        Assert.InRange(boostGain / idGain, 1.8f, 2.2f);   // ~2
        Console.WriteLine($"P475-ARMORREGEN: identity gain {idGain:F4}; GA2 gain {boostGain:F4}; ratio {boostGain / idGain:F3} (expect ~2)");
    }

    // ── Capture: old-owner GA + switch to new owner ──────────────────────

    /// <summary>⭐ On capture the heroic-engineer starting shield is computed from
    /// the OLD owner's GA (native computes <c>GetShieldRegeneration()/GetMaxShieldHitPoints()</c>
    /// from the GA wrapper BEFORE <c>SetSide</c>, <c>fsship.cpp:592-634</c>): with
    /// the old owner's <c>MaxShieldStation = 2</c> the denominator doubles, so the
    /// boost halves. Pre-fix Capture used raw type values, so the boost ignored the
    /// GA and this assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Capture_heroic_shield_uses_old_owners_ga()
    {
        Require.Artwork();
        var m = NewMission((GlobalAttributeSet.MaxShieldStation, 2f));   // old owner (side 0) max shield GA = 2
        var st = NewStation(m, 9260, 0, 5000f, 100f, shieldRegen: 10f);
        // Old-owner ratio = ShieldRegen/(MaxShield·2) = 10/200 = 0.05 → boost = 0.05·7 = 0.35.
        // (identity would be 10/100·7 = 0.7.)
        st.Capture(1);
        Assert.Equal(0.35f, st.ShieldFraction, 4);
        Assert.Equal((short)1, st.SideId);
        Console.WriteLine($"P475-CAPTURE: old-owner MaxShield GA=2 → heroic boost {st.ShieldFraction:F3} (identity would be 0.7)");
    }

    /// <summary>⭐ After capture the station immediately switches to the NEW
    /// owner's GA: <c>EffectiveMaxArmor</c> reads the capturing side's
    /// <c>MaxArmorStation</c> (native: after <c>pstation-&gt;SetSide</c>,
    /// <c>fsship.cpp:634</c>, the <c>MyStationType</c> getters read the new side).
    /// Pre-fix there was no GA at all, so the switch was invisible.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Capture_switches_max_and_regen_to_new_owners_ga()
    {
        Require.Artwork();
        var m = NewMission((GlobalAttributeSet.MaxArmorStation, 1.5f));   // side 0
        var arr1 = new float[GlobalAttributeSet.Count];
        Array.Fill(arr1, 1f);
        arr1[GlobalAttributeSet.MaxArmorStation] = 3f;
        arr1[GlobalAttributeSet.ArmorRegenerationStation] = 2f;
        m.Sides[1].GlobalAttributes = new GlobalAttributeSet(arr1);       // side 1 (capturer)

        var st = NewStation(m, 9270, 0, 2000f, 500f, shieldRegen: 0f, armorRegen: 100f);
        Assert.Equal(2000f * 1.5f, st.EffectiveMaxArmor(), 3);   // old owner (side 0)
        Assert.Equal(100f * 1f, st.EffectiveArmorRegen(), 3);

        st.Capture(1);                                              // captured by side 1

        Assert.Equal(2000f * 3f, st.EffectiveMaxArmor(), 3);   // new owner (side 1)
        Assert.Equal(100f * 2f, st.EffectiveArmorRegen(), 3);
        Assert.Equal((short)1, st.SideId);
    }

    // ── Wire: STATIONS_UPDATE fraction stays correct (scope item 2) ──────

    /// <summary>⭐ The wire carries the stored FRACTION, not an absolute value:
    /// after a GA-scaled shield hit, <c>STATIONS_UPDATE</c>'s bpShieldFraction
    /// matches the stored <see cref="Station.ShieldFraction"/> (within div-240
    /// quantization), so the HUD/target readout doesn't jump against the scaled
    /// max. No client change is needed because the fraction never crosses the wire
    /// unscaled.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stations_update_fraction_tracks_stored_fraction_under_ga()
    {
        Require.Artwork();
        var m = NewMission((GlobalAttributeSet.MaxShieldStation, 2f));
        const float ms = 500f;
        var st = NewStation(m, 9280, 0, 2000f, ms, shieldRegen: 0f);
        var launcher = NewEnemy(m);
        byte dtype = PositiveCellsDamageType(Core.Value);
        float ds = Dtm(Core.Value, dtype, DefShield);
        st.ReceiveDamage(dtype, 0.5f * ms / ds, m.LastUpdate, Vector3.Zero, st.Center, launcher); // identity-absorbed 0.5 → GA2 absorbed 0.25
        Assert.Equal(0.75f, st.ShieldFraction, 3);

        var up = StationsUpdate.FromStations([st]);
        var entry = up.Entries.Single(e => e.StationId == st.StationId);
        float wireFrac = entry.BpShieldFraction / 240f;
        Assert.InRange(wireFrac, st.ShieldFraction - 0.006f, st.ShieldFraction + 0.006f);
        Console.WriteLine($"P475-WIRE: stored ShieldFraction {st.ShieldFraction:F3}; STATIONS_UPDATE frac {wireFrac:F3} (tracking, div-240)");
    }
}
