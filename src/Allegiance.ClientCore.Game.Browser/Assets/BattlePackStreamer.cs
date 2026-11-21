namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>One event-loop worker, shared four-download budget and verified,
/// atomic closure promotion. A failure leaves the stand-in until a later retry.</summary>
public sealed class BattlePackStreamer : IAsyncDisposable
{
    private sealed class Request(string key) { internal readonly string Key = key; internal int Failures; internal long RetryAt; }
    private readonly Dictionary<string, StagedAsset[]> _packs;
    private readonly Dictionary<string, Request> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> _available;
    private readonly AssetStager _stager;
    private readonly StagedAssetSource _destination;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _worker;
    private bool _disposed;
    public int CompletedPacks { get; private set; }
    public int RetryCount { get; private set; }
    public long PromotedBytes { get; private set; }
    public bool IsReady(string key) => _ready.Contains(key);

    public BattlePackStreamer(BattleManifest manifest, StagedManifest resident, AssetStager stager, StagedAssetSource destination)
    {
        manifest.Validate(resident);
        _stager = stager; _destination = destination; stager.CommitCacheManifest = false;
        var files = manifest.Assets.ToDictionary(a => StagedManifest.Canonical(a.Source), StringComparer.Ordinal);
        _available = resident.Assets.Concat(manifest.Assets).Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        _packs = manifest.Packs.ToDictionary(p => p.Key, p => p.Sources.Select(StagedManifest.Canonical).Distinct().Where(files.ContainsKey).Select(n => files[n]).ToArray(), StringComparer.Ordinal);
    }

    public void RequestPack(string key)
    {
        if (_disposed || _ready.Contains(key) || !_packs.ContainsKey(key) || _pending.ContainsKey(key)) return;
        _pending.Add(key, new(key));
        if (_worker is null || _worker.IsCompleted) _worker = RunAsync();
    }

    private async Task RunAsync()
    {
        await Task.Yield(); // Never do cache/hash work on the requesting draw call.
        try
        {
            while (!_disposed && _pending.Count > 0)
            {
                var request = _pending.Values.FirstOrDefault(r => r.RetryAt <= Environment.TickCount64);
                if (request is null) { await Task.Delay(100, _lifetime.Token); continue; }
                try
                {
                    var assets = _packs[request.Key].Where(a => !_destination.Contains(a.Source)).ToArray();
                    if (assets.Length > 0)
                    {
                        var staged = await _stager.StageAsync(new(2, assets), cancellationToken: _lifetime.Token, residentSources: _available);
                        _lifetime.Token.ThrowIfCancellationRequested();
                        _destination.Merge(staged); PromotedBytes += staged.ResidentBytes;
                    }
                    _ready.Add(request.Key); _pending.Remove(request.Key); CompletedPacks++;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    RetryCount++; request.Failures++;
                    request.RetryAt = Environment.TickCount64 + (1 << Math.Min(5, request.Failures - 1)) * 1000;
                    Console.WriteLine($"[browser] Battle visual retry {request.Key}: {error.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _worker = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        if (_worker is { } worker) await worker;
        _pending.Clear(); _lifetime.Dispose();
    }
}
