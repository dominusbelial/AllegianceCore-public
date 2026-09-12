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
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork",
        Environment.GetEnvironmentVariable("ALLEGIANCE_NATIVE_SRC_PATH") ?? "/home/domingo/Development/Allegiance",
    ];

    public const int ExpectedUniqueFiles = 564;
    public const int ExpectedFramedFiles = 414;

    private static readonly Lazy<IReadOnlyList<string>> Cached = new(Discover);

    public static IReadOnlyList<string> Files => Cached.Value;

    private static IReadOnlyList<string> Discover()
    {
        if (!Roots.Any(Directory.Exists))
            throw new DirectoryNotFoundException(
                $"no cvh corpus found -- checked {string.Join(", ", Roots)}. " +
                "Set ALLEGIANCE_ARTWORK_PATH and ALLEGIANCE_NATIVE_SRC_PATH to override.");

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
}
