using Allegiance.ClientCore.MonoGame.Assets;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Verified base streams and declared visual names. Deferred names use
/// the prepared-texture hook; their bytes never trigger IO in Draw.</summary>
public sealed class StagedAssetSource : IAssetSource, IResidentTextureSource
{
    private readonly Dictionary<string, byte[]> _files;
    internal StagedAssetSource(Dictionary<string, byte[]> files) => _files = files;
    public IReadOnlySet<string>? StreamedSources { get; set; }
    public long TextureRevision { get; set; }
    public long ContentRevision { get; private set; }
    public Func<string, bool, (bool Handled, Microsoft.Xna.Framework.Graphics.Texture2D? Texture)>? TextureLookup { get; set; }
    public bool TryGetTexture(string stem, bool colorKey, out Microsoft.Xna.Framework.Graphics.Texture2D? texture)
    {
        var result = TextureLookup?.Invoke(stem, colorKey) ?? (false, null);
        texture = result.Item2; return result.Item1;
    }
    public bool Contains(string relativePath) => _files.ContainsKey(StagedManifest.Canonical(relativePath));
    public bool Exists(string relativePath) => StreamedSources?.Contains(relativePath) == true || Contains(relativePath);
    public byte[] Bytes(string relativePath) => _files[StagedManifest.Canonical(relativePath)];
    public Stream OpenRead(string relativePath) => _files.TryGetValue(StagedManifest.Canonical(relativePath), out var bytes)
        ? new MemoryStream(bytes, writable: false)
        : throw new AssetNotResidentException(relativePath);
    public IEnumerable<string> Enumerate(string directoryRelativePath)
    {
        string prefix = directoryRelativePath.Length == 0 ? "" : StagedManifest.Canonical(directoryRelativePath.TrimEnd('/')) + "/";
        return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/')).ToArray();
    }
    /// <summary>Promote a completely verified pack on the event loop before joining.</summary>
    public void Merge(StagedAssetSource additional)
    {
        foreach (var (name, bytes) in additional._files) _files[name] = bytes;
        ContentRevision++;
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
