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
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork",
        Environment.GetEnvironmentVariable("ALLEGIANCE_NATIVE_SRC_PATH") ?? "/home/domingo/Development/Allegiance",
    ];

    public const int ExpectedUniqueFiles = 210;
    public const int ExpectedStaticCores = 99;
    public const int ExpectedMaps = 111;

    private static readonly Lazy<IReadOnlyList<string>> Cached = new(Discover);

    public static IReadOnlyList<string> Files => Cached.Value;

    private static IReadOnlyList<string> Discover()
    {
        if (!Roots.Any(Directory.Exists))
            throw new DirectoryNotFoundException(
                $"no artwork corpus found -- checked {string.Join(", ", Roots)}. " +
                "Set ALLEGIANCE_ARTWORK_PATH and ALLEGIANCE_NATIVE_SRC_PATH to override.");

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

    public static TheoryData<string> Theory()
    {
        var d = new TheoryData<string>();
        foreach (var f in Files) d.Add(f);
        return d;
    }
}
