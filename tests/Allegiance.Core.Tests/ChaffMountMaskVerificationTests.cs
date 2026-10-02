using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P492 — chaff dispensers fit no hull: verify and fix the part-mask decode.
///
/// RESULT OF VERIFICATION: there is NO decode or index bug. The port's decode
/// of both sides of the chaff mount is byte-accurate against native
/// (<c>src/Igc/igc.h:530-539</c> enum order, <c>:1809</c> hull <c>pmEquipment</c>
/// offset/size, <c>:1939-1945</c> the launcher's <c>LauncherDef.partMask</c>
/// field). Chaff's "fits no hull on a hull-swap" outcome is a faithful
/// reproduction of native, caused by a genuine PCore001 DATA inconsistency:
/// every hull's chaff slot <c>PmEquipment[ET_ChaffLauncher]</c> is
/// <c>0x0100</c> (light hulls) / <c>0x0200</c> (capitals), while every real
/// chaff launcher's <c>LauncherPartMask</c> is <c>0x0001</c>/<c>0x0002</c> —
/// so <c>(partMask &amp; ~slotMask) == 0</c> is never satisfied.
///
/// These facts were established from the real bytes (offsets cross-checked
/// against the C++ struct arithmetic; a full scan of every chaff record finds
/// no <c>0x0100</c>/<c>0x0200</c> mask field anywhere). This test locks that
/// faithful decode so a future change that accidentally corrupts the chaff /
/// hull masks is caught. It is deliberately a DOCUMENTATION-of-truth test, not
/// a behavior-forcing test: no code change is warranted.
/// </summary>
public sealed class ChaffMountMaskVerificationTests
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    [Fact]
    public void RealChaffLaunchersHaveLowBitMasksMatchingTheDocDataFact()
    {
        var core = Core();

        var chaffMasks = core.Launchers
            .Where(kv => core.ChaffTypes.ContainsKey(kv.Value.ExpendabletypeId))
            .Select(kv => core.ChaffTypes[kv.Value.ExpendabletypeId].LauncherPartMask)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        // The data fact the P492 investigation established from the raw bytes:
        // every real chaff launcher's embedded LauncherDef.partMask is the
        // low-bit value 0x0001 (Counter) or 0x0002 (Hvy Counter).
        Assert.NotEmpty(chaffMasks);
        Assert.All(chaffMasks, m => Assert.True(m is 0x0001 or 0x0002,
            $"unexpected chaff launcher partMask 0x{m:X4}; decode changed?"));
    }

    [Fact]
    public void HullChaffSlotsUseHighBitSpace0100And0200()
    {
        var core = Core();

        // Representative light hull (fighter-class) and capital hull chaff slots.
        var fighter = core.HullTypes.Values.First(h => h.Name == "Fighter");
        var corvette = core.HullTypes.Values.First(h => h.Name == "Corvette");

        // ET_ChaffLauncher == 0 (igc.h:531); pmEquipment is decoded at the
        // native struct offset 509 (igc.h:1809), little-endian.
        Assert.Equal(0x0100, fighter.PmEquipment[(int)EquipmentType.ChaffLauncher]);
        Assert.Equal(0x0200, corvette.PmEquipment[(int)EquipmentType.ChaffLauncher]);

        // No hull's chaff slot has bit 0 or bit 1 set (the chaff mask bits).
        Assert.All(core.HullTypes.Values, h =>
            Assert.True((h.PmEquipment[(int)EquipmentType.ChaffLauncher] & 0x0003) == 0,
                $"hull {h.HullId} chaff slot unexpectedly covers chaff masks"));
    }

    [Fact]
    public void NoHullCoversAnyRealChaffLauncherMask_FaithfulToNative()
    {
        var core = Core();

        var chaffParts = core.Launchers
            .Where(kv => core.ChaffTypes.ContainsKey(kv.Value.ExpendabletypeId))
            .Select(kv => { core.TryBuildExpendableLauncher(kv.Key, out var p); return p; })
            .ToList();
        Assert.NotEmpty(chaffParts);

        foreach (var hull in core.HullTypes.Values)
            foreach (var part in chaffParts)
                Assert.False(hull.CanMountPart(part),
                    $"hull {hull.HullId} ('{hull.Name}') unexpectedly accepts chaff '{part.Name}' mask 0x{part.PartMask:X4} " +
                    $"(chaff slot 0x{hull.PmEquipment[(int)part.EquipmentType]:X4})");
    }

    [Fact]
    public void MissileAndMineLaunchersStillMountWhereTheyShould_PositiveControl()
    {
        var core = Core();

        // A real missile launcher that genuinely fits a light fighter's magazine
        // slot (0x8100 covers 0x0100): LRM Killer 1 mask 0x0100.
        var killer = core.Launchers.First(kv =>
            core.MissileTypes.TryGetValue(kv.Value.ExpendabletypeId, out var mt) &&
            mt.LauncherName == "LRM Killer 1");
        Assert.True(core.TryBuildExpendableLauncher(killer.Key, out var killerPart));
        Assert.Equal(EquipmentType.Magazine, killerPart.EquipmentType);
        Assert.True(core.HullTypes.Values.Any(h => h.CanMountPart(killerPart)),
            "LRM Killer 1 must still fit a fighter; missile mounting must be unaffected");

        // A real mine launcher that fits at least one hull (dispenser slot with
        // 0x0400 coverage): Mine Pack 1 mask 0x0400.
        var mine = core.Launchers.First(kv =>
            core.MineTypes.TryGetValue(kv.Value.ExpendabletypeId, out var mt) &&
            mt.LauncherName == "Mine Pack 1");
        Assert.True(core.TryBuildExpendableLauncher(mine.Key, out var minePart));
        Assert.Equal(EquipmentType.Dispenser, minePart.EquipmentType);
        Assert.True(core.HullTypes.Values.Any(h => h.CanMountPart(minePart)),
            "Mine Pack 1 must still fit some hull; dispenser mounting must be unaffected");
    }
}
