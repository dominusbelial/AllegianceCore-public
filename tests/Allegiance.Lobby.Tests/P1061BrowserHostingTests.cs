using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.Lobby.BrowserPreview;

namespace Allegiance.Lobby.Tests;

/// <summary>P1061 — the production <c>/play/</c> contract: public prefix, host cache classes,
/// extensionless content-addressed packs, the same-origin WSS CSP entry and the API-limiter exemption.</summary>
public sealed class P1061BrowserHostingTests
{
    [Fact]
    public async Task Production_prefix_serves_every_generation_class_with_the_host_headers()
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        using var page = await client.GetAsync("/play/menu.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl!.NoStore);
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", csp);
        Assert.Contains("connect-src 'self' wss://game.invalid/game;", csp);
        Assert.DoesNotContain("wss://evil.invalid", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        using var manifest = await client.GetAsync("/play/app-manifest.json");
        Assert.True(manifest.Headers.CacheControl!.NoStore);
        using var stable = await client.GetAsync("/play/stable.js");
        Assert.True(stable.Headers.CacheControl!.NoCache);
        using var wasm = await client.GetAsync("/play/_framework/test.123456789a.wasm");
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", wasm.Headers.CacheControl!.ToString());
        // The published pack has no extension; it is the only extensionless entry served.
        using var pack = await client.GetAsync("/play/packs/" + Bundle.PackHash);
        Assert.Equal(HttpStatusCode.OK, pack.StatusCode);
        Assert.Equal("application/octet-stream", pack.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", pack.Headers.CacheControl!.ToString());
        Assert.Equal(Bundle.Pack, await pack.Content.ReadAsByteArrayAsync());
        using var unknown = await client.GetAsync("/play/no-extension");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var art = await client.GetAsync("/play/" + fixture.Asset.Path);
        Assert.Equal(Bundle.Artwork, await art.Content.ReadAsByteArrayAsync());
        Assert.Contains(art.Headers.CacheControl!.Extensions, e => e.Name == "immutable");
    }

    [Theory]
    [InlineData("/play/_framework/test.123456789a.wasm", "gzip", "gzip")]
    [InlineData("/play/_framework/test.123456789a.wasm", "br", "br")]
    [InlineData("/play/_framework/test.123456789a.wasm", "", "identity")]
    // The publication tool writes gzip sidecars for packs, never brotli; a br-only
    // client still gets the identity representation rather than a made-up encoding.
    [InlineData("/play/packs/4a12b1810a1372005540c84ba00e0fbb8c3199892b475fb89594a6cceb8ec422", "gzip", "gzip")]
    [InlineData("/play/packs/4a12b1810a1372005540c84ba00e0fbb8c3199892b475fb89594a6cceb8ec422", "br", "identity")]
    [InlineData("/play/packs/4a12b1810a1372005540c84ba00e0fbb8c3199892b475fb89594a6cceb8ec422", "", "identity")]
    public async Task Pack_and_framework_negotiate_one_precompressed_representation(string path, string accept, string expected)
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accept.Length > 0) request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        var actual = await response.Content.ReadAsByteArrayAsync();
        if (expected == "identity") Assert.Empty(response.Content.Headers.ContentEncoding);
        else
        {
            Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentEncoding));
            using var input = new MemoryStream(actual);
            using Stream decoder = expected == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); await decoder.CopyToAsync(output); actual = output.ToArray();
        }
        Assert.Equal(path.Contains("packs/", StringComparison.Ordinal) ? Bundle.Pack : Bundle.Wasm, actual);
    }

    [Fact]
    public async Task Production_prefix_redirects_and_never_falls_back_to_an_entry_page()
    {
        using var fixture = new Bundle(); using var app = fixture.App();
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var bare = await client.GetAsync("/play");
        Assert.Equal(HttpStatusCode.MovedPermanently, bare.StatusCode);
        Assert.Equal("/play/", bare.Headers.Location!.OriginalString);
        foreach (var path in new[] { "/play/unknown", "/play/preview/index.html", "/play/api", "/play/packs/" + new string('a', 63), "/play/packs/" + new string('z', 64) })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        // The lobby's own CSP still covers API responses; the play policy is scoped to the bundle.
        using var api = await client.GetAsync("/api/servers");
        Assert.DoesNotContain("wasm-unsafe-eval", api.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Static_bundle_requests_never_spend_the_global_API_budget_at_the_production_prefix()
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        for (var i = 0; i < 150; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/play/packs/" + Bundle.PackHash)).StatusCode);
        for (var i = 0; i < 120; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/play/stable.js")).StatusCode);
    }

    [Theory]
    [InlineData("/play", "/game", true)]
    [InlineData("play", "/game", false)]
    [InlineData("/", "/game", false)]
    [InlineData("/api", "/game", false)]
    [InlineData("/play/", "/game", false)]
    [InlineData("/play", "game", false)]
    [InlineData("/play", "/ga me", false)]
    public void Prefix_and_route_validation_is_closed(string prefix, string route, bool valid)
    {
        var settings = new BrowserPreviewSettings { Enabled = true, Prefix = prefix, WebSocketPath = route };
        if (valid) settings.Validate();
        else Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Theory]
    [InlineData("https://game.invalid", true)]
    [InlineData("https://game.invalid:8443", true)]
    [InlineData("http://game.invalid", false)]
    [InlineData("https://game.invalid/play", false)]
    [InlineData("https://game.invalid?x=1", false)]
    [InlineData("https://user@game.invalid", false)]
    public void Public_origin_must_be_one_canonical_https_origin(string origin, bool valid)
    {
        var settings = new BrowserPreviewSettings { Enabled = true, PublicOrigin = origin };
        if (valid) settings.Validate();
        else Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void Same_origin_policy_has_no_wss_entry_and_no_foreign_host()
    {
        var settings = new BrowserPreviewSettings();
        Assert.Equal(BrowserPreviewHosting.ContentSecurityPolicy, BrowserPreviewHosting.ContentSecurityPolicyFor(settings));
        var publicSettings = new BrowserPreviewSettings { PublicOrigin = "https://game.invalid:8443", WebSocketPath = "/game" };
        Assert.Contains("connect-src 'self' wss://game.invalid:8443/game;", BrowserPreviewHosting.ContentSecurityPolicyFor(publicSettings));
    }

    private sealed class Bundle : IDisposable
    {
        internal static readonly byte[] Wasm = Encoding.UTF8.GetBytes("test wasm content");
        internal static readonly byte[] Artwork = [1, 2, 3, 4];
        internal static readonly byte[] Pack = Encoding.UTF8.GetBytes(new string('p', 4096));
        internal static readonly string PackHash = Convert.ToHexStringLower(SHA256.HashData(Pack));
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1061", Guid.NewGuid().ToString("N"));
        internal string ArtRoot => Path.Combine(Root, "corpus");
        internal PreviewAsset Asset { get; }
        internal Bundle()
        {
            Directory.CreateDirectory(ArtRoot);
            Directory.CreateDirectory(Path.Combine(Root, "_framework"));
            Directory.CreateDirectory(Path.Combine(Root, "packs"));
            File.WriteAllText(Path.Combine(Root, "index.html"), "<!doctype html><base href='./'>Production");
            File.WriteAllText(Path.Combine(Root, "menu.html"), "<!doctype html><base href='./'>Menu");
            File.WriteAllText(Path.Combine(Root, "stable.js"), "// stable-name compatibility file");
            File.WriteAllText(Path.Combine(Root, "app-manifest.json"), "{\"version\":1,\"files\":{}}");
            File.WriteAllBytes(Path.Combine(Root, "packs", PackHash), Pack);
            File.WriteAllBytes(Path.Combine(Root, "packs", PackHash + ".gz"), Gzip(Pack));
            var wasm = Path.Combine(Root, "_framework/test.123456789a.wasm");
            File.WriteAllBytes(wasm, Wasm);
            File.WriteAllBytes(wasm + ".br", Brotli(Wasm));
            File.WriteAllBytes(wasm + ".gz", Gzip(Wasm));
            var sha = Convert.ToHexStringLower(SHA256.HashData(Artwork));
            Asset = new($"art/fig05.{sha}.mdl", "fig05.mdl", Artwork.Length, sha, "application/octet-stream");
            File.WriteAllText(Path.Combine(Root, "asset-manifest.json"), JsonSerializer.Serialize(new PreviewManifest(1, [Asset]), PreviewJson.Default.PreviewManifest));
            File.WriteAllBytes(Path.Combine(ArtRoot, "fig05.mdl"), Artwork);
        }
        internal LobbyTestApp App() => new()
        {
            BrowserPreviewConfiguration = new BrowserPreviewSettings
            {
                Enabled = true, Prefix = "/play", BundlePath = Root, ArtworkPath = ArtRoot,
                PublicOrigin = "https://game.invalid", WebSocketPath = "/game",
            },
        };
        private static byte[] Gzip(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var writer = new GZipStream(output, CompressionLevel.Fastest)) writer.Write(bytes);
            return output.ToArray();
        }
        private static byte[] Brotli(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var writer = new BrotliStream(output, CompressionLevel.Fastest)) writer.Write(bytes);
            return output.ToArray();
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
