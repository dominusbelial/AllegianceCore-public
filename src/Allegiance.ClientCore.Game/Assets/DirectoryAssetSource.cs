using System.IO;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// The DesktopGL <see cref="IAssetSource"/>: reads the native Artwork tree from
/// a filesystem directory. Preserves today's exact on-disk behaviour for the
/// desktop client (see <see cref="AssetSources.ResolveRoot"/> for the
/// <c>ALLEGIANCE_ARTWORK_PATH</c> default + fallback).
/// </summary>
public sealed class DirectoryAssetSource : IAssetSource
{
    private readonly string _root;

    /// <param name="root">The absolute directory to serve as the logical asset
    /// root (e.g. the resolved Artwork path).</param>
    public DirectoryAssetSource(string root)
    {
        _root = Path.GetFullPath(root);
    }

    /// <summary>The absolute filesystem root this source serves.</summary>
    public string Root => _root;

    public bool Exists(string relativePath) =>
        File.Exists(ToAbsolute(relativePath));

    public Stream OpenRead(string relativePath) =>
        File.OpenRead(ToAbsolute(relativePath));

    public IEnumerable<string> Enumerate(string directoryRelativePath)
    {
        string abs = ToAbsolute(directoryRelativePath);
        if (!Directory.Exists(abs)) yield break;
        foreach (var f in Directory.EnumerateFiles(abs))
            yield return ToRelative(f);
    }

    private string ToAbsolute(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("relative path must not be empty", nameof(relativePath));
        string rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string abs = Path.GetFullPath(Path.Combine(_root, rel));
        // Defence in depth: never let a relative path escape the root.
        if (!abs.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(abs, _root, StringComparison.Ordinal))
            throw new ArgumentException($"relative path escapes the asset root: {relativePath}", nameof(relativePath));
        return abs;
    }

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
