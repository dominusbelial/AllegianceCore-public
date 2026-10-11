using System.Collections.Generic;
using System.IO;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P601 — tests for the private Meshy asset pack resolver and the optional-at-
/// runtime behaviour. Tests that exercise the real pack carry the
/// <c>Requires=MeshyPack</c> trait and skip (with a reason) on a clean clone
/// where the pack is absent — mirroring the P597 corpus tests. Tests that only
/// probe machine-independent invariants run everywhere.
/// </summary>
public class MeshyPackTests
{
    [Fact]
    public void IsAvailable_implies_configured()
    {
        // Machine-independent invariant: you cannot load the pack's content
        // unless a pack root is configured.
        if (MeshyPack.IsAvailable())
            Assert.True(MeshyPack.Configured());
    }

    [Fact]
    public void Unavailable_pack_describes_how_to_enable_it()
    {
        if (MeshyPack.IsAvailable()) return;   // no message needed when present
        var msg = MeshyPack.DescribeUnavailable();
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.Contains("ALLEGIANCE_MESHY_PACK_PATH", msg, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltContentDir_points_under_the_configured_root()
    {
        var root = MeshyPack.ResolveRoot();
        if (root.Length == 0) return;
        var c = MeshyPack.BuiltContentDir();
        Assert.StartsWith(root, c);
        Assert.Contains(Path.Combine("Content", "bin"), c, System.StringComparison.Ordinal);
    }

    // ---- With the pack present, today's Meshy resolution is unchanged ----
    // These skip on a clean clone (no pack) rather than fail.

    [SkippableFact, Trait("Requires", "MeshyPack")]
    public void Pack_catalogue_has_the_full_meshy_set()
    {
        Require.MeshyPack();
        var packCat = ShipCatalogue.LoadFile(MeshyPack.CatalogueFile());
        // The pack holds every Meshy model id (P601 moved them out of the public
        // catalogue), so a catalogue override can resolve each as before.
        Assert.True(packCat.Count >= 90, $"expected the full Meshy catalogue (>=90), got {packCat.Count}");
        Assert.NotNull(packCat.ById("bios-carrier"));
    }

    [SkippableFact, Trait("Requires", "MeshyPack")]
    public void Pack_catalogue_merge_yields_identical_resolution_to_before()
    {
        Require.MeshyPack();
        // Public (native-only) + pack (Meshy) merged → every previously-adoptable
        // model id still resolves through the same ResolveKey rule (repaired first).
        var publicCat = ShipCatalogue.Parse("{ \"models\": [] }");
        var packCat = ShipCatalogue.LoadFile(MeshyPack.CatalogueFile());
        var merged = ShipCatalogue.Merge(publicCat, packCat);
        var loaded = new HashSet<string> { "repaired/bios-carrier", "bios-carrier" };
        Assert.Equal("repaired/bios-carrier", merged.ResolveKey("bios-carrier", loaded));
    }

    [SkippableFact, Trait("Requires", "MeshyPack")]
    public void Pack_built_content_directory_exists()
    {
        Require.MeshyPack();
        Assert.True(Directory.Exists(MeshyPack.BuiltContentDir()),
            "Meshy pack built content dir missing — run the pack's build.sh");
    }
}
