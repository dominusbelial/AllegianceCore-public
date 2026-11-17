using Microsoft.JSInterop;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Only asynchronous interop crosses the cache boundary; StagedAssetSource has no JS dependency.</summary>
public sealed class BrowserAssetCache(IJSObjectReference platform) : IImmutableAssetCache
{
    public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken)
        => await platform.InvokeAsync<byte[]?>("cacheRead", cancellationToken, hash);
    public async ValueTask RemoveAsync(string hash, CancellationToken cancellationToken)
        => await platform.InvokeVoidAsync("cacheRemove", cancellationToken, hash);
    public async ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken cancellationToken)
        => await platform.InvokeAsync<bool>("cachePut", cancellationToken, asset.Sha256, bytes, asset.ContentType);
    public async ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken cancellationToken)
        => await platform.InvokeVoidAsync("cacheCommit", cancellationToken, identity, hashes);
}
