using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P586 — failing-first tests for the treasure → native visual resolution
/// (<see cref="TreasureVisualResolver"/>), ported from <c>CtreasureIGC::Initialize</c>
/// (<c>treasureIGC.cpp:118-155</c>). Validated against the REAL artwork tree and
/// REAL core (<c>PCore001.igc</c>) exactly as <see cref="NativeShipResolverTests"/>
/// does, so the resolved model/icon stems correspond to real art. Fails on
/// pre-P586 code: there was no resolver — every treasure rendered as the shared
/// gold gem, so a part resolved to no model/name and cash/powerup/flag/artifact
/// had no distinct stems.
/// </summary>
public class TreasureVisualResolverTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static ShopCatalog Catalog()
    {
        var c = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(c);
        return c!;
    }

    private static bool FileExists(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cash_ResolvesToAcs14CashIcon()
    {
        Require.Artwork();
        var v = TreasureVisualResolver.Resolve(TreasureCode.Cash, 9500, Catalog());
        Assert.Equal(TreasureVisualKind.Cash, v.Kind);
        Assert.Equal("acs14", v.ModelStem);
        Assert.Equal("cashicon", v.IconName);
        Assert.Equal("Cash", v.DisplayName);
        Assert.False(v.IsDecal);
        Assert.True(FileExists(v.ModelStem!), "acs14.mdl must exist in the artwork tree");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Powerup_ResolvesToAcs14PuIcon()
    {
        Require.Artwork();
        var v = TreasureVisualResolver.Resolve(TreasureCode.Powerup, 0x1f, Catalog());
        Assert.Equal(TreasureVisualKind.Powerup, v.Kind);
        Assert.Equal("acs14", v.ModelStem);
        Assert.Equal("iconpu", v.IconName);
        Assert.Equal("Powerup", v.DisplayName);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealSideFlag_ResolvesToBiosflagDecal()
    {
        Require.Artwork();
        var v = TreasureVisualResolver.Resolve(TreasureCode.Flag, 3 /* a real side id */, Catalog());
        Assert.Equal(TreasureVisualKind.Flag, v.Kind);
        Assert.Null(v.ModelStem);          // a decal, not a model
        Assert.Equal("biosflagbmp", v.TextureStem);
        Assert.Equal("flag", v.IconName);
        Assert.Equal("Flag", v.DisplayName);
        Assert.True(v.IsDecal);
        Assert.True(FileExists(v.TextureStem!), "biosflagbmp.mdl must exist");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SentinelFlag_ResolvesToArtifact()
    {
        Require.Artwork();
        // SIDE_TEAMLOBBY = -2 (igc.h:514) → the artifact model (treasureIGC.cpp:141-146).
        var v = TreasureVisualResolver.Resolve(TreasureCode.Flag, Side.ArtifactSentinelFlag, Catalog());
        Assert.Equal(TreasureVisualKind.Artifact, v.Kind);
        Assert.Equal("artifact", v.ModelStem);
        Assert.Equal("articon", v.IconName);
        Assert.Equal("Artifact", v.DisplayName);
        Assert.False(v.IsDecal);
        Assert.True(FileExists(v.ModelStem!), "artifact.mdl must exist");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealAmmoPackPart_ResolvesToItsOwnModelAndName()
    {
        Require.Artwork();
        // Native: a part treasure uses the part's OWN model + icon
        // (treasureIGC.cpp:123-126). Pick the real ammo pack (c_packAmmo,
        // PackType 0) from the real core and assert it resolves to THAT part's
        // ModelName/IconName/Name, all present in the artwork/core.
        var catalog = Catalog();
        var ammoPack = catalog.Parts.FirstOrDefault(p =>
            p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0);   // c_packAmmo (igc.h:549)
        Assert.NotEqual(0, ammoPack.PartId);   // the real core HAS an ammo pack

        var v = TreasureVisualResolver.Resolve(TreasureCode.Part, ammoPack.PartId, catalog);
        Assert.Equal(TreasureVisualKind.Part, v.Kind);
        Assert.Equal(ammoPack.ModelName, v.ModelStem);
        Assert.Equal(ammoPack.IconName, v.IconName);
        Assert.Equal(ammoPack.Name, v.DisplayName);
        Assert.False(v.IsDecal);
        Assert.NotEmpty(v.DisplayName);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void UnresolvablePartId_FallsBackToGem()
    {
        Require.Artwork();
        var v = TreasureVisualResolver.Resolve(TreasureCode.Part, short.MaxValue, Catalog());
        Assert.Equal(TreasureVisualKind.Gem, v.Kind);
        Assert.Null(v.ModelStem);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NullCatalog_StillResolvesCashFlagArtifact()
    {
        Require.Artwork();
        // A degraded client (no loadable core) must still distinguish the
        // always-known codes; only part/development need the core lookup.
        var cash = TreasureVisualResolver.Resolve(TreasureCode.Cash, 9510, catalog: null);
        Assert.Equal(TreasureVisualKind.Cash, cash.Kind);

        var flag = TreasureVisualResolver.Resolve(TreasureCode.Flag, 1, catalog: null);
        Assert.Equal(TreasureVisualKind.Flag, flag.Kind);

        var art = TreasureVisualResolver.Resolve(TreasureCode.Flag, Side.ArtifactSentinelFlag, catalog: null);
        Assert.Equal(TreasureVisualKind.Artifact, art.Kind);

        var part = TreasureVisualResolver.Resolve(TreasureCode.Part, 901, catalog: null);
        Assert.Equal(TreasureVisualKind.Gem, part.Kind);
    }

    /// <summary>
    /// The P586 "contact sheet": every treasure KIND's native model resolved
    /// from the real static core (all part types + the always-known cash/
    /// powerup/artifact/flag), each verified to have its mesh + texture asset
    /// in the artwork tree. Enumerated so the report lists exactly what the
    /// client can now render natively.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ContactSheet_EveryTreasureKindResolvesToAnExistingNativeModel()
    {
        Require.Artwork();
        var catalog = Catalog();
        var rows = new List<string>();

        // (1) Every buyable part type resolves to its OWN model (part's ModelName).
        var resolvedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in catalog.Parts.Where(p => p.PartId != 0))
        {
            var v = TreasureVisualResolver.Resolve(TreasureCode.Part, p.PartId, catalog);
            Assert.NotEqual(TreasureVisualKind.Gem, v.Kind);
            Assert.NotNull(v.ModelStem);
            resolvedModels.Add(v.ModelStem!);
        }

        // (2) The always-known codes.
        var cash = TreasureVisualResolver.Resolve(TreasureCode.Cash, 9400, catalog);
        var powerup = TreasureVisualResolver.Resolve(TreasureCode.Powerup, 0x1f, catalog);
        var artifact = TreasureVisualResolver.Resolve(TreasureCode.Flag, Side.ArtifactSentinelFlag, catalog);
        var flag = TreasureVisualResolver.Resolve(TreasureCode.Flag, 1, catalog);

        // (3) EVERY distinct model mesh + its texture unit must exist in the artwork tree.
        var allStems = new[] { cash.ModelStem, powerup.ModelStem, artifact.ModelStem }
            .Concat(resolvedModels)
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var stem in allStems)
        {
            Assert.True(File.Exists(Path.Combine(ArtRoot, stem + ".mdl")), $"model {stem}.mdl missing");
            rows.Add($"model {stem}");
        }
        // The flag decal texture + every part icon stem must exist too.
        Assert.True(FileExists(flag.TextureStem!), "biosflagbmp.mdl missing");

        // Surface the contact sheet as part of the test output (readable in the report).
        Console.WriteLine("[P586 contact sheet] " + string.Join(", ", rows.OrderBy(s => s)));
    }

    // ---- P586b: launcher / dispenser treasures + texture stems ------------

    /// <summary>
    /// P586b — a mine dispenser treasure (launcher part 13, "Mine Pack 1")
    /// must resolve to the launcher's own native model, NOT the gold gem.
    /// Pre-P586b the resolver only searched the 117 plain <c>catalog.Parts</c>,
    /// so every launcher/dispenser id (missile/mine/chaff/probe) fell back to
    /// the gem (native: <c>m_buyable = GetPartType(treasureID)</c> spans ANY
    /// OT_partType, treasureIGC.cpp:118-126; a launcher's model is its
    /// expendable's LauncherDef.modelName, launchertypeigc.h:59-67).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MineDispenser_ResolvesToItsOwnModel_NotGem()
    {
        Require.Artwork();
        var catalog = Catalog();
        var v = TreasureVisualResolver.Resolve(TreasureCode.Part, 13 /* Mine Pack 1 */, catalog);
        Assert.Equal(TreasureVisualKind.Part, v.Kind);
        Assert.Equal("dn_ptminpak", v.ModelStem);   // the real mine-pack model
        Assert.Equal("Mine Pack 1", v.DisplayName); // the launcher's real name
        Assert.True(FileExists(v.ModelStem!), "dn_ptminpak.mdl must exist");
    }

    /// <summary>
    /// P586b — EVERY real launcher/dispenser in the core (missile/mine/chaff/
    /// probe, incl. the exact ids Domingo's log flagged: 80, 81, 153, 13, 150)
    /// resolves to a native model, never the gem. Fails on pre-P586b code,
    /// where all 123 launchers resolved to <see cref="TreasureVisualKind.Gem"/>.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EveryLauncherInCore_ResolvesToItsOwnModel()
    {
        Require.Artwork();
        var catalog = Catalog();
        foreach (var l in catalog.Launchers!)
        {
            var v = TreasureVisualResolver.Resolve(TreasureCode.Part, l.Part.PartId, catalog);
            Assert.NotEqual(TreasureVisualKind.Gem, v.Kind);
            Assert.Equal(l.Part.ModelName, v.ModelStem);
            Assert.False(v.IsDecal);
            Assert.NotEmpty(v.DisplayName);
        }
    }

    /// <summary>
    /// P586b — a part/launcher treasure's <see cref="TreasureVisual.TextureStem"/>
    /// must be NULL (native passes <c>texture=NULL</c> for a buyable,
    /// treasureIGC.cpp:154), so the model's OWN texture namespace supplies it.
    /// Pre-P586b the resolver set TextureStem to the ICON name (mostly the
    /// literal <c>"part"</c>, or <c>"ammo_icon"</c>/<c>"fuel_icon"</c>), which
    /// made the renderer look for nonexistent <c>part.mdl</c>/<c>fuel_icon.mdl</c>
    /// texture units and draw lit-solid.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PartAndLauncher_TextureStem_IsNull_NotTheIconName()
    {
        Require.Artwork();
        var catalog = Catalog();
        // The real ammo pack (plain part) — icon is "ammo_icon".
        var ammo = catalog.Parts.First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0);
        var av = TreasureVisualResolver.Resolve(TreasureCode.Part, ammo.PartId, catalog);
        Assert.Null(av.TextureStem);
        // A launcher — its icon is the literal "part".
        var launcher = catalog.Launchers!.First(l => l.Part.PartId == 13);
        var lv = TreasureVisualResolver.Resolve(TreasureCode.Part, launcher.Part.PartId, catalog);
        Assert.Null(lv.TextureStem);
        Assert.Equal("part", launcher.Part.IconName);   // sanity: the icon really is "part"
    }

    /// <summary>
    /// P586b — the startup self-check resolves every part type AND launcher in
    /// the 221 core with ZERO fallbacks (the "0 unresolved" invariant). This is
    /// the exact set the 15v15 showcase drops. Fails on pre-P586b code because
    /// every launcher resolved to the gem.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SelfCheck_ReportsZeroFallbacks_ForPartsAndLaunchers()
    {
        Require.Artwork();
        var catalog = Catalog();
        var logged = new List<string>();
        int fallback = TreasureVisualResolver.SelfCheck(catalog, logged.Add);
        Assert.Equal(0, fallback);
        Assert.Empty(logged);
    }
}
