using System.Net;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1053StagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Four_downloads_overlap_but_only_a_complete_verified_pack_is_published(bool corrupt)
    {
        var files = Enumerable.Range(1, 9).Select(i => new byte[] { (byte)i }).ToArray();
        var assets = files.Select((bytes, i) =>
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new StagedAsset($"file{i}.mdl", "packs/" + hash, 1, hash, "application/octet-stream", "fixture", []);
        }).ToArray();
        var handler = new Handler(assets, files, corrupt);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var cache = new Cache(); var stager = new AssetStager(http, cache);
        var stage = stager.StageAsync(new(2, assets));
        await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, handler.Active); Assert.Null(stager.Current); Assert.False(cache.Committed);
        handler.Release.SetResult();
        if (corrupt)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => stage);
            Assert.Null(stager.Current); Assert.False(cache.Committed);
        }
        else
        {
            var resident = await stage; Assert.Same(resident, stager.Current);
            Assert.Equal(9, resident.ResidentBytes); Assert.True(cache.Committed);
        }
        Assert.Equal(0, handler.Active); Assert.InRange(handler.Maximum, 2, 4);
    }
    [Fact]
    public async Task Menu_stage_keeps_prior_join_files_until_complete_join_owns_cleanup()
    {
        static StagedAsset Entry(string name, byte value)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { value }));
            return new(name, "packs/" + hash, 1, hash, "application/octet-stream", "fixture", []);
        }
        var menu = Entry("menu.mdl", 1); var join = Entry("flight.mdl", 2); var obsolete = Entry("old.mdl", 3);
        var cache = new Cache();
        cache.Files[menu.Sha256] = [1]; cache.Files[join.Sha256] = [2]; cache.Files[obsolete.Sha256] = [3];
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new Uri("http://localhost/") };
        var stager = new AssetStager(http, cache) { CommitCacheManifest = false };
        await stager.StageAsync(new(2, [menu]));
        Assert.False(cache.Committed); Assert.Equal(3, cache.Files.Count);
        stager.CommitCacheManifest = true;
        await stager.StageAsync(new(2, [menu, join]));
        Assert.True(cache.Committed); Assert.Equal(2, cache.Files.Count);
        Assert.DoesNotContain(obsolete.Sha256, cache.Files.Keys);
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Warm menu and join must reuse verified cache bytes");
    }
    private sealed class Handler(StagedAsset[] assets, byte[][] files, bool corrupt) : HttpMessageHandler
    {
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active, Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int active = Interlocked.Increment(ref Active);
            Maximum = Math.Max(Maximum, active);
            if (active == 4) FourStarted.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                int index = Array.FindIndex(assets, a => request.RequestUri!.AbsolutePath == "/" + a.Path);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt && index == 8 ? [0] : files[index]) };
                response.Content.Headers.ContentType = new("application/octet-stream"); return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class Cache : IImmutableAssetCache
    {
        public bool Committed;
        public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new();
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken token) => ValueTask.FromResult(Files.GetValueOrDefault(hash));
        public ValueTask RemoveAsync(string hash, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken token) { Files[asset.Sha256] = bytes; return ValueTask.FromResult(true); }
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken token)
        { Committed = true; foreach (string hash in Files.Keys.Except(hashes)) Files.TryRemove(hash, out _); return ValueTask.CompletedTask; }
    }
}
