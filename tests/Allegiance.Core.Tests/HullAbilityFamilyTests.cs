using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P300 — completes the <c>c_habm*</c> hull-ability-bit family in
/// <see cref="DataHullTypeIGC"/>. The native canonical 16-bit list is
/// enumerated by its own debug CSV-dump routine
/// (<c>Igc/missionigc.cpp:5078-5108</c>); the port had 14 of 16 bits named
/// after P299 added <c>HabmThreatToStation</c>, leaving
/// <c>HabmCaptureThreat</c> (0x08) and <c>HabmMiner</c> (0x4000) unnamed.
///
/// Unlike a ported-but-unreachable enum member, these two bits are GENUINELY
/// set on real hulls in the shipped corpus (troop transports/APCs carry
/// <c>CanCapture</c> → <c>c_habmCaptureThreat</c>; miners carry <c>IsMiner</c>
/// → <c>c_habmMiner</c>). Their native consumers are respectively the
/// asset-mask → cluster-warning / spotting-score system and the client
/// targeting-command system — neither of which this port has yet — so this
/// task decodes the constants for family completeness and defers behavioral
/// wiring to a named future prerequisite (no fabricated integration point).
/// These tests pin the decode value AND prove the bits are populated in real
/// corpus data (the discriminator that separates "real capability, not yet
/// wired" from "dead enum member nobody sets").
/// </summary>
public class HullAbilityFamilyTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
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

    private static IEnumerable<DataHullTypeIGC> AllHulls() =>
        RepresentativeCore.Value.HullTypes.Values;

    /// <summary>The two newly-added bits have the exact native bit values
    /// (<c>c_habmCaptureThreat = 0x08</c> igc.h:729, <c>c_habmMiner = 0x4000</c>
    /// igc.h:740).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Newly_added_bits_match_native_values()
    {
        Require.Artwork();
        Assert.Equal(0x08, DataHullTypeIGC.HabmCaptureThreat);
        Assert.Equal(0x4000, DataHullTypeIGC.HabmMiner);
    }

    /// <summary>The full 16-bit native hull-ability family is now representable:
    /// each canonical bit (<c>Igc/missionigc.cpp:5078-5108</c>) has a named
    /// constant. A missing constant would fail loudly here if the family drifts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Complete_native_hull_ability_family_is_named()
    {
        Require.Artwork();
        var expected = new Dictionary<string, short>
        {
            ["Board"] = 0x01, ["Rescue"] = 0x02, ["Lifepod"] = 0x04,
            ["CaptureThreat"] = 0x08, ["LandOnCarrier"] = 0x10, ["NoRipcord"] = 0x20,
            ["IsRipcordTarget"] = 0x40, ["Fighter"] = 0x80,
            ["RemoteLeadIndicator"] = 0x100, ["ThreatToStation"] = 0x200,
            ["Carrier"] = 0x400, ["LeadIndicator"] = 0x800,
            ["IsLtRipcordTarget"] = 0x1000, ["CanLtRipcord"] = 0x2000,
            ["Miner"] = 0x4000, ["Builder"] = unchecked((short)0x8000),
        };
        foreach (var (name, value) in expected)
        {
            var prop = typeof(DataHullTypeIGC).GetField("Habm" + name)
                ?? throw new InvalidOperationException(
                    $"DataHullTypeIGC.Habm{name} is missing (family drift!)");
            Assert.Equal(value, (short)(prop.GetValue(null)!));
        }
    }

    /// <summary>⭐ The capture-threat bit is GENUINELY populated in real corpus
    /// data: at least one real hull type carries <c>c_habmCaptureThreat</c>.
    /// Selected by bit, not name (model names recur across cores with different
    /// capability sets), the same corpus-evidence discipline P299 used.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CaptureThreat_bit_is_populated_on_real_hulls()
    {
        Require.Artwork();
        var cap = AllHulls()
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmCaptureThreat))
            .Select(h => $"{h.HullId}:{h.Name}|{h.ModelName}")
            .Take(8)
            .ToList();
        Assert.NotEmpty(cap);
    }

    /// <summary>⭐ The miner bit is GENUINELY populated in real corpus data: at
    /// least one real hull type carries <c>c_habmMiner</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Miner_bit_is_populated_on_real_hulls()
    {
        Require.Artwork();
        var min = AllHulls()
            .Where(h => h.HasCapability(DataHullTypeIGC.HabmMiner))
            .Select(h => $"{h.HullId}:{h.Name}|{h.ModelName}")
            .Take(8)
            .ToList();
        Assert.NotEmpty(min);
    }
}
