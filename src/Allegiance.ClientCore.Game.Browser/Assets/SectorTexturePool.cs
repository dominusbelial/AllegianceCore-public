using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Only Pump touches graphics, at most eight decoded/uploaded rows per
/// RAF. A completed texture replaces its prior surface atomically. Old entry
/// copies remain usable until then; missing remote textures render lit-solid.</summary>
public sealed class SectorTexturePool : IDisposable
{
    private sealed record Ready(string Hash, Texture2D Texture);
    private sealed class Upload(StagedAsset asset, byte[] bytes, CancellationToken token)
    {
        internal readonly StagedAsset Asset = asset;
        internal readonly CancellationToken Token = token;
        internal readonly IncrementalSurface Surface = new(bytes);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Texture2D? Texture;
        internal byte[]? Rows;
    }
    private readonly SectorManifest _manifest;
    private readonly StagedAssetSource _base;
    private readonly Dictionary<string, Ready> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<Upload> _uploads = new();
    private bool _disposed;
    public long Revision { get; private set; }
    public double MaximumPumpMilliseconds { get; private set; }
    public int UploadedTextures { get; private set; }
    public int PendingTextures => _uploads.Count;
    public SectorTexturePool(SectorManifest manifest, StagedAssetSource resident)
    {
        _manifest = manifest; _base = resident;
        Revision = ++resident.TextureRevision;
        resident.StreamedSources = manifest.Sectors.SelectMany(s => s.Assets).Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        resident.TextureLookup = Lookup;
    }
    public bool IsResident(short sector) => _manifest.Sectors.FirstOrDefault(s => s.SectorId == sector) is { } pack &&
        pack.Assets.All(a => _ready.TryGetValue(StagedManifest.Canonical(a.Source), out var value) && value.Hash == a.Sha256);
    public bool IsSourceReady(string source) => _ready.ContainsKey(StagedManifest.Canonical(source)) || _base.Contains(source);
    public object Metrics => new { uploadedTextures = UploadedTextures, pendingTextures = PendingTextures, revision = Revision,
        maximumPumpMs = MaximumPumpMilliseconds,
        readySectors = _manifest.Sectors.Where(s => IsResident(s.SectorId)).Select(s => s.SectorId).ToArray(),
        surfaces = _ready.Select(pair => new { source = pair.Key, sha256 = pair.Value.Hash, width = pair.Value.Texture.Width, height = pair.Value.Texture.Height }).ToArray() };
    private (bool, Texture2D?) Lookup(string stem, bool colorKey)
    {
        // Names in these dictionaries have already passed manifest validation.
        // Lookup occurs for every rock/material every frame, so do not run a
        // path regex or lowercase allocation on the hot rendering path.
        string name = stem + ".mdl";
        if (_ready.TryGetValue(name, out var ready)) return (true, ready.Texture);
        return (_base.StreamedSources?.Contains(name) == true && !_base.Contains(name), null);
    }
    public async Task PrepareAsync(SectorVisualPack batch, StagedAssetSource staged, CancellationToken token)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SectorTexturePool));
        var tasks = new List<Task>();
        foreach (var asset in batch.Assets)
        {
            if (_ready.TryGetValue(StagedManifest.Canonical(asset.Source), out var ready) && ready.Hash == asset.Sha256) continue;
            token.ThrowIfCancellationRequested();
            var upload = new Upload(asset, staged.Bytes(asset.Source), token);
            _uploads.Enqueue(upload); tasks.Add(upload.Done.Task);
        }
        await Task.WhenAll(tasks).WaitAsync(token);
    }
    public void Retain(short current, short? prefetch)
    {
        var names = _manifest.Sectors.Where(s => s.SectorId == current || s.SectorId == prefetch)
            .SelectMany(s => s.Assets).Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        foreach (string name in _ready.Keys.Where(n => !names.Contains(n)).ToArray())
        { _ready[name].Texture.Dispose(); _ready.Remove(name); _base.TextureRevision = ++Revision; }
    }
    public void Pump(GraphicsDevice device)
    {
        if (!_uploads.TryPeek(out var upload)) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            upload.Token.ThrowIfCancellationRequested();
            var surface = upload.Surface;
            if (upload.Texture is null)
            {
                upload.Texture = new Texture2D(device, surface.Width, surface.Height, false, SurfaceFormat.Color);
                upload.Rows = new byte[surface.Width * 4 * 8];
                return; // allocation and decoding never share a frame
            }
            int first = surface.Row;
            int rows = surface.DecodeRows(upload.Rows!, 8, !upload.Asset.Source.StartsWith("bgrnd", StringComparison.OrdinalIgnoreCase));
            upload.Texture.SetData(0, new Rectangle(0, first, surface.Width, rows), upload.Rows!, 0, rows * surface.Width * 4);
            if (surface.Complete)
            {
                string source = StagedManifest.Canonical(upload.Asset.Source);
                if (_ready.Remove(source, out var previous)) previous.Texture.Dispose();
                _ready[source] = new(upload.Asset.Sha256, upload.Texture);
                upload.Texture = null; _uploads.Dequeue(); _base.TextureRevision = ++Revision; UploadedTextures++;
                upload.Done.TrySetResult();
            }
        }
        catch (OperationCanceledException) { upload.Texture?.Dispose(); _uploads.Dequeue(); upload.Done.TrySetCanceled(upload.Token); }
        catch (Exception error) { upload.Texture?.Dispose(); _uploads.Dequeue(); upload.Done.TrySetException(error); }
        finally { MaximumPumpMilliseconds = Math.Max(MaximumPumpMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _base.TextureLookup = null; _base.StreamedSources = null;
        _base.TextureRevision = ++Revision;
        foreach (var texture in _ready.Values) texture.Texture.Dispose();
        foreach (var upload in _uploads) { upload.Texture?.Dispose(); upload.Done.TrySetCanceled(); }
        _uploads.Clear(); _ready.Clear();
    }
}
