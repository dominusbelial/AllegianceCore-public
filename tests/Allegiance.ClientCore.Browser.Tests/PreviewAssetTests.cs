using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.Browser.Assets;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class PreviewAssetTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static PreviewAsset Entry()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Bytes));
        return new($"art/fig05.{sha}.mdl", "fig05.mdl", Bytes.Length, sha, "application/octet-stream");
    }

    [Theory]
    [InlineData("../fig05.mdl")]
    [InlineData("/fig05.mdl")]
    [InlineData("FIG05.mdl")]
    [InlineData("art\\fig05.mdl")]
    [InlineData("fig05%2emdl")]
    public void Manifest_rejects_noncanonical_sources(string source)
        => Assert.Throws<InvalidDataException>(() => new PreviewManifest(1, [Entry() with { Source = source }]).Validate());

    [Fact]
    public void Manifest_enforces_fingerprint_bounds_type_and_unique_names()
    {
        var asset = Entry();
        new PreviewManifest(1, [asset]).Validate();
        foreach (var bad in new[] { asset with { Path = "art/fig05.mdl" }, asset with { ByteSize = 0 },
            asset with { ByteSize = 4 * 1024 * 1024 + 1 }, asset with { Sha256 = "bad" },
            asset with { ContentType = "text/html" } })
            Assert.Throws<InvalidDataException>(() => new PreviewManifest(1, [bad]).Validate());
        Assert.Throws<InvalidDataException>(() => new PreviewManifest(1, [asset, asset]).Validate());
        Assert.Throws<InvalidDataException>(() => new PreviewManifest(2, [asset]).Validate());
        Assert.Throws<InvalidDataException>(() => new PreviewManifest(1, []).Validate());
    }

    [Fact]
    public async Task Hash_mismatch_is_a_load_error_and_retry_fetches_fresh_verified_bytes()
    {
        var asset = Entry();
        var corrupt = true;
        var downloads = 0;
        var bypassCache = false;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("asset-manifest.json", StringComparison.Ordinal))
                return Json(new PreviewManifest(1, [asset]));
            downloads++;
            // A cached corrupt response persists until the platform requests a fresh fetch.
            return Binary(corrupt && request.Headers.CacheControl?.NoCache != true ? [1, 2, 3, 5] : Bytes);
        })) { BaseAddress = new Uri("http://localhost/play/preview/") };
        var loader = new PreviewAssetLoader(http, request =>
        {
            if (bypassCache) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        });
        var progress = new List<int>();
        Assert.Null(await loader.LoadAsync((p, _) => progress.Add(p)));
        Assert.Contains("verification failed", loader.Error);
        Assert.False(loader.IsLoading);
        bypassCache = true;
        var staged = await loader.LoadAsync((p, _) => progress.Add(p));
        Assert.Null(loader.Error);
        Assert.Equal(Bytes, staged![asset.Source]);
        Assert.Equal(2, downloads);
        Assert.Equal(100, progress[^1]);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("type")]
    [InlineData("missing")]
    [InlineData("manifest")]
    public async Task Invalid_responses_never_publish_partial_assets(string failure)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("asset-manifest.json", StringComparison.Ordinal))
                return failure == "manifest" ? new(HttpStatusCode.OK) { Content = new StringContent("{broken") } : Json(new(1, [Entry()]));
            return failure switch
            {
                "short" => Binary([1]),
                "long" => Binary([1, 2, 3, 4, 5]),
                "type" => new(HttpStatusCode.OK) { Content = new StringContent("oops") },
                _ => new(HttpStatusCode.NotFound)
            };
        })) { BaseAddress = new Uri("http://localhost/play/preview/") };
        var loader = new PreviewAssetLoader(http);
        Assert.Null(await loader.LoadAsync((_, _) => { }));
        Assert.NotNull(loader.Error);
        Assert.False(loader.IsLoading);
    }

    private static HttpResponseMessage Json(PreviewManifest manifest) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(manifest, PreviewJson.Default.PreviewManifest)) };
    private static HttpResponseMessage Binary(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
