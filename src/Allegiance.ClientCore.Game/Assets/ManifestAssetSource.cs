using System.Collections.Concurrent;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>Rejects artwork opens outside the reviewed player manifest. Probes
/// still reach the underlying source so missing optional assets keep their fallback.</summary>
public sealed class ManifestAssetSource(IAssetSource inner, IEnumerable<string> paths) : IAssetSource
{
    private readonly HashSet<string> _allowed = new(paths, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _violations = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Violations => _violations.Keys.ToArray();
    public static string[] ReadPaths(string path) => File.ReadLines(path)
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
    public bool Exists(string relativePath) => inner.Exists(relativePath);
    public IEnumerable<string> Enumerate(string directoryRelativePath) => inner.Enumerate(directoryRelativePath);
    public Stream OpenRead(string relativePath)
    {
        if (!_allowed.Contains(relativePath))
        {
            _violations.TryAdd(relativePath, 0);
            throw new InvalidDataException($"Artwork file is absent from artwork-manifest.txt: {relativePath}");
        }
        return inner.OpenRead(relativePath);
    }
}
