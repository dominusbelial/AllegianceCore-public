namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Async network/cache work happens before publication. Draw only sees Current.</summary>
public sealed class AssetStager(HttpClient http, IImmutableAssetCache cache,
    Action<HttpRequestMessage>? prepareRequest = null,
    Func<StagedAsset, byte[], CancellationToken, ValueTask>? verify = null)
{
    private readonly Dictionary<string, Download> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _generation;
    // Starter/join/sector loaders share the head's HttpClient. Retiring one
    // join while another starts must not multiply the network budget.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, SemaphoreSlim> DownloadSlots = new();
    private readonly SemaphoreSlim _downloadSlots = DownloadSlots.GetValue(http, static _ => new(4));
    private sealed class Download
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<byte[]> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
        internal required StagedAsset Asset;
    }
    public string[]? RetainedHashes { get; set; }
    // A menu-only stage may preserve the previous complete join residency;
    // the user-triggered complete join owns obsolete-file cleanup. Per-file
    // verification and the cache's byte/entry limits still apply on every path.
    public bool CommitCacheManifest { get; set; } = true;
    public StagedAssetSource? Current { get; private set; }

    public async Task<StagedManifest> ReadManifestAsync(CancellationToken cancellationToken, string manifestPath = "starter-manifest.json")
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize(await ReadManifestBytesAsync(manifestPath, cancellationToken), StagedJson.Default.StagedManifest)
            ?? throw new InvalidDataException("Missing game manifest.");
        manifest.Validate();
        return manifest;
    }

    public async Task<SectorManifest> ReadSectorManifestAsync(StagedManifest resident, CancellationToken cancellationToken)
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize(await ReadManifestBytesAsync("sector-manifest.json", cancellationToken), SectorJson.Default.SectorManifest)
            ?? throw new InvalidDataException("Missing sector manifest.");
        manifest.Validate(resident);
        return manifest;
    }

    public async Task<BattleManifest> ReadBattleManifestAsync(StagedManifest resident, CancellationToken token)
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize(await ReadManifestBytesAsync("battle-manifest.json", token), BattleJson.Default.BattleManifest)
            ?? throw new InvalidDataException("Missing battle manifest.");
        manifest.Validate(resident);
        return manifest;
    }

    private async Task<byte[]> ReadManifestBytesAsync(string manifestPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestPath);
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
        return buffer.ToArray();
    }

    public async Task<StagedAssetSource> StageAsync(StagedManifest manifest, Action<int, string>? progress = null,
        CancellationToken cancellationToken = default, IReadOnlySet<string>? residentSources = null)
    {
        manifest.Validate(residentSources);
        long generation;
        lock (_gate) generation = ++_generation;
        var staged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assets = manifest.Assets.DistinctBy(a => StagedManifest.Canonical(a.Source)).ToArray();
        long total = assets.Sum(a => a.ByteSize), completed = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        // Bounded async batches overlap cache/network waits on WASM's event
        // loop. Every file is verified before atomic resident publication;
        // failed batches drain fully so Retry cannot race an earlier download.
        foreach (var batch in assets.Chunk(4))
        {
            var downloads = batch.Select(async asset =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                double secondsLeft = completed == 0 ? double.NaN : elapsed.Elapsed.TotalSeconds * (total - completed) / completed;
                string eta = double.IsFinite(secondsLeft) ? $"{Math.Ceiling(secondsLeft)}s" : "estimating";
                progress?.Invoke((int)(completed * 100 / total), $"{asset.Source} · {completed / 1048576d:F2}/{total / 1048576d:F2} MB · ETA {eta}");
                return (asset, bytes: await GetAsync(asset, cancellationToken));
            }).ToArray();
            foreach (var (asset, bytes) in await Task.WhenAll(downloads))
            {
                staged.Add(StagedManifest.Canonical(asset.Source), bytes);
                completed += bytes.Length;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (CommitCacheManifest)
            await cache.CommitManifestAsync(manifest.Identity, RetainedHashes ?? assets.Select(a => a.Sha256).Distinct().ToArray(), cancellationToken);
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
            if (start) { download = new Download { Asset = asset }; _inflight.Add(asset.Sha256, download); }
            download.Waiters++;
        }
        if (start) _ = CompleteAsync(asset, download);
        try
        {
            var bytes = await download.Result.Task.WaitAsync(cancellationToken);
            // A shared hash may have been requested by a different manifest.
            // Each waiter still has to satisfy its own declared size contract.
            if (download.Asset.ByteSize != asset.ByteSize) throw new InvalidDataException("Inconsistent shared asset size.");
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
        await _downloadSlots.WaitAsync(cancellationToken);
        try { return await LoadOwnedAsync(asset, cancellationToken); }
        finally { _downloadSlots.Release(); }
    }

    private ValueTask VerifyAsync(StagedAsset asset, byte[] bytes, CancellationToken cancellationToken)
    {
        if (verify is not null) return verify(asset, bytes, cancellationToken);
        PreviewManifest.Verify(asset.Source, asset.ByteSize, asset.Sha256, bytes);
        return ValueTask.CompletedTask;
    }

    private async Task<byte[]> LoadOwnedAsync(StagedAsset asset, CancellationToken cancellationToken)
    {
        var hit = await cache.ReadAsync(asset.Sha256, cancellationToken);
        if (hit is not null)
        {
            try { await VerifyAsync(asset, hit, cancellationToken); return hit; }
            catch (InvalidDataException) { await cache.RemoveAsync(asset.Sha256, cancellationToken); }
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Path);
        prepareRequest?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != asset.ContentType)
            throw new InvalidDataException($"Unexpected content type: {asset.Source}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[checked((int)asset.ByteSize)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (count == 0) break;
            offset += count;
        }
        if (offset != bytes.Length || await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException($"Unexpected artwork size: {asset.Source}");
        await VerifyAsync(asset, bytes, cancellationToken);
        if (!await cache.PutAsync(asset, bytes, cancellationToken))
            Console.WriteLine($"[browser] Storage refused {asset.Source}; keeping verified bytes in memory for this visit.");
        return bytes;
    }

}
