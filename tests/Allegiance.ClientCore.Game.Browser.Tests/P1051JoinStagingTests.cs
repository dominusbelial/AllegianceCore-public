using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1051JoinStagingTests
{
    private static StagedAsset Entry(string name, byte[] bytes) => new(name,
        "packs/" + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
        Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/octet-stream", "fixture", []);

    [Fact]
    public async Task Warm_starter_retains_join_hashes_then_complete_join_fetches_no_pack_bytes()
    {
        byte[] starter = [1, 2, 3], flight = [4, 5, 6];
        var first = Entry("starter.mdl", starter); var second = Entry("flight.mdl", flight);
        var cache = new Cache(); cache.Files[first.Sha256] = starter; cache.Files[second.Sha256] = flight;
        int fetched = 0;
        using var http = Http(_ => { fetched++; throw new InvalidOperationException("Warm visit must not fetch assets"); });
        var loader = new AssetStager(http, cache) { RetainedHashes = [first.Sha256, second.Sha256] };
        await loader.StageAsync(new(2, [first]));
        Assert.Contains(second.Sha256, cache.Files.Keys);
        var resident = await loader.StageAsync(new(2, [first, second]));
        Assert.Equal(0, fetched); Assert.Equal(6, resident.ResidentBytes);
        Assert.Equal(new[] { first.Sha256, second.Sha256 }, cache.Retained);
    }
    [Fact]
    public async Task One_stale_file_refetches_only_that_file_and_storage_refusal_still_starts_from_memory()
    {
        byte[] one = [1, 2, 3], two = [4, 5, 6];
        var first = Entry("one.mdl", one); var second = Entry("two.mdl", two);
        var cache = new Cache { Refuse = true };
        cache.Files[first.Sha256] = one; cache.Files[second.Sha256] = [4, 5, 0];
        var fetched = new List<string>();
        using var http = Http(request => { fetched.Add(request.RequestUri!.AbsolutePath); return Response(two); });
        var resident = await new AssetStager(http, cache).StageAsync(new(2, [first, second]));
        Assert.Equal(["/" + second.Path], fetched);
        Assert.DoesNotContain(second.Sha256, cache.Files.Keys);
        using var stream = resident.OpenRead("two.mdl");
        using var output = new MemoryStream(); stream.CopyTo(output);
        Assert.Equal(two, output.ToArray());
    }
    [Fact]
    public async Task Progress_identifies_file_MB_and_ETA_and_retry_reuses_completed_files()
    {
        byte[] one = [1, 2, 3], two = [4, 5, 6];
        var first = Entry("one.mdl", one); var second = Entry("two.mdl", two);
        var cache = new Cache(); bool fail = true; int firstFetches = 0;
        using var http = Http(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(first.Sha256, StringComparison.Ordinal)) { firstFetches++; return Response(one); }
            return fail ? new(HttpStatusCode.ServiceUnavailable) : Response(two);
        });
        var loader = new AssetStager(http, cache);
        var progress = new List<string>();
        await Assert.ThrowsAsync<HttpRequestException>(() => loader.StageAsync(new(2, [first, second]), (_, status) => progress.Add(status)));
        Assert.Null(loader.Current); Assert.Contains(progress, line => line.Contains("two.mdl", StringComparison.Ordinal) && line.Contains(" MB · ETA ", StringComparison.Ordinal));
        fail = false;
        var resident = await loader.StageAsync(new(2, [first, second]));
        Assert.Equal(1, firstFetches); Assert.Equal(6, resident.ResidentBytes);
    }
    private static HttpResponseMessage Response(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); return response;
    }
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(new Handler(reply)) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private sealed class Cache : IImmutableAssetCache
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public string[] Retained { get; private set; } = [];
        public bool Refuse { get; init; }
        public ValueTask<byte[]?> ReadAsync(string hash, CancellationToken token) => ValueTask.FromResult(Files.GetValueOrDefault(hash));
        public ValueTask RemoveAsync(string hash, CancellationToken token) { Files.Remove(hash); return ValueTask.CompletedTask; }
        public ValueTask<bool> PutAsync(StagedAsset asset, byte[] bytes, CancellationToken token) { if (!Refuse) Files[asset.Sha256] = bytes; return ValueTask.FromResult(!Refuse); }
        public ValueTask CommitManifestAsync(string identity, string[] hashes, CancellationToken token)
        { Retained = hashes; foreach (string key in Files.Keys.Except(hashes).ToArray()) Files.Remove(key); return ValueTask.CompletedTask; }
    }
}
