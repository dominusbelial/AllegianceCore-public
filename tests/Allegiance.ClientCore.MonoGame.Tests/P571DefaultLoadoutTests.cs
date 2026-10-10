using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P571 — the docked shop's NEW-HULL default loadout (native TryToBuyParts /
/// BuyHull→RefreshLoadout) and the relaunch-after-death path. Uses the real
/// PCore001.igc under the artwork dir, exercising real hull/part ids and masks.
/// </summary>
public class P571DefaultLoadoutTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static DataHullTypeIGC Hull(short id) =>
        Catalog.Hulls.First(h => h.HullId == id);

    private static DataPartTypeIGC? LookupPart(short partId)
    {
        foreach (var p in Catalog.Parts) if (p.PartId == partId) return p;
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Part;
        return null;
    }

    private static short LauncherAmmo(short partId)
    {
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Ammo;
        return 0;
    }

    /// <summary>Select hull 285 (Adv Scout) from a bare current ship and read
    /// the pending loadout — the parts the launch would send (SendBuyLoadout
    /// sends shop.ChosenParts verbatim).</summary>
    private static ShopModel OpenAdvScout()
    {
        var m = new ShopModel(Catalog, Hull(1).HullId, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(285);
        return m;
    }

    /// <summary>⭐ P571 — selecting a NEW hull (Adv Scout 285) seeds the shop's
    /// pending loadout with the hull's NATIVE default loadout — the preferred
    /// parts that mount (P555 masks): Counter 1, EWS Probe 1, PW Gat Gun 1, MRM
    /// Seeker 1, Sm Shield 1 — even when the current/docked ship had ZERO parts
    /// (the respawned-lifepod "+0 parts" bug).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Selecting_adv_scout_seeds_native_default_loadout_from_bare_chassis()
    {
        Require.Artwork();
        var m = OpenAdvScout();

        var parts = m.ChosenParts;
        Assert.True(parts.Count > 0, "a newly chosen hull must carry its default loadout, never 0 parts");
        var expected = new HashSet<short> { 80, 81, 33, 153, 21 }; // Counter 1, EWS Probe 1, PW Gat Gun 1, MRM Seeker 1, Sm Shield 1
        Assert.Equal(expected, parts.Select(p => p.PartId).ToHashSet());

        // Each default part truly mounts the Adv Scout (native CanMount gate).
        foreach (var p in parts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && Hull(285).CanMountPart(pp), $"default part {p.PartId} must mount hull 285");
        }
    }

    /// <summary>⭐ P571 — the fighter's default loadout fills EVERY weapon
    /// hardpoint with the first preferred gun (native iMountMax = MaxWeapons),
    /// plus its shield/chaff/dispenser/magazine/afterburner. Uses Adv Fighter
    /// 288 (3 hardpoints): 3× PW Gat Gun + Hvy Booster + Counter + Mine Pack +
    /// Sm Shield + SRM Dumbfire = 8 parts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Selecting_adv_fighter_mounts_the_gun_in_every_hardpoint()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, Hull(1).HullId, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(288);

        var parts = m.ChosenParts;
        Assert.True(parts.Count > 0);
        // Three hardpoints → three PW Gat Gun (33) instances.
        var guns = parts.Where(p => p.PartId == 33).ToList();
        Assert.Equal(3, guns.Count);
        Assert.Equal(new byte[] { 0, 1, 2 }, guns.Select(g => g.MountId).OrderBy(x => x).ToArray());
        // The single-socket defaults are present exactly once.
        Assert.Contains(parts, p => p.PartId == 174);   // Hvy Booster (Afterburner)
        Assert.Contains(parts, p => p.PartId == 80);    // Counter (Chaff)
        Assert.Contains(parts, p => p.PartId == 13);    // Mine Pack (Dispenser)
        Assert.Contains(parts, p => p.PartId == 21);    // Sm Shield
        Assert.Contains(parts, p => p.PartId == 150);   // SRM Dumbfire (Magazine)
        foreach (var p in parts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && Hull(288).CanMountPart(pp), $"part {p.PartId} must mount hull 288");
        }
    }

    /// <summary>⭐ P571 — a bomber's new hull also gets its default loadout
    /// (N>0), and preferred parts that DON'T mount on it (native CanMount
    /// gate — e.g. the gunship's Prox Mine under P555 masks) are never sent.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Selecting_bomber_carries_only_mountable_preferred_parts()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, Hull(1).HullId, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(124); // Bomber

        var parts = m.ChosenParts;
        Assert.True(parts.Count > 0, "a new bomber hull must carry its default loadout");
        Assert.Contains(parts, p => p.PartId == 22);    // Med Shield
        Assert.Contains(parts, p => p.PartId == 80);    // Counter
        foreach (var p in parts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && Hull(124).CanMountPart(pp), $"default part {p.PartId} must mount hull 124");
            Assert.True(Hull(124).PreferredPartsTypes.Contains(p.PartId), $"default part {p.PartId} must be a preferred part");
        }
    }

    /// <summary>⭐ P571 — preferred parts that do NOT mount the hull are skipped
    /// (native CanMount gate), never shipped. The Gunship (290) lists Prox Mine
    /// (59) and Tower AC Drone (168) as preferred but they don't mount it (P555
    /// masks) — neither appears in the new-hull loadout.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_mountable_preferred_parts_are_skipped()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, Hull(1).HullId, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(290); // Gunship

        Assert.DoesNotContain(m.ChosenParts, p => p.PartId == 59);   // Prox Mine
        Assert.DoesNotContain(m.ChosenParts, p => p.PartId == 168);  // Tower AC Drone
        Assert.Contains(m.ChosenParts, p => p.PartId == 22);         // Med Shield still there
        foreach (var p in m.ChosenParts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && Hull(290).CanMountPart(pp), $"part {p.PartId} must mount hull 290");
        }
    }

    /// <summary>⭐ P571 — sending a launch carries the default loadout: after
    /// selecting Adv Scout, shop.ChosenParts (what SendBuyLoadout transmits) is
    /// exactly the hull's mountable preferred parts — never "+0 parts".</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Launch_transmits_the_default_loadout_not_zero_parts()
    {
        Require.Artwork();
        var m = OpenAdvScout();
        Assert.True(m.TryLaunch());
        Assert.True(m.ChosenParts.Count > 0);
        // The launcher default (MRM Seeker 1) carries its real ammo reserve.
        var mrm = m.ChosenParts.First(p => p.PartId == 153);
        Assert.Equal(LauncherAmmo(153), mrm.Amount);
        Assert.True(mrm.Amount > 0, "a default launcher must carry real ammo");
    }

    /// <summary>⭐ P571 — under allTech (testUnlock) the shop's FILTERED
    /// catalogue culls entry-tier preferred parts (gat gun, sm shield, ...) as
    /// obsolete, so a new hull's default loadout must resolve each preferred
    /// part to its highest OFFERED successor (native TryToBuyParts →
    /// pstation->GetSuccessor, clintlib.h:1357 / stationIGC.h:399-435) — the
    /// default is NEVER spuriously empty. This mirrors exactly how the real
    /// client builds its shop (FilteredFor) on the allTech live-proof map.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Filtered_all_tech_hull_swap_yields_non_empty_default()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, ShopCatalog.CoreFileName)));
        var gar = core.StationTypes.Values.First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
        var allTech = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        var filt = Catalog.FilteredFor(gar.TtbmLocal, allTech);

        // Bare docked ship (hull 92, Lxy Scout, empty loadout) → pick hull 299
        // (Lxy Hvy Bomber) in the shop, exactly the real-client sequence.
        var m = new ShopModel(filt, 92, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(299);
        Assert.Equal((short)299, m.EffectiveHullId);
        Assert.True(m.WillSwapHull);

        Assert.True(m.ChosenParts.Count > 0, "an all-tech hull swap must resolve a non-empty default loadout, never 0");
        foreach (var p in m.ChosenParts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && Hull(299).CanMountPart(pp), $"default part {p.PartId} must mount hull 299");
        }
    }

    /// <summary>⭐ P571 — launching an UNCHANGED hull still keeps its current
    /// parts (the shop opens pre-seeded from the docked ship; no hull picked
    /// ⇒ no rebase).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unchanged_hull_keeps_current_parts()
    {
        Require.Artwork();
        var current = new[]
        {
            new ExpandedPartData(33, 20, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(21, 1, 0, ExpandedPartData.FullyMounted),
        };
        var m = new ShopModel(Catalog, Hull(285).HullId, current);
        Assert.Null(m.ChosenHullId);
        Assert.Equal(2, m.ChosenParts.Count);
        Assert.Equal(current.Select(p => p.PartId).OrderBy(x => x),
            m.ChosenParts.Select(p => p.PartId).OrderBy(x => x));
    }
}
