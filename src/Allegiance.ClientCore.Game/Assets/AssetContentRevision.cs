namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>Optional, append-only artwork delivery. Local desktop sources have no
/// revision; their caches retain their existing lifetime.</summary>
public interface IAssetContentRevision
{
    long ContentRevision { get; }
}

/// <summary>One invalidation boundary for negative lookups and file indexes.
/// Successful decoded/uploaded resources remain owned by their renderer.</summary>
public sealed class AssetContentRevision(IAssetSource assets)
{
    private long _seen = (assets as IAssetContentRevision)?.ContentRevision ?? 0;

    public bool Changed()
    {
        long current = (assets as IAssetContentRevision)?.ContentRevision ?? 0;
        if (current == _seen) return false;
        _seen = current;
        return true;
    }
}

/// <summary>A missing asset is attempted/logged once per content revision.</summary>
public sealed class AssetMissCache(IAssetSource assets)
{
    private readonly AssetContentRevision _revision = new(assets);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private void Refresh() { if (_revision.Changed()) _missing.Clear(); }
    public bool Contains(string stem) { Refresh(); return _missing.Contains(stem); }
    public bool Add(string stem) { Refresh(); return _missing.Add(stem); }
    public void Clear() => _missing.Clear();
}

/// <summary>Native Windows case folding over the artwork root, refreshed only
/// when verified content is promoted. Never keeps an early, incomplete index.</summary>
public sealed class AssetStemIndex
{
    private readonly IAssetSource _assets;
    private readonly AssetContentRevision _revision;
    private readonly Dictionary<string, string> _stems = new(StringComparer.OrdinalIgnoreCase);
    public AssetStemIndex(IAssetSource assets)
    {
        _assets = assets; _revision = new(assets); Rebuild();
    }
    private void Rebuild()
    {
        _stems.Clear();
        foreach (string path in _assets.Enumerate("."))
            if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                _stems.TryAdd(stem, stem);
            }
    }
    public bool TryGetValue(string stem, out string exact)
    {
        if (_revision.Changed()) Rebuild();
        return _stems.TryGetValue(stem, out exact!);
    }
    public bool ContainsKey(string stem) => TryGetValue(stem, out _);
}
