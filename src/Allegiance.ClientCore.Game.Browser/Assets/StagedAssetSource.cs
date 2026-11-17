using Allegiance.ClientCore.MonoGame.Assets;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>A complete, verified residency snapshot. Never performs disk, HTTP or JS interop.</summary>
public sealed class StagedAssetSource : IAssetSource
{
    private readonly Dictionary<string, byte[]> _files;
    internal StagedAssetSource(Dictionary<string, byte[]> files) => _files = files;
    public bool Exists(string relativePath) => _files.ContainsKey(StagedManifest.Canonical(relativePath));
    public Stream OpenRead(string relativePath) => _files.TryGetValue(StagedManifest.Canonical(relativePath), out var bytes)
        ? new MemoryStream(bytes, writable: false)
        : throw new AssetNotResidentException(relativePath);
    public IEnumerable<string> Enumerate(string directoryRelativePath)
    {
        string prefix = directoryRelativePath.Length == 0 ? "" : StagedManifest.Canonical(directoryRelativePath.TrimEnd('/')) + "/";
        return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/')).ToArray();
    }
    public long ResidentBytes => _files.Values.Sum(b => (long)b.Length);
}

public sealed class AssetNotResidentException(string path) : IOException($"Asset is not resident: {path}");

/// <summary>The cache may discard bytes at any time; residency is independent of persistence.</summary>
public interface IImmutableAssetCache
{
    ValueTask<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken);
    ValueTask RemoveAsync(string hash, CancellationToken cancellationToken);
    /// <returns>False on quota/storage refusal; verified resident bytes remain usable.</returns>
    ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken cancellationToken);
    ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken cancellationToken);
}
