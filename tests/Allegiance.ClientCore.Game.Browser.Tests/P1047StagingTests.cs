using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1047StagingTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static StagedAsset Asset(string name = "Fonts/HUD@2x.xnb", byte[]? bytes = null)
    {
        bytes ??= Bytes;
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(name, "packs/" + hash, bytes.Length, hash, "application/octet-stream", "DejaVu; desktop XNB bake", []);
    }
    private static StagedManifest Manifest(params StagedAsset[] assets) => new(2, assets.Length == 0 ? [Asset()] : assets);
    private static HttpClient Client(Func<CancellationToken, Task<HttpResponseMessage>> reply) => new(new Handler(reply)) { BaseAddress = new Uri("http://localhost/") };
    private static HttpResponseMessage Response(byte[]? bytes = null)
    {
        var content = new ByteArrayContent(bytes ?? Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }
    [Fact]
    public async Task Only_complete_verified_snapshots_are_published_and_reads_are_resident()
    {
        int requests = 0;
        using var http = Client(_ => { requests++; return Task.FromResult(Response()); });
        var stager = new AssetStager(http, new Cache());
        var source = await stager.StageAsync(Manifest());
        Assert.Same(source, stager.Current);
        Assert.True(source.Exists("fonts/hud@2x.xnb"));
        Assert.Equal(["fonts/hud@2x.xnb"], source.Enumerate("FONTS"));
        using var stream = source.OpenRead("FONTS/HUD@2X.XNB");
        Assert.Equal(Bytes, ((MemoryStream)stream).ToArray());
        Assert.False(stream.CanWrite);
        Assert.False(((MemoryStream)stream).TryGetBuffer(out _));
        Assert.Throws<AssetNotResidentException>(() => source.OpenRead("missing.mdl"));
        Assert.Equal(1, requests); // A missing read never initiates a download.
        Assert.Equal(4, source.ResidentBytes);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("mime")]
    public async Task Bad_new_generation_preserves_previous_residency_and_retry_recovers(string kind)
    {
        var failing = false;
        using var http = Client(_ => Task.FromResult(!failing ? Response() : kind switch
        {
            "missing" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "corrupt" => Response([1, 2, 3, 5]),
            "short" => Response([1]),
            "long" => Response([1, 2, 3, 4, 5]),
            _ => new(HttpStatusCode.OK) { Content = new StringContent("oops") }
        }));
        var cache = new Cache { CanStore = false };
        var stager = new AssetStager(http, cache);
        var old = await stager.StageAsync(Manifest());
        failing = true;
        await Assert.ThrowsAnyAsync<Exception>(() => stager.StageAsync(Manifest()));
        Assert.Same(old, stager.Current);
        failing = false;
        Assert.NotSame(old, await stager.StageAsync(Manifest()));
    }
    [Fact]
    public async Task Corrupt_persistent_bytes_are_removed_and_evicted_bytes_are_downloaded_again()
    {
        int requests = 0;
        var cache = new Cache();
        cache.Bytes[Asset().Sha256] = [8, 8, 8, 8];
        using var http = Client(_ => { requests++; return Task.FromResult(Response()); });
        var stager = new AssetStager(http, cache);
        await stager.StageAsync(Manifest());
        Assert.Equal(1, cache.Removed);
        await stager.StageAsync(Manifest());
        Assert.Equal(1, requests);
        cache.Bytes.Clear(); // Browser/OS may evict immutable cache entries between visits.
        await stager.StageAsync(Manifest());
        Assert.Equal(2, requests);
    }
    [Fact]
    public async Task Quota_refusal_keeps_verified_residency_without_claiming_persistent_bytes()
    {
        var cache = new Cache { CanStore = false };
        using var http = Client(_ => Task.FromResult(Response()));
        var source = await new AssetStager(http, cache).StageAsync(Manifest());
        Assert.True(source.Exists(Asset().Source));
        Assert.Empty(cache.Bytes);
        Assert.Equal(1, cache.Commits);
    }
    [Fact]
    public async Task Aborted_transfer_never_promotes_or_caches_partial_bytes_and_retry_is_fresh()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stalled = true;
        var cache = new Cache();
        using var http = Client(async token =>
        {
            if (stalled) { requested.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Response();
        });
        var stager = new AssetStager(http, cache);
        using var abort = new CancellationTokenSource();
        var task = stager.StageAsync(Manifest(), cancellationToken: abort.Token);
        await requested.Task;
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Null(stager.Current);
        Assert.Empty(cache.Bytes);
        Assert.Equal(0, cache.Commits);
        stalled = false;
        Assert.NotNull(await stager.StageAsync(Manifest()));
    }
    [Fact]
    public async Task Inflight_download_is_shared_and_one_canceled_waiter_does_not_abort_the_other()
    {
        int requests = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client(async token => { requests++; await gate.Task.WaitAsync(token); return Response(); });
        var stager = new AssetStager(http, new Cache());
        using var abort = new CancellationTokenSource();
        var one = stager.StageAsync(Manifest(), cancellationToken: abort.Token);
        var two = stager.StageAsync(Manifest());
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        gate.SetResult();
        Assert.NotNull(await two);
        Assert.Equal(1, requests);
        Assert.NotNull(stager.Current);
    }
    [Fact]
    public async Task Shared_download_does_not_bypass_a_second_manifests_size_contract()
    {
        int requests = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client(async token => { requests++; await gate.Task.WaitAsync(token); return Response(); });
        var stager = new AssetStager(http, new Cache());
        var valid = stager.StageAsync(Manifest());
        var invalid = stager.StageAsync(Manifest(Asset() with { ByteSize = Bytes.Length - 1 }));
        gate.SetResult();
        Assert.NotNull(await valid);
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid);
        Assert.Equal(1, requests);
        Assert.Null(stager.Current); // The newest requested generation failed.
    }
    [Fact]
    public void Manifest_canonicalizes_same_content_aliases_and_rejects_ambiguity_and_missing_dependencies()
    {
        var entry = Asset();
        Manifest(entry, entry with { Source = entry.Source.ToUpperInvariant() }).Validate();
        Assert.Throws<InvalidDataException>(() => Manifest(entry, Asset(entry.Source.ToUpperInvariant(), [9])).Validate());
        Assert.Throws<InvalidDataException>(() => Manifest(entry with { Dependencies = ["lost.mdl"] }).Validate());
        Assert.Throws<InvalidDataException>(() => Manifest(entry, entry with { Source = "other.mdl", ByteSize = 9 }).Validate());
        foreach (string invalid in new[] { "../secret", "/root.mdl", "a\\b.mdl", "a%2eb.mdl", "a//b.mdl", "x/../b.mdl" })
            Assert.Throws<InvalidDataException>(() => Manifest(entry with { Source = invalid }).Validate());
        Assert.Throws<InvalidDataException>(() => Manifest(entry with { Provenance = "" }).Validate());
        Assert.Throws<InvalidDataException>(() => new StagedManifest(1, [entry]).Validate());
        Assert.Throws<InvalidDataException>(() => new StagedManifest(2, []).Validate());
    }
    private sealed class Cache : IImmutableAssetCache
    {
        internal readonly Dictionary<string, byte[]> Bytes = new();
        internal bool CanStore = true;
        internal int Removed, Commits;
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken) => ValueTask.FromResult(Bytes.GetValueOrDefault(hash));
        public ValueTask RemoveAsync(string hash, CancellationToken cancellationToken) { Removed++; Bytes.Remove(hash); return ValueTask.CompletedTask; }
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken cancellationToken)
        { if (CanStore) Bytes[asset.Sha256] = bytes; return ValueTask.FromResult(CanStore); }
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken cancellationToken) { Commits++; return ValueTask.CompletedTask; }
    }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(cancellationToken);
    }
}
