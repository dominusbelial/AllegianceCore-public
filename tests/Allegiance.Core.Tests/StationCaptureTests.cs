using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P126 — pure-domain proofs of the <b>capture state change</b> half of
/// <see cref="Station.Capture"/> (the port of the fudge + re-side inside native
/// <c>CFSShip::CaptureStation</c>, <c>fsship.cpp:577-631</c>): the post-capture
/// damage fudge (halve the CURRENT hull fraction, <c>fsship.cpp:582</c>) and the
/// &quot;heroic engineer&quot; regen-derived starting shield
/// (<c>fsship.cpp:589-596</c>), and the re-side to the capturing side
/// (<c>fsship.cpp:631</c>). The companion <see cref="StationCombatTests"/> covers
/// damage intake; this file covers only what capture changes. The wire/broadcast
/// half is covered by the live loopback (<see cref="StationCaptureLiveTests"/>).
/// Everything is data-derived from the real <c>PCore001.igc</c> static core
/// (capacity fields decoded in <c>DataStationTypeIGC.cs</c>); no hardcoded
/// numbers except the deliberate shield-capacity/regen pair under test.
/// </summary>
/// <remarks>
/// The station is synthetic (a real <see cref="DataStationTypeIGC"/> from the
/// core <c>with</c>-modified to the capacities/regen under test + a crafted
/// <see cref="DataStationIGC"/>), the same shape <see cref="StationCombatTests"/>
/// uses. <see cref="Station.Capture"/> needs no Mission/sector, so these are
/// direct object-level asserts.
/// </remarks>
public sealed class StationCaptureTests
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

    /// <summary>A synthetic station owned by <paramref name="side"/> with the
    /// given armor/shield capacities and shield-regen rate. Real type from the
    /// core, <c>with</c>-modified; shield chosen so the heroic-engineer formula
    /// resolves exactly (regen/maxShield known).</summary>
    private static Station Mk(StaticCore core, short side, float maxArmor, float maxShield, float shieldRegen)
    {
        var baseType = core.StationTypes.Values.First();
        var type = baseType with
        {
            MaxArmorHitPoints = maxArmor,
            MaxShieldHitPoints = maxShield,
            ShieldRegeneration = shieldRegen,
        };
        var data = new DataStationIGC(
            Position: [0f, 0f, -500f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: 0, SideId: side, StationId: 7801,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "BASE_7801");
        return new Station(data, type);
    }

    /// <summary>⭐ Capture halves the CURRENT armor fraction and sets the shield
    /// to the heroic-engineer regen-derived value (regen/maxShield·7), and
    /// re-sides the station to the capturing side — <c>fsship.cpp:582,589-596,631</c>.</summary>
    [Fact]
    public void Capture_halves_armor_sets_heroic_shield_and_resides()
    {
        // MaxShield 100, ShieldRegen 10 → regenRatio 0.1 → boost 0.7 (fsship.cpp:592-593).
        var s = Mk(Core(), side: 0, maxArmor: 5000f, maxShield: 100f, shieldRegen: 10f);
        Assert.Equal(1f, s.ArmorFraction);
        Assert.Equal((short)0, s.SideId);

        s.Capture(1);                       // hostile side 1 captures the side-0 base

        Assert.Equal(0.5f, s.ArmorFraction, 4);    // exactly halved (fsship.cpp:582)
        Assert.Equal(0.7f, s.ShieldFraction, 4);   // regenRatio·7 = 0.1·7 (fsship.cpp:593)
        Assert.Equal((short)1, s.SideId);          // re-sided (fsship.cpp:631)
    }

    /// <summary>⭐ The fudge is multiplicative of the CURRENT value, not a reset:
    /// capturing an already-half-damaged base halves it again (0.5 → 0.25).</summary>
    [Fact]
    public void Capture_again_halves_the_already_halved_fraction()
    {
        var s = Mk(Core(), side: 0, maxArmor: 5000f, maxShield: 100f, shieldRegen: 10f);
        s.Capture(1);                        // 1.0 → 0.5
        Assert.Equal(0.5f, s.ArmorFraction, 4);
        s.Capture(2);                        // 0.5 → 0.25 (halve CURRENT, not reset-to-half)
        Assert.Equal(0.25f, s.ArmorFraction, 4);
        Assert.Equal((short)2, s.SideId);
    }

    /// <summary>⭐ A shield-less station type (MaxShield == 0) starts with zero
    /// shield after capture — the heroic-engineer boost is guarded against
    /// divide-by-zero and defaults to 0 (<c>fsship.cpp:589-590,596</c>).</summary>
    [Fact]
    public void Capture_on_shield_less_station_sets_shield_zero()
    {
        var s = Mk(Core(), side: 0, maxArmor: 5000f, maxShield: 0f, shieldRegen: 10f);
        Assert.False(s.HasShield);
        s.Capture(1);
        Assert.Equal(0.5f, s.ArmorFraction, 4);
        Assert.Equal(0f, s.ShieldFraction);
        Assert.Equal((short)1, s.SideId);
    }
}
