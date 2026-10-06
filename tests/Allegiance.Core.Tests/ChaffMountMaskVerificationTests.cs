using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P492/P555 — the chaff mount-mask decode, corrected by P555.
///
/// P492 originally concluded (from the pre-P555 decode) that "chaff fits no
/// hull" and that hull chaff slots lived in the high bit-space (0x0100/0x0200).
/// That was WRONG: it was measuring <c>PmEquipment[ET_ChaffLauncher]</c> at the
/// buggy byte offset 509 instead of the native 510 (<c>src/Igc/igc.h:1809</c>,
/// see <c>docs/reports/P555_HULL_MASK_DECODE.md</c>). At native offset 510 the
/// slot values are the real low-bit masks:
///   • 0x0000 — no chaff socket (some hulls);
///   • 0x0001 — Counter-chaff socket (virtually all fighters/scouts/bombers/
///     miners/constructors/transports);
///   • 0x0002 — Hvy-Counter socket (capital ships: corvette/cruiser/destroyer).
/// Because the real chaff launcher masks are 0x0001 (Counter) / 0x0002 (Hvy
/// Counter), chaff now MOUNTS on the hulls that carry the matching socket —
/// the exact behavior native intends. These tests lock the corrected decode.
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

    private static DataHullTypeIGC By(StaticCore core, string name)
        => core.HullTypes.Values.First(x => x.Name == name);

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

        // Unchanged by P555: every real chaff launcher's LauncherDef.partMask is
        // 0x0001 (Counter) or 0x0002 (Hvy Counter).
        Assert.NotEmpty(chaffMasks);
        Assert.All(chaffMasks, m => Assert.True(m is 0x0001 or 0x0002,
            $"unexpected chaff launcher partMask 0x{m:X4}; decode changed?"));
    }

    /// <summary>P555-corrected: hull chaff slots are the real low-bit masks
    /// (0x0000 / 0x0001 / 0x0002), NOT the high-bit 0x0100/0x0200 the buggy
    /// offset-509 decode produced. A Fighter has the Counter socket (0x0001); a
    /// capital (Corvette) has the Hvy-Counter socket (0x0002); no hull carries a
    /// high-bit-space value.</summary>
    [Fact]
    public void HullChaffSlots_AreLowBitMasks_NotTheBuggyHighBitSpace()
    {
        var core = Core();

        var fighter = By(core, "Fighter");
        var corvette = By(core, "Corvette");

        Assert.Equal(0x0001, fighter.PmEquipment[(int)EquipmentType.ChaffLauncher]);
        Assert.Equal(0x0002, corvette.PmEquipment[(int)EquipmentType.ChaffLauncher]);

        // No hull's chaff slot uses the high nibble the buggy 509 decode produced.
        Assert.All(core.HullTypes.Values, h =>
        {
            var v = (short)h.PmEquipment[(int)EquipmentType.ChaffLauncher];
            Assert.True(v is 0x0000 or 0x0001 or 0x0002,
                $"hull {h.HullId} chaff slot 0x{v:X4} is not a real low-bit socket");
        });
    }

    /// <summary>P555-corrected (inverts the P492 conclusion): chaff DOES mount.
    /// A Fighter's Counter socket (0x0001) accepts a real Counter chaff
    /// launcher (mask 0x0001); a Corvette's Hvy socket (0x0002) accepts a real
    /// Hvy Counter (0x0002).</summary>
    [Fact]
    public void Chaff_MountsOnSocketedHulls_AfterNativeOffsetFix()
    {
        var core = Core();

        var counter = core.Launchers.First(kv =>
            core.ChaffTypes.TryGetValue(kv.Value.ExpendabletypeId, out var ct) &&
            ct.LauncherPartMask == 0x0001);
        var hvy = core.Launchers.First(kv =>
            core.ChaffTypes.TryGetValue(kv.Value.ExpendabletypeId, out var ct) &&
            ct.LauncherPartMask == 0x0002);
        Assert.True(core.TryBuildExpendableLauncher(counter.Key, out var counterPart));
        Assert.True(core.TryBuildExpendableLauncher(hvy.Key, out var hvyPart));

        Assert.True(By(core, "Fighter").CanMountPart(counterPart),
            "a Fighter's 0x0001 chaff socket must accept a Counter chaff launcher");
        Assert.True(By(core, "Corvette").CanMountPart(hvyPart),
            "a Corvette's 0x0002 chaff socket must accept a Hvy Counter chaff launcher");
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
