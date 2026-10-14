using System.Security.Cryptography;

namespace Allegiance.Core.Tests;

/// <summary>
/// Locates the shipped <c>.igc</c> corpus.
/// </summary>
/// <remarks>
/// The corpus is the public FreeAllegiance <c>Artwork</c> checkout
/// (<c>ALLEGIANCE_ARTWORK_PATH</c>) — what every contributor can clone — so the
/// baselines below hold on any machine. Its <c>.igc</c> files de-duplicate to
/// <b>209 unique</b> by content (98 static cores + 111 maps).
/// The native C++ repo carries one more core, <c>artwork/tester.igc</c>; it used to be
/// folded in, which made the totals true only on the developer's machine (a clean-room
/// install failed 11 corpus tests). It now has its own NativeSource-gated test
/// (<see cref="NativeTesterCoreTests"/>).
/// </remarks>
public static class Corpus
{
    private static readonly string[] Roots =
    [
        ExternalPaths.Resolve(ExternalResourceKind.Artwork),
    ];

    public const int ExpectedUniqueFiles = 209;
    public const int ExpectedStaticCores = 98;
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
