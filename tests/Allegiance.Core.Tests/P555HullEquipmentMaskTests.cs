using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P555 — the hull <c>pmEquipment</c> decode bug: <c>PartMask pmEquipment[ET_MAX]</c>
/// is read at byte 509 in the port but native alignment (<c>src/Igc/igc.h:1809</c>,
/// 2-byte aligned after <c>char textureName[13]</c>) puts it at <b>510</b>.
/// Every non-weapon mount mask (shields, chaff, magazines, dispensers, cloaks,
/// packs, afterburners) was garbled, so a hull's own native preferred equipment
/// rarely mounted while foreign/capital gear often did (the P539/P539b
/// "invincible bot" mechanism).
///
/// These tests FAIL on the pre-fix code (offset 509) and PASS at 510:
///   • across the whole corpus, preferred-equipment-not-mounting collapses by
///     &gt;20× (measured 48,990 → 475, a 103× reduction; the residue is genuine
///     cross-core data — training/event hulls that list preferred gear with no
///     mount socket, plus the flagship afterburner quirks below);
///   • on the canonical PCore001, every preferred equipment part mounts except
///     exactly the 3 documented native afterburner-socket quirks (542 → 3);
///   • the shield table: Scout/Adv Scout/Adv Fighter take Sm Shield (0x0001),
///     Bomber takes Med (0x0002), Cruiser/Destroyer take Lrg (0x0004);
///   • the exact thing P539b called "not a bug": Sm Shield 1 (part 21) MOUNTS
///     on the Adv Scout once the slot is read natively.
/// </summary>
public sealed class P555HullEquipmentMaskTests
{
    private static StaticCore Pcore001()
    {
        var path = Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        return StaticCore.Load(IgcReader.ParseFile(path));
    }

    private static byte[] RawPayload(string path, IgcFile f, IgcRecord r)
    {
        var raw = File.ReadAllBytes(path);
        var body = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
        return raw[(body + r.PayloadOffset)..(body + r.PayloadOffset + r.Size)];
    }

    /// <summary>Count, across every static core in the corpus, the hull/preferred
    /// EQUIPMENT-part pairs that do not mount (real PartType, non-Weapon), reading
    /// <c>pmEquipment</c> from the raw bytes at <paramref name="pmEqOffset"/>.
    /// Non-representative (training/event) cores legitimately list preferred gear
    /// whose mount socket that hull lacks, so the absolute count is corpus-shaped;
    /// what P555 proves is the catastrophic collapse when the offset is native.</summary>
    private static int CountUnmounted(int pmEqOffset)
    {
        int fail = 0;
        foreach (var path in Corpus.Files)
        {
            var f = IgcReader.ParseFile(path);
            if (!f.Records.Any(r => r.Type == IgcObjectType.HullType)) continue;
            var parts = new Dictionary<short, DataPartTypeIGC>();
            foreach (var r in f.Records.Where(r => r.Type == IgcObjectType.PartType))
            {
                if (r.Size == DataLauncherTypeIGC.Size) continue;   // compact launcher
                var p = DataPartTypeIGC.Decode(RawPayload(path, f, r));
                if (!parts.ContainsKey(p.PartId)) parts[p.PartId] = p;
            }
            foreach (var r in f.Records.Where(r => r.Type == IgcObjectType.HullType))
            {
                var pay = RawPayload(path, f, r);
                if (pay.Length < 540) continue;
                short[] eq = new short[8];
                for (int i = 0; i < 8; i++) eq[i] = BitConverter.ToInt16(pay, pmEqOffset + 2 * i);
                foreach (var pid in Enumerable.Range(0, 14).Select(i => BitConverter.ToInt16(pay, 466 + 2 * i)))
                {
                    if (pid < 0) continue;
                    if (!parts.TryGetValue(pid, out var p)) continue;
                    if (p.EquipmentType == EquipmentType.Weapon) continue;
                    int idx = (int)p.EquipmentType;
                    if (idx >= 8) continue;
                    if ((p.PartMask & ~eq[idx]) != 0) fail++;
                }
            }
        }
        return fail;
    }

