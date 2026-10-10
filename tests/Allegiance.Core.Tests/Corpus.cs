using System.Security.Cryptography;

namespace Allegiance.Core.Tests;

/// <summary>
/// Locates the shipped <c>.igc</c> corpus.
/// </summary>
/// <remarks>
/// The artwork lives in <b>two</b> trees: the code repo carries only a handful
/// of assets, the bulk sit in a separate <c>Artwork</c> checkout. A search
/// scoped to one tree silently finds far too few files. 217 files on disk
/// de-duplicate to <b>210 unique</b> by content.
/// </remarks>
public static class Corpus
{
    private static readonly string[] Roots =
    [
        ExternalPaths.Resolve(ExternalResourceKind.Artwork),
        ExternalPaths.Resolve(ExternalResourceKind.NativeSource),
    ];

    public const int ExpectedUniqueFiles = 210;
    public const int ExpectedStaticCores = 99;
    public const int ExpectedMaps = 111;

    /// <summary>True when at least one corpus root exists on disk. Corpus-backed
    /// tests use <c>Require.Artwork()</c> to skip (not fail) when false.</summary>
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
            foreach (var p in Directory.EnumerateFiles(root, "*.igc", SearchOption.AllDirectories))
            {
                if (p.Contains("/.git/", StringComparison.Ordinal)) continue;
                var hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p)));
                // Deduplicate by content, not filename; keep a stable representative.
                if (!byHash.TryGetValue(hash, out var kept) || string.CompareOrdinal(p, kept) < 0)
                    byHash[hash] = p;
            }
        }
        return byHash.Values.ToList();
    }

    /// <summary>Theory data over the whole corpus. When the corpus is absent
    /// (clean clone) it yields a single sentinel row so a <c>[SkippableTheory]</c>
    /// whose body starts with <c>Require.Artwork()</c> reports a visible skip
    /// rather than enumerating nothing.</summary>
    public static TheoryData<string> Theory()
    {
        var d = new TheoryData<string>();
        if (!Available) { d.Add(""); return d; }
        foreach (var f in Files) d.Add(f);
        return d;
    }
}
