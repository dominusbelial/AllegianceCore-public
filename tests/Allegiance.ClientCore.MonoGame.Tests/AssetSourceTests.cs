using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P498 — the asset-source abstraction. Verifies <see cref="DirectoryAssetSource"/>
/// (desktop on-disk behaviour), <see cref="RecordingAssetSource"/> (the manifest
/// recorder), and that the real client loaders route their artwork IO through
/// <see cref="IAssetSource"/> with behaviour identical to the legacy string-path
/// path (regression guard: desktop must behave identically afterwards).
/// </summary>
public class AssetSourceTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "p498-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void DirectoryAssetSource_RoundTripsExistsOpenReadEnumerate()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.bin"), "hello");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "b.mdl"), "world");

            var src = new DirectoryAssetSource(dir);

            Assert.True(src.Exists("a.bin"));
            Assert.False(src.Exists("missing"));
            Assert.True(src.Exists("sub/b.mdl"));

            using (var r = new StreamReader(src.OpenRead("a.bin")))
                Assert.Equal("hello", r.ReadToEnd());

            var files = src.Enumerate(".").OrderBy(f => f, System.StringComparer.Ordinal).ToArray();
            Assert.Equal(["a.bin"], files);                    // direct children only
            Assert.Contains("sub/b.mdl", src.Enumerate("sub"));

            Assert.Empty(src.Enumerate("nosuchdir"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DirectoryAssetSource_RejectsEscapingRelativePaths()
    {
        string dir = TempDir();
        try
        {
            var src = new DirectoryAssetSource(dir);
            Assert.Throws<ArgumentException>(() => src.OpenRead("../secret.txt"));
            Assert.Throws<ArgumentException>(() => src.Exists("../secret.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RecordingAssetSource_RecordsOpenedFilesAndBytes()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.mdl"), "abcdefghij");           // 10 bytes
            File.WriteAllText(Path.Combine(dir, "y.cvh"), "12345");                // 5 bytes

            var inner = new DirectoryAssetSource(dir);
            var rec = new RecordingAssetSource(inner);

            Assert.False(rec.Exists("missing"));          // probed (recorded) but never opened
            using (var s = rec.OpenRead("x.mdl")) s.CopyTo(Stream.Null);
            using (var s = rec.OpenRead("y.cvh")) s.CopyTo(Stream.Null);
            using (var s = rec.OpenRead("x.mdl")) s.CopyTo(Stream.Null);   // reopened

            Assert.Equal(2, rec.OpenedCount);
            Assert.Equal(10 + 5 + 10, rec.TotalBytes);
            Assert.Equal(10 + 10, rec.OpenedBytes["x.mdl"]);
            Assert.Equal(5, rec.OpenedBytes["y.cvh"]);
            Assert.Contains("missing", rec.ProbedOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RecordingAssetSource_ManifestJson_ReportsTotals()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "only.mdl"), "12345");
            var rec = new RecordingAssetSource(new DirectoryAssetSource(dir));
            using (var s = rec.OpenRead("only.mdl")) s.CopyTo(Stream.Null);

            string json = rec.ToManifestJson(dir);
            Assert.Contains("\"only.mdl\"", json);
            Assert.Contains("\"openedCount\": 1", json);
            Assert.Contains("\"totalBytes\": 5", json);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void ShopCatalog_RoutesThroughIAssetSource_SameResultAsLegacyPath()
    {
        // Regression: the desktop shop catalogue must be identical whether loaded
        // through the legacy string artwork dir or the new IAssetSource.
        Require.Artwork();
        string root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

        var viaSource = ShopCatalog.TryLoad(new DirectoryAssetSource(root));
        var viaLegacy = ShopCatalog.TryLoad(root);

        Assert.NotNull(viaSource);
        Assert.Equal(viaLegacy!.Hulls.Select(h => h.HullId), viaSource.Hulls.Select(h => h.HullId));
        Assert.Equal(viaLegacy.Parts.Select(p => p.PartId), viaSource.Parts.Select(p => p.PartId));
    }
}