    /// <summary>THE P555 corpus invariant: preferred equipment mounting must
    /// improve catastrophically when <c>pmEquipment</c> is read at native offset
    /// 510 vs the buggy 509. Measured: 48,990 → 475 across the corpus (a 103×
    /// reduction; we assert only &gt;20× to stay corpus-content-independent).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PreferredEquipmentMounting_CollapsesToNearZero_AtNativeOffset()
    {
        Require.Artwork();
        int at509 = CountUnmounted(509);
        int at510 = CountUnmounted(510);
        Assert.True(at510 * 20 < at509,
            $"native-aligned decode must slash unmounted-preferred by >20× (509={at509}, 510={at510})");
    }

    /// <summary>On the canonical PCore001, every preferred equipment part mounts
    /// except exactly the 3 genuine native afterburner-socket quirks: the
    /// flagship hulls (#73/#74) prefer Hyper-Jump (mask 0x8000) with no
    /// afterburner socket, and the "Mustang" hull (#129) prefers Ion Booster
    /// (0x0002) into a slot that only covers 0x0001. (Pre-fix: 542.)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PreferredEquipmentMounts_OnPcore001_ExceptDocumentedAfterburnerQuirks()
    {
        Require.Artwork();
        var path = Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase));
        var core = StaticCore.Load(IgcReader.ParseFile(path));
        var failures = new List<string>();

        foreach (var h in core.HullTypes.Values)
            foreach (var pid in h.PreferredPartsTypes)
            {
                if (pid < 0) break;
                if (!core.PartTypes.TryGetValue(pid, out var p)) continue;
                if (p.EquipmentType == EquipmentType.Weapon) continue;
                if (!h.CanMountPart(p))
                    failures.Add($"hull #{h.HullId} ({h.Name}) pref {pid} ({p.Name}, et={(int)p.EquipmentType} mask 0x{p.PartMask:X4}) slot 0x{h.PmEquipment[(int)p.EquipmentType]:X4}");
            }

        Assert.True(failures.Count == 3,
            $"expected the 3 documented afterburner quirks, got {failures.Count}:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(40)));
    }

    /// <summary>The P555 proof table — the shield mount masks after the 510 fix,
    /// for the PCore001 hulls the task names.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShieldSlots_MatchNativePartMasks_OnNamedHulls()
    {
        Require.Artwork();
        var core = Pcore001();
        static short Shield(DataHullTypeIGC h) => h.PmEquipment[(int)EquipmentType.Shield];

        Assert.Equal(0x0001, Shield(By(core, "Scout")));        // Sm Shield 1/2/3
        Assert.Equal(0x0001, Shield(By(core, "Adv Scout")));
        Assert.Equal(0x0001, Shield(By(core, "Adv Fighter")));
        Assert.Equal(0x0002, Shield(By(core, "Bomber")));       // Med
        Assert.Equal(0x0004, Shield(By(core, "Cruiser")));      // Lrg
        Assert.Equal(0x0004, Shield(By(core, "Destroyer")));    // Lrg
    }

    /// <summary>The exact case P539b mis-labelled "native rejects Sm Shield 1 on
    /// Adv Scout too, not a bug": at native offset 510, Sm Shield 1 (part 21,
    /// mask 0x0001) mounts on the Adv Scout — and is one of its preferred parts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SmShield1_MountsOnAdvScout_AndIsPreferred()
    {
        Require.Artwork();
        var core = Pcore001();
        var advScout = By(core, "Adv Scout");
        var sm = core.PartTypes[(short)21];   // Sm Shield 1
        Assert.Equal("Sm Shield 1", sm.Name);
        Assert.Equal(EquipmentType.Shield, sm.EquipmentType);
        Assert.True(advScout.CanMountPart(sm), "Sm Shield 1 must mount on the Adv Scout at native offset 510");
        Assert.Contains((short)21, advScout.PreferredPartsTypes);
    }

    private static DataHullTypeIGC By(StaticCore core, string name)
        => core.HullTypes.Values.First(x => x.Name == name);
}
