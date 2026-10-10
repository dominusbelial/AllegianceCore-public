using System.Security.Cryptography;

namespace Allegiance.Core.Tests;

/// <summary>
/// Locates the shipped <c>.cvh</c> corpus (mirrors <see cref="Corpus"/> for
/// the collision-hull files). Same two-tree de-duplication by content hash.
/// </summary>
/// <remarks>
/// 627 files on disk across the two trees de-duplicate to <b>564 unique</b> by
/// content (measured 2026-08-28). Of those, 414 carry a trailing frame section
/// and 150 do not.
/// </remarks>
public static class CvhCorpus
{
    private static readonly string[] Roots =
    [
        ExternalPaths.Resolve(ExternalResourceKind.Artwork),
        ExternalPaths.Resolve(ExternalResourceKind.NativeSource),
    ];

    public const int ExpectedUniqueFiles = 564;
    public const int ExpectedFramedFiles = 414;

    /// <summary>True when at least one cvh corpus root exists on disk.</summary>
    public static bool Available => Roots.Any(Directory.Exists);

    private static readonly Lazy<IReadOnlyList<string>> Cached = new(Discover);

    public static IReadOnlyList<string> Files => Cached.Value;

    private static IReadOnlyList<string> Discover()
    {
        // No corpus on this host (clean clone) — return empty; tests guard with
        // Require.Artwork() / the sentinel Theory() row rather than throw here.
        if (!Roots.Any(Directory.Exists))
            return [];

        var byHash = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var p in Directory.EnumerateFiles(root, "*.cvh", SearchOption.AllDirectories))
            {
                if (p.Contains("/.git/", StringComparison.Ordinal)) continue;
                var hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p)));
                if (!byHash.TryGetValue(hash, out var kept) || string.CompareOrdinal(p, kept) < 0)
                    byHash[hash] = p;
            }
        }
        return byHash.Values.ToList();
    }

    public static TheoryData<string> Theory()
    {
        var d = new TheoryData<string>();
        foreach (var f in Files) d.Add(f);
        return d;
    }

    /// <summary>Member-data paths for <c>[SkippableTheory]</c> tests. Yields a
    /// single sentinel row when the cvh corpus is absent (clean clone) so a test
    /// whose body starts with <c>Require.Artwork()</c> reports a visible skip
    /// rather than a parameter-mismatch failure from an empty dataset.</summary>
    public static IEnumerable<object[]> Paths()
    {
        if (!Available) return new object[][] { new object[] { "" } };
        return Files.Select(p => new object[] { p });
    }
}
