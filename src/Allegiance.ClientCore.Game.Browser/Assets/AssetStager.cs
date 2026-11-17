namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Async network/cache work happens before publication. Draw only sees Current.</summary>
public sealed class AssetStager(HttpClient http, IImmutableAssetCache cache,
    Action<HttpRequestMessage>? prepareRequest = null)
{
    private readonly Dictionary<string, Download> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _generation;
    private sealed class Download
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<byte[]> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
    }
    public StagedAssetSource? Current { get; private set; }

    public async Task<StagedManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "starter-manifest.json");
        prepareRequest?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Bound manifest bytes too; no unbounded ReadFromJson on an untrusted origin.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) != 0)
        {
            if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Game manifest is too large.");
            buffer.Write(block, 0, count);
        }
        var manifest = System.Text.Json.JsonSerializer.Deserialize(buffer.ToArray(), StagedJson.Default.StagedManifest)
            ?? throw new InvalidDataException("Missing game manifest.");
        manifest.Validate();
        return manifest;
    }

    public async Task<StagedAssetSource> StageAsync(StagedManifest manifest, Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        manifest.Validate();
        long generation;
        lock (_gate) generation = ++_generation;
        var staged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assets = manifest.Assets.DistinctBy(a => StagedManifest.Canonical(a.Source)).ToArray();
        long total = assets.Sum(a => a.ByteSize), completed = 0;
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke((int)(completed * 100 / total), $"Loading {asset.Source}…");
            var bytes = await GetAsync(asset, cancellationToken);
            staged.Add(StagedManifest.Canonical(asset.Source), bytes);
            completed += bytes.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await cache.CommitManifestAsync(manifest.Identity, assets.Select(a => a.Sha256).Distinct().ToArray(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var resident = new StagedAssetSource(staged);
        lock (_gate) { if (generation == _generation) Current = resident; }
        progress?.Invoke(100, "Starter pack verified. Starting game…");
        return resident;
    }

    // Concurrent callers share bytes, but cancellation belongs to each waiter.
    // The underlying request is aborted only after every interested caller has left.
    private async Task<byte[]> GetAsync(StagedAsset asset, CancellationToken cancellationToken)
    {
        Download download;
        bool start;
        lock (_gate)
        {
            start = !_inflight.TryGetValue(asset.Sha256, out download!);
            if (start) { download = new Download(); _inflight.Add(asset.Sha256, download); }
            download.Waiters++;
        }
        if (start) _ = CompleteAsync(asset, download);
        try
        {
            var bytes = await download.Result.Task.WaitAsync(cancellationToken);
            // A shared hash may have been requested by a different manifest.
            // Each waiter still has to satisfy its own declared size contract.
            PreviewManifest.Verify(asset.Source, asset.ByteSize, asset.Sha256, bytes);
            return bytes;
        }
        finally
        {
            lock (_gate)
            {
                if (--download.Waiters == 0 && !download.Result.Task.IsCompleted)
                {
                    download.Cancellation.Cancel();
                    if (_inflight.GetValueOrDefault(asset.Sha256) == download) _inflight.Remove(asset.Sha256);
                }
            }
        }
    }
    private async Task CompleteAsync(StagedAsset asset, Download download)
    {
        try { download.Result.TrySetResult(await LoadAsync(asset, download.Cancellation.Token)); }
        catch (OperationCanceledException) { download.Result.TrySetCanceled(); }
        catch (Exception error) { download.Result.TrySetException(error); }
        finally
        {
            lock (_gate)
                if (_inflight.GetValueOrDefault(asset.Sha256) == download) _inflight.Remove(asset.Sha256);
            download.Cancellation.Dispose();
        }
    }

    private async Task<byte[]> LoadAsync(StagedAsset asset, CancellationToken cancellationToken)
    {
        var hit = await cache.ReadAsync(asset.Sha256, cancellationToken);
        if (hit is not null)
        {
            try { PreviewManifest.Verify(asset.Source, asset.ByteSize, asset.Sha256, hit); return hit; }
            catch (InvalidDataException) { await cache.RemoveAsync(asset.Sha256, cancellationToken); }
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Path);
        prepareRequest?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != asset.ContentType)
            throw new InvalidDataException($"Unexpected content type: {asset.Source}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await PreviewManifest.ReadVerifiedAsync(asset.Source, asset.ByteSize, asset.Sha256, stream, cancellationToken);
        await cache.PutAsync(asset, bytes, cancellationToken);
        return bytes;
    }

}
