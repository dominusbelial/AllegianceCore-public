using System.Security.Cryptography;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Event-loop visual delivery. Flight never awaits this worker. One
/// sector owns at most four downloads; a current-sector request preempts a
/// speculative prefetch. Preparation/promotion are platform callbacks.</summary>
public sealed class SectorPackStreamer : IAsyncDisposable
{
    private sealed class Request(short sectorId, int priority)
    {
        internal readonly short SectorId = sectorId;
        internal int Priority = priority, Failures;
        internal long RetryAt;
    }
    private readonly SectorManifest _manifest;
    private readonly AssetStager _stager;
    private readonly IReadOnlySet<string> _available;
    private readonly Func<SectorVisualPack, StagedAssetSource, CancellationToken, Task> _prepare;
    private readonly Func<short, bool> _resident;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<short, Request> _pending = new();
    private CancellationTokenSource? _attempt;
    private Task? _worker;
    private Request? _active;
    private bool _disposed;
    public string Status { get; private set; } = "";
    public short? ActiveSector => _active?.SectorId;
    public int RetryCount { get; private set; }
    public int CompletedPacks { get; private set; }
    public event Action? Changed;

    public void SetInterest(short current, short? prefetch)
    {
        foreach (var request in _pending.Values.Where(r => r.Priority > 0 && r.SectorId != current && r.SectorId != prefetch).ToArray())
        {
            _pending.Remove(request.SectorId);
            if (ReferenceEquals(request, _active)) _attempt?.Cancel();
        }
    }

    public SectorPackStreamer(SectorManifest manifest, StagedManifest resident, AssetStager stager,
        Func<SectorVisualPack, StagedAssetSource, CancellationToken, Task> prepare, Func<short, bool> isResident)
    {
        manifest.Validate(resident);
        _manifest = manifest; _stager = stager; _prepare = prepare; _resident = isResident;
        _available = resident.Assets.Concat(manifest.Sectors.SelectMany(s => s.Assets)).Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        stager.CommitCacheManifest = false;
    }

    // 0 = first launch upgrade, 1 = sector entry, 2 = selected target/starmap.
    public void RequestSector(short sectorId, int priority)
    {
        if (_disposed || _resident(sectorId) || !_manifest.Sectors.Any(s => s.SectorId == sectorId)) return;
        priority = Math.Clamp(priority, 0, 2);
        if (_pending.TryGetValue(sectorId, out var existing)) existing.Priority = Math.Min(priority, existing.Priority);
        else _pending[sectorId] = new(sectorId, priority);
        if (_active is { } active && priority < active.Priority) _attempt?.Cancel();
        _worker ??= RunAsync();
    }

    private async Task RunAsync()
    {
        // Do not run even warm cache/preparation work in the requesting RAF.
        await Task.Yield();
        try
        {
            while (!_disposed && _pending.Count > 0)
            {
                var request = _pending.Values.OrderBy(r => r.Priority).ThenBy(r => r.RetryAt).FirstOrDefault(r => r.RetryAt <= Environment.TickCount64);
                if (request is null) { await Task.Delay(100, _lifetime.Token); continue; }
                if (_resident(request.SectorId)) { _pending.Remove(request.SectorId); continue; }
                _active = request;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _attempt = attempt;
                var sector = _manifest.Sectors.Single(s => s.SectorId == request.SectorId);
                try
                {
                    int completed = 0;
                    // Each existing staging snapshot remains <=64 MiB, even a
                    // sector whose original surfaces plus headers exceed it.
                    foreach (var batch in sector.Assets.Chunk(4))
                    {
                        var staged = await _stager.StageAsync(new(2, batch), (percent, _) =>
                        {
                            Status = $"Loading {sector.Name} visuals · {(completed * 100 + percent * batch.Length) / sector.Assets.Length}%";
                            Changed?.Invoke();
                        }, attempt.Token, _available);
                        await _prepare(sector with { Assets = batch }, staged, attempt.Token);
                        completed += batch.Length;
                    }
                    attempt.Token.ThrowIfCancellationRequested();
                    _pending.Remove(request.SectorId); CompletedPacks++;
                    Status = _pending.Count == 0 ? "" : "Sector visuals ready";
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                catch (Exception error)
                {
                    Console.WriteLine($"[browser] {sector.Name} visual retry: {error.GetType().Name}: {error.Message}");
                    RetryCount++; request.Failures++;
                    int seconds = 1 << Math.Min(5, request.Failures - 1);
                    request.RetryAt = Environment.TickCount64 + seconds * 1000;
                    Status = $"{sector.Name} visuals unavailable · retrying in {seconds}s. Flight continues.";
                }
                finally { _active = null; _attempt = null; Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _worker = null; }
    }

    /// <summary>Chunked SHA work yields to the browser between bounded blocks.
    /// Same size + SHA-256 contract as the synchronous preflight verifier.</summary>
    public static async ValueTask VerifyCooperativelyAsync(StagedAsset asset, byte[] bytes, CancellationToken token)
    {
        if (bytes.LongLength != asset.ByteSize) throw new InvalidDataException("Unexpected visual size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int offset = 0; offset < bytes.Length; offset += 128 * 1024)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(bytes, offset, Math.Min(128 * 1024, bytes.Length - offset));
            await Task.Delay(1, token);
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256) throw new InvalidDataException("Visual verification failed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        if (_worker is { } worker) await worker;
        _pending.Clear(); _lifetime.Dispose();
    }
}
