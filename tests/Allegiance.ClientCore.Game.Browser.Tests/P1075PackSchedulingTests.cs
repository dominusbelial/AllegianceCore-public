using System.Net;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1075PackSchedulingTests
{
    private static StagedAsset Asset(string name, byte value) => new(name, "packs/" + Convert.ToHexStringLower(SHA256.HashData(new[] { value })),
        1, Convert.ToHexStringLower(SHA256.HashData(new[] { value })), "application/octet-stream", "fixture", []);

    [Fact]
    public async Task Visible_priority_overrides_offscreen_grace_and_shared_station_texture_downloads_once()
    {
        var core = Asset("core.igc", 1); var station = Asset("station.mdl", 2);
        var texture = Asset("sharedbmp.mdl", 3); var other = Asset("other.mdl", 4);
        var resident = new StagedManifest(2, [core]);
        var manifest = new BattleManifest(1, [station, texture, other],
            [new("station-1", [station.Source, texture.Source]), new("station-2", [other.Source, texture.Source])]);
        var paths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            paths.Enqueue(request.RequestUri!.AbsolutePath);
            started.TrySetResult(); await release.Task.WaitAsync(token);
            byte value = request.RequestUri.AbsolutePath.EndsWith(station.Sha256, StringComparison.Ordinal) ? (byte)2
                : request.RequestUri.AbsolutePath.EndsWith(texture.Sha256, StringComparison.Ordinal) ? (byte)3 : (byte)4;
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([value]) };
            reply.Content.Headers.ContentType = new("application/octet-stream"); return reply;
        })) { BaseAddress = new("http://localhost/") };
        var destination = new StagedAssetSource(new(StringComparer.Ordinal) { [core.Source] = [1] });
        await using var streamer = new BattlePackStreamer(manifest, resident, new(http, new EmptyCache()), destination);
        streamer.RequestPack("station-2", 2); // offscreen: thirty-second grace
        streamer.RequestPack("station-1", 0); // in view: immediate
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("/" + other.Path, paths);
        release.TrySetResult();
        await Until(() => streamer.IsReady("station-1"));
        Assert.False(streamer.IsReady("station-2"));
        streamer.RequestPack("station-2", 0); // camera turns: do not wait for grace
        await Until(() => streamer.IsReady("station-2"));
        Assert.Equal(3, paths.Count);
        Assert.Single(paths, p => p.EndsWith(texture.Sha256, StringComparison.Ordinal));
        Assert.True(destination.Contains(other.Source));
    }

    [Fact]
    public async Task Shared_sector_and_battle_stager_coalesces_an_inflight_texture_even_without_persistent_storage()
    {
        var texture = Asset("sharedbmp.mdl", 9);
        var alias = texture with { Source = "aliasbmp.mdl" };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref requests); started.TrySetResult();
            await release.Task.WaitAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) };
            response.Content.Headers.ContentType = new("application/octet-stream"); return response;
        })) { BaseAddress = new("http://localhost/") };
        var stager = new AssetStager(http, new EmptyCache()) { CommitCacheManifest = false };
        var sector = stager.StageAsync(new(2, [texture]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var battle = stager.StageAsync(new(2, [alias]));
        release.TrySetResult();
        var sources = await Task.WhenAll(sector, battle);
        Assert.Equal(1, requests);
        Assert.Equal(new byte[] { 9 }, sources[0].Bytes(texture.Source));
        Assert.Equal(new byte[] { 9 }, sources[1].Bytes(alias.Source));
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class EmptyCache : IImmutableAssetCache
    {
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken token) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask RemoveAsync(string hash, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken token) => ValueTask.CompletedTask;
    }
}
