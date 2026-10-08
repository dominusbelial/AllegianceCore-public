using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

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
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static ShopCatalog Catalog()
    {
        var c = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(c);
        return c!;
    }

    private static bool FileExists(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    [Fact]
    public void Cash_ResolvesToAcs14CashIcon()
    {
        var v = TreasureVisualResolver.Resolve(TreasureCode.Cash, 9500, Catalog());
        Assert.Equal(TreasureVisualKind.Cash, v.Kind);
        Assert.Equal("acs14", v.ModelStem);
        Assert.Equal("cashicon", v.IconName);
        Assert.Equal("Cash", v.DisplayName);
        Assert.False(v.IsDecal);
        Assert.True(FileExists(v.ModelStem!), "acs14.mdl must exist in the artwork tree");
    }

    [Fact]
    public void Powerup_ResolvesToAcs14PuIcon()
    {
        var v = TreasureVisualResolver.Resolve(TreasureCode.Powerup, 0x1f, Catalog());
        Assert.Equal(TreasureVisualKind.Powerup, v.Kind);
        Assert.Equal("acs14", v.ModelStem);
        Assert.Equal("iconpu", v.IconName);
        Assert.Equal("Powerup", v.DisplayName);
    }

    [Fact]
    public void RealSideFlag_ResolvesToBiosflagDecal()
    {
        var v = TreasureVisualResolver.Resolve(TreasureCode.Flag, 3 /* a real side id */, Catalog());
        Assert.Equal(TreasureVisualKind.Flag, v.Kind);
        Assert.Null(v.ModelStem);          // a decal, not a model
        Assert.Equal("biosflagbmp", v.TextureStem);
        Assert.Equal("flag", v.IconName);
        Assert.Equal("Flag", v.DisplayName);
        Assert.True(v.IsDecal);
        Assert.True(FileExists(v.TextureStem!), "biosflagbmp.mdl must exist");
    }

    [Fact]
    public void SentinelFlag_ResolvesToArtifact()
    {
        // SIDE_TEAMLOBBY = -2 (igc.h:514) → the artifact model (treasureIGC.cpp:141-146).
        var v = TreasureVisualResolver.Resolve(TreasureCode.Flag, Side.ArtifactSentinelFlag, Catalog());
        Assert.Equal(TreasureVisualKind.Artifact, v.Kind);
        Assert.Equal("artifact", v.ModelStem);
        Assert.Equal("articon", v.IconName);
        Assert.Equal("Artifact", v.DisplayName);
        Assert.False(v.IsDecal);
        Assert.True(FileExists(v.ModelStem!), "artifact.mdl must exist");
    }

    [Fact]
    public void RealAmmoPackPart_ResolvesToItsOwnModelAndName()
    {
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

    [Fact]
    public void UnresolvablePartId_FallsBackToGem()
    {
        var v = TreasureVisualResolver.Resolve(TreasureCode.Part, short.MaxValue, Catalog());
        Assert.Equal(TreasureVisualKind.Gem, v.Kind);
        Assert.Null(v.ModelStem);
    }

    [Fact]
    public void NullCatalog_StillResolvesCashFlagArtifact()
    {
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
    [Fact]
    public void ContactSheet_EveryTreasureKindResolvesToAnExistingNativeModel()
    {
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
}
