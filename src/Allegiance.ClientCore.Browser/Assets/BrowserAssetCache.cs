using Microsoft.JSInterop;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Only asynchronous interop crosses the cache boundary; StagedAssetSource has no JS dependency.</summary>
public sealed class BrowserAssetCache(IJSObjectReference platform, Dictionary<string, byte[]>? memoryFallback = null) : IImmutableAssetCache
{
    private readonly Dictionary<string, byte[]> _memory = memoryFallback ?? new(StringComparer.Ordinal);
    public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken)
        => _memory.TryGetValue(hash, out var bytes) ? bytes : await platform.InvokeAsync<byte[]?>("cacheRead", cancellationToken, hash);
    public async ValueTask RemoveAsync(string hash, CancellationToken cancellationToken)
    { _memory.Remove(hash); await platform.InvokeVoidAsync("cacheRemove", cancellationToken, hash); }
    public async ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken cancellationToken)
    {
        bool persisted = await platform.InvokeAsync<bool>("cachePut", cancellationToken, asset.Sha256, bytes, asset.ContentType);
        if (!persisted)
        {
            _memory.Remove(asset.Sha256);
            long total = _memory.Values.Sum(value => (long)value.Length);
            while (_memory.Count > 0 && (_memory.Count >= 1024 || total + bytes.Length > StagedManifest.MaximumPackBytes))
            {
                string oldest = _memory.Keys.First(); total -= _memory[oldest].Length; _memory.Remove(oldest);
            }
            _memory[asset.Sha256] = bytes;
        }
        return persisted;
    }
    public async ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken cancellationToken)
    {
        var retained = hashes.ToHashSet(StringComparer.Ordinal);
        foreach (string hash in _memory.Keys.Where(hash => !retained.Contains(hash)).ToArray()) _memory.Remove(hash);
        await platform.InvokeVoidAsync("cacheCommit", cancellationToken, identity, hashes);
    }
}
