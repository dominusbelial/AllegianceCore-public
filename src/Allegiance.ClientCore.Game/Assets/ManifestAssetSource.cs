using System.Collections.Concurrent;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>Rejects artwork opens outside the reviewed player manifest. Probes
/// still reach the underlying source so missing optional assets keep their fallback.
/// Names match case-insensitively and open the manifest's canonical spelling: native
/// Allegiance ran on Windows, so its cores and maps mix cases (training_1.igc asks for
/// "alephbmp" while the file is Alephbmp.mdl), which a case-sensitive Linux/macOS
/// file system would otherwise reject.</summary>
public sealed class ManifestAssetSource(IAssetSource inner, IEnumerable<string> paths) : IAssetSource
{
    private readonly Dictionary<string, string> _canonical = BuildCanonical(paths);
    private readonly ConcurrentDictionary<string, byte> _violations = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Violations => _violations.Keys.ToArray();
    public static string[] ReadPaths(string path) => File.ReadLines(path)
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
    private static Dictionary<string, string> BuildCanonical(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) map.TryAdd(path, path);   // first spelling wins, like a case-insensitive file system
        return map;
    }
    public bool Exists(string relativePath) =>
        inner.Exists(_canonical.TryGetValue(relativePath, out var canonical) ? canonical : relativePath);
    public IEnumerable<string> Enumerate(string directoryRelativePath) => inner.Enumerate(directoryRelativePath);
    public Stream OpenRead(string relativePath)
    {
        if (!_canonical.TryGetValue(relativePath, out var canonical))
        {
            _violations.TryAdd(relativePath, 0);
            throw new InvalidDataException($"Artwork file is absent from artwork-manifest.txt: {relativePath}");
        }
        return inner.OpenRead(canonical);
    }
}
