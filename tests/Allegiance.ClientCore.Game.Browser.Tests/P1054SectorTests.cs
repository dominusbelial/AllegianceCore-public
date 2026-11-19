using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.Core;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1054SectorTests
{
    private static StagedAsset Asset(string name, byte[] bytes) => new(name, "packs/" + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
        Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/octet-stream", "fixture original", []);
    private static readonly StagedManifest Resident = new(2, [Asset("core.igc", [1])]);
    private static SectorManifest Index(params SectorVisualPack[] packs) => new(1, "XXV_simple.igc", 1051, packs[0].SectorId, packs);

    [Theory]
    [InlineData("hull.mdl")]
    [InlineData("rock.cvh")]
    [InlineData("core.igc")]
    [InlineData("weaponbmp.mdl")]
    public void Deferred_manifest_rejects_every_non_allowlisted_gameplay_file(string name)
        => Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", [Asset(name, [2])])).Validate(Resident));

    [Fact]
    public void Manifest_allows_base_dependencies_but_rejects_missing_or_conflicting_visuals()
    {
        var visual = Asset("bgrnd03bmp.mdl", [2]) with { Dependencies = ["core.igc"] };
        Index(new SectorVisualPack(1053, "Ceto", [visual])).Validate(Resident);
        Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", [visual with { Dependencies = ["missing.mdl"] }])).Validate(Resident));
        Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", [visual]), new(1054, "Other", [Asset(visual.Source, [3])])).Validate(Resident));
        Assert.Equal(2, Index(new SectorVisualPack(1053, "Ceto", [visual])).RetainedHashes(Resident).Length);
        var geometry = Asset("rock.mdl", [7]) with { VisualDependencies = [visual.Source] };
        var baseWithGeometry = new StagedManifest(2, [Resident.Assets[0], geometry]);
        baseWithGeometry.Validate();
        Index(new SectorVisualPack(1053, "Ceto", [visual])).Validate(baseWithGeometry);
        Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", [Asset("nebplnt01bmp.mdl", [3])])).Validate(baseWithGeometry));
        Assert.Throws<InvalidDataException>(() => new StagedManifest(2, [geometry with { VisualDependencies = ["hull.cvh"] }]).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incremental_rows_equal_native_mesh_decoder_with_pitch_and_colorkey(bool key)
    {
        var bytes = Surface(17, 13);
        var decoder = new IncrementalSurface(bytes);
        var actual = new List<byte>(); var row = new byte[17 * 4 * 3];
        while (!decoder.Complete)
        { int rows = decoder.DecodeRows(row, 3, key); actual.AddRange(row.Take(rows * 17 * 4)); }
        var reference = MdlTextureDecoder.Decode(bytes, flipVertical: false);
        Assert.Equal(key ? MdlTextureDecoder.ToPremultipliedRgba(reference, 10) : reference.Rgba, actual.ToArray());
        Assert.Equal(13, decoder.Row);
        Assert.Throws<InvalidDataException>(() => new IncrementalSurface(bytes[..^5]));
    }

    [Fact]
    public void Manifest_rejects_nulls_and_hash_contract_conflicts_across_batches()
    {
        Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", [null!])).Validate(Resident));
        var assets = Enumerable.Range(1, 5).Select(i => Asset($"bgrnd{i}bmp.mdl", [(byte)(i + 1)])).ToArray();
        assets[4] = assets[4] with { Sha256 = assets[0].Sha256, Path = assets[0].Path, ByteSize = 2 };
        Assert.Throws<InvalidDataException>(() => Index(new SectorVisualPack(1053, "Ceto", assets)).Validate(Resident));
    }

    [Fact]
    public void Large_logical_sector_keeps_the_existing_per_snapshot_residency_bound()
    {
        var assets = Enumerable.Range(1, 8).Select(i => Asset($"bgrnd{i}bmp.mdl", [(byte)(i + 1)]) with { ByteSize = 8 * 1024 * 1024 + 116 }).ToArray();
        Assert.Throws<InvalidDataException>(() => new StagedManifest(2, assets).Validate());
        Index(new SectorVisualPack(1053, "Ceto", assets)).Validate(Resident);
    }

    [Fact]
    public async Task Cooperative_verifier_checks_every_byte_size_hash_and_cancellation()
    {
        byte[] bytes = new byte[300000]; RandomNumberGenerator.Fill(bytes);
        var asset = Asset("bgrnd03bmp.mdl", bytes);
        await SectorPackStreamer.VerifyCooperativelyAsync(asset, bytes, default);
        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => SectorPackStreamer.VerifyCooperativelyAsync(asset, corrupt, default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => SectorPackStreamer.VerifyCooperativelyAsync(asset, bytes[..^1], default).AsTask());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SectorPackStreamer.VerifyCooperativelyAsync(asset, bytes, cancel.Token).AsTask());
    }

    [Fact]
    public async Task Texture_epochs_survive_rejoining_and_declared_visual_names_remain_case_canonical()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply([1])))) { BaseAddress = new("http://localhost/") };
        var source = await new AssetStager(http, new MemoryCache()).StageAsync(Resident);
        var index = Index(new SectorVisualPack(1053, "Ceto", [Asset("BGRND03BMP.MDL", [2])]));
        index.Validate(Resident);
        var first = new SectorTexturePool(index, source); long initial = source.TextureRevision;
        Assert.True(source.Exists("bgrnd03bmp.mdl")); Assert.False(source.Contains("bgrnd03bmp.mdl"));
        Assert.Throws<InvalidDataException>(() => source.Exists("../bgrnd03bmp.mdl"));
        Assert.True(source.TryGetTexture("BGRND03BMP", false, out var placeholder)); Assert.Null(placeholder);
        first.Dispose(); Assert.True(source.TextureRevision > initial); Assert.False(source.Exists("bgrnd03bmp.mdl"));
        using var second = new SectorTexturePool(index, source);
        Assert.True(second.Revision > first.Revision); Assert.Equal(second.Revision, source.TextureRevision);
        Assert.False(second.IsResident(1053));
    }

    [Fact]
    public async Task Entry_upgrade_preempts_prefetch_without_publishing_unverified_bytes()
    {
        var entry = Asset("bgrnd03bmp.mdl", [2]); var prefetch = Asset("nebplnt01bmp.mdl", [3]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>(); var resident = new HashSet<short>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string hash = request.RequestUri!.AbsolutePath.Split('/')[^1]; order.Add(hash);
            if (hash == prefetch.Sha256 && order.Count == 1)
            { firstStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Reply(hash == entry.Sha256 ? [2] : [3]);
        })) { BaseAddress = new("http://localhost/") };
        await using var streamer = new SectorPackStreamer(Index(new SectorVisualPack(1053, "Ceto", [entry]), new SectorVisualPack(1051, "Other", [prefetch])), Resident,
            new(http, new MemoryCache()), (pack, staged, _) =>
            { Assert.Equal(1, staged.ResidentBytes); resident.Add(pack.SectorId); if (pack.SectorId == 1053) entryReady.TrySetResult(); return Task.CompletedTask; }, resident.Contains);
        streamer.RequestSector(1051, 2);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        streamer.RequestSector(1053, 0);
        await entryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(entry.Sha256, order[1]); Assert.Contains((short)1053, resident); Assert.Equal(0, streamer.RetryCount);
    }

    [Fact]
    public async Task Sector_503_retries_with_backoff_and_disposal_cancels_a_queued_attempt()
    {
        var visual = Asset("bgrnd03bmp.mdl", [2]); int attempts = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var times = new List<long>();
        using var http = new HttpClient(new Handler((_, _) =>
        { times.Add(Environment.TickCount64); return Task.FromResult(++attempts == 1 ? new(HttpStatusCode.ServiceUnavailable) : Reply([2])); })) { BaseAddress = new("http://localhost/") };
        bool published = false;
        await using var streamer = new SectorPackStreamer(Index(new SectorVisualPack(1053, "Ceto", [visual])), Resident, new(http, new MemoryCache()),
            (_, staged, _) => { Assert.Equal([2], staged.Bytes(visual.Source)); published = true; ready.TrySetResult(); return Task.CompletedTask; }, _ => published);
        streamer.RequestSector(1053, 1);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts); Assert.Equal(1, streamer.RetryCount); Assert.True(times[1] - times[0] >= 1000);
        await streamer.DisposeAsync(); streamer.RequestSector(1053, 0); Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Dispose_cancels_a_suspended_prepare_without_a_graphics_frame()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply([2])))) { BaseAddress = new("http://localhost/") };
        var streamer = new SectorPackStreamer(Index(new SectorVisualPack(1053, "Ceto", [Asset("bgrnd03bmp.mdl", [2])])), Resident, new(http, new MemoryCache()),
            async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, _ => false);
        streamer.RequestSector(1053, 0); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await streamer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Concurrent_sector_attempts_share_a_global_four_download_limit()
    {
        var assets = Enumerable.Range(1, 8).Select(i => Asset($"bgrnd{i}bmp.mdl", [(byte)i])).ToArray();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0, started = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            int concurrent = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(maximum, concurrent));
            if (Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                var asset = assets.Single(a => request.RequestUri!.AbsolutePath == "/" + a.Path);
                return Reply([(byte)(Array.IndexOf(assets, asset) + 1)]);
            }
            finally { Interlocked.Decrement(ref active); }
        })) { BaseAddress = new("http://localhost/") };
        var stager = new AssetStager(http, new MemoryCache()) { CommitCacheManifest = false };
        var replacement = new AssetStager(http, new MemoryCache()) { CommitCacheManifest = false };
        var first = stager.StageAsync(new(2, assets[..4]));
        var second = replacement.StageAsync(new(2, assets[4..]));
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, active); Assert.Equal(4, started);
        release.TrySetResult(); await Task.WhenAll(first, second);
        Assert.Equal(4, maximum); Assert.Equal(0, active); Assert.Equal(8, started);
        Assert.Same(await second, replacement.Current);
    }

    private static byte[] Surface(int width, int height)
    {
        int pitch = (width * 2 + 3) & ~3;
        var bytes = new byte[76 + pitch * height];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, MdlTextureDecoder.MdlMagic);
        foreach (var (offset, value) in new[] { (40, width), (44, height), (48, pitch), (52, 16), (56, 0xf800), (60, 0x7e0), (64, 0x1f) })
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        new Random(1054).NextBytes(bytes.AsSpan(76)); bytes[76] = bytes[77] = 0;
        return bytes;
    }
    private static HttpResponseMessage Reply(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/octet-stream"); return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class MemoryCache : IImmutableAssetCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _files = new();
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken token) => ValueTask.FromResult(_files.GetValueOrDefault(hash));
        public ValueTask RemoveAsync(string hash, CancellationToken token) { _files.TryRemove(hash, out _); return ValueTask.CompletedTask; }
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken token) { _files[asset.Sha256] = bytes; return ValueTask.FromResult(true); }
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken token) => ValueTask.CompletedTask;
    }
}
