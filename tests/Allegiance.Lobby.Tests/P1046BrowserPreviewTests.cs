using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.Lobby.BrowserPreview;

namespace Allegiance.Lobby.Tests;

public sealed class P1046BrowserPreviewTests
{
    [Theory]
    [InlineData("/play/preview")]
    [InlineData("/Play/Preview")]
    public async Task Bare_entry_redirects_to_the_canonical_base_path(string path)
    {
        using var fixture = new Bundle(); using var app = fixture.App();
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/play/preview/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Base_path_opens_the_game_menu_and_the_art_preview_stays_at_index_html()
    {
        using var fixture = new Bundle();
        File.WriteAllText(Path.Combine(fixture.Root, "menu.html"), "<!doctype html><base href='/play/preview/'>Menu");
        using var app = fixture.App();
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var root = await client.GetAsync("/play/preview/?dev=1&online=1");
        Assert.Equal(HttpStatusCode.Found, root.StatusCode);
        Assert.Equal("/play/preview/menu.html?dev=1&online=1", root.Headers.Location!.OriginalString);
        using var menu = await client.GetAsync("/play/preview/menu.html");
        Assert.Equal(HttpStatusCode.OK, menu.StatusCode);
        Assert.Contains("Menu", await menu.Content.ReadAsStringAsync());
        using var preview = await client.GetAsync("/play/preview/index.html");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("Preview", await preview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Disabled_route_is_absent_and_existing_lobby_headers_are_unchanged()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        foreach (var path in new[] { "/play/preview", "/play/preview/", "/play/preview/_framework/test.wasm", "/play/preview/asset-manifest.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("wasm-unsafe-eval", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        using var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'", home.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("gzip;q=1,br;q=0", "gzip")]
    [InlineData("br;q=0.5,gzip;q=1", "gzip")]
    [InlineData("br,gzip", "br")]
    [InlineData("br;q=0,gzip;q=0", "identity")]
    [InlineData("", "identity")]
    public async Task Published_wasm_has_correct_mime_single_encoding_vary_and_immutable_cache(string accept, string expected)
    {
        using var fixture = new Bundle();
        using var app = fixture.App();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/play/preview/_framework/test.123456789a.wasm");
        if (accept.Length > 0) request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/wasm", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        var actual = await response.Content.ReadAsByteArrayAsync();
        if (expected != "identity")
        {
            Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentEncoding));
            using var input = new MemoryStream(actual);
            using Stream decoder = expected == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); await decoder.CopyToAsync(output); actual = output.ToArray();
        }
        else Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(Bundle.Wasm, actual);
    }

    [Fact]
    public async Task Launch_manifest_revalidate_CSP_is_scoped_and_unknown_paths_do_not_fall_back()
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        foreach (var path in new[] { "/play/preview/", "/play/preview/asset-manifest.json", "/play/preview/_content/test.js" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // P1061: entry pages and mutable manifests are never stored; stable-name files revalidate.
            if (path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith(".html", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
                Assert.True(response.Headers.CacheControl!.NoStore);
            else Assert.True(response.Headers.CacheControl!.NoCache);
            Assert.Equal(BrowserPreviewHosting.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var cached = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
        }
        using var home = await client.GetAsync("/");
        Assert.DoesNotContain("wasm-unsafe-eval", home.Headers.GetValues("Content-Security-Policy").Single());
        foreach (var path in new[] { "/play/preview/unknown", "/play/preview/api", "/play/preview/art/secret.mdl", "/play/preview/_framework/test.123456789a.wasm.br", "/play/preview/art/%2e%2e%2fsecret.mdl", "/play/preview/art/fig05.mdl", "/play/preview/asset-manifest.json/extra", "/api/unknown" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/play/preview/"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Artwork_allowlist_serves_only_verified_manifest_entry_and_rejects_links()
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        using var response = await client.GetAsync("/play/preview/" + fixture.Asset.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Bundle.Artwork, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(response.Headers.CacheControl!.Extensions, e => e.Name == "immutable");
        File.Delete(Path.Combine(fixture.ArtRoot, "fig05.mdl"));
        File.CreateSymbolicLink(Path.Combine(fixture.ArtRoot, "fig05.mdl"), Path.Combine(fixture.ArtRoot, "secret.mdl"));
        using var linkedApp = fixture.App();
        Assert.Throws<InvalidDataException>(() => linkedApp.CreateClient());
    }

    [Fact]
    public void Corrupted_artwork_fails_closed_at_startup()
    {
        using var fixture = new Bundle();
        File.WriteAllBytes(Path.Combine(fixture.ArtRoot, "fig05.mdl"), [9, 9, 9, 9]);
        using var app = fixture.App();
        Assert.Throws<InvalidDataException>(() => app.CreateClient());
    }

    [Fact]
    public async Task Static_requests_do_not_spend_global_API_budget_and_still_work_after_API_exhaustion()
    {
        using var fixture = new Bundle(); using var app = fixture.App(); using var client = app.CreateClient();
        for (var i = 0; i < 150; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/play/preview/preview.js")).StatusCode);
        for (var i = 0; i < 120; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/play/preview/preview.js")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_identity_limit_origin_body_size_and_untrusted_proxy_rules_remain(bool enabled)
    {
        using var fixture = new Bundle(); using var app = fixture.App(enabled); using var client = app.CreateClient();
        using var large = new HttpRequestMessage(HttpMethod.Post, "/api/servers/register") { Content = new StringContent(new string('x', 16385)) };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(large)).StatusCode);
        using var origin = new HttpRequestMessage(HttpMethod.Post, "/api/identity/signout");
        origin.Headers.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(origin)).StatusCode);
        // The rejected identity write also consumes one permit, exactly as before.
        for (var i = 0; i < 29; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/identity/me")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/identity/me")).StatusCode);
        using var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        spoof.Headers.Add("X-Forwarded-For", "192.0.2.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(spoof)).StatusCode);
    }

    private sealed class Bundle : IDisposable
    {
        internal static readonly byte[] Wasm = Encoding.UTF8.GetBytes("test wasm content");
        internal static readonly byte[] Artwork = [1, 2, 3, 4];
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1046", Guid.NewGuid().ToString("N"));
        internal string ArtRoot => Path.Combine(Root, "corpus");
        internal PreviewAsset Asset { get; }
        internal Bundle()
        {
            Directory.CreateDirectory(ArtRoot);
            Directory.CreateDirectory(Path.Combine(Root, "_framework"));
            Directory.CreateDirectory(Path.Combine(Root, "_content"));
            File.WriteAllText(Path.Combine(Root, "index.html"), "<!doctype html><base href='/play/preview/'>Preview");
            File.WriteAllText(Path.Combine(Root, "preview.js"), "// preview");
            File.WriteAllText(Path.Combine(Root, "_content/test.js"), "// interop");
            var wasm = Path.Combine(Root, "_framework/test.123456789a.wasm");
            File.WriteAllBytes(wasm, Wasm);
            foreach (var br in new[] { true, false })
            {
                using var output = File.Create(wasm + (br ? ".br" : ".gz"));
                using Stream encoder = br ? new BrotliStream(output, CompressionLevel.Fastest) : new GZipStream(output, CompressionLevel.Fastest);
                encoder.Write(Wasm);
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(Artwork));
            Asset = new($"art/fig05.{sha}.mdl", "fig05.mdl", Artwork.Length, sha, "application/octet-stream");
            File.WriteAllText(Path.Combine(Root, "asset-manifest.json"), JsonSerializer.Serialize(new PreviewManifest(1, [Asset]), PreviewJson.Default.PreviewManifest));
            File.WriteAllBytes(Path.Combine(ArtRoot, "fig05.mdl"), Artwork);
            File.WriteAllText(Path.Combine(ArtRoot, "secret.mdl"), "private artwork outside manifest");
        }
        internal LobbyTestApp App(bool enabled = true) => new() { BrowserPreviewConfiguration = new BrowserPreviewSettings { Enabled = enabled, BundlePath = Root, ArtworkPath = ArtRoot } };
        public void Dispose() => Directory.Delete(Root, true);
    }
}
