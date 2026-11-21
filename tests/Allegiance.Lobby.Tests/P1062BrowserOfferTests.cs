using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.Lobby.BrowserPreview;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P1062 — the lobby side of the browser offer. <see cref="BrowserReturnTarget"/> is the
/// only rule that lets the identity page leave for another location, so it is tested
/// exhaustively (the open-redirect cases included) before the endpoint tests that drive
/// the enabled/disabled visibility of the identity page's action and the server list's
/// per-game link.
/// </summary>
public sealed class P1062BrowserOfferTests
{
    private const string Prefix = "/play";

    [Theory]
    [InlineData("/play/")]
    [InlineData("/play/?online=1")]
    [InlineData("/play/?online=1&server=6f1c6d1e-6a5f-4a63-9d0e-2d5f6e7a8b90")]
    [InlineData("/play/index.html?online=1")]
    [InlineData("/play/preview/?online=1")]
    public void Path_only_targets_under_the_prefix_are_accepted_unchanged(string raw)
    {
        Assert.Equal(raw, BrowserReturnTarget.Normalize(raw, Prefix));
    }

    [Theory]
    // Not under the browser prefix (and nothing is repaired into it).
    [InlineData("https://game.invalid/play/")]
    [InlineData("http://game.invalid/")]
    [InlineData("//game.invalid/play/")]
    [InlineData("///game.invalid/play/")]
    [InlineData("/\\game.invalid/play/")]
    [InlineData("/play/\\\\game.invalid/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/play:/x")]
    [InlineData("/play?online=1")]
    [InlineData("/player/?online=1")]
    [InlineData("/PLAY/?online=1")]
    [InlineData("play/?online=1")]
    [InlineData("/play/./index.html")]
    [InlineData("/play/../api/servers")]
    [InlineData("/play/..")]
    [InlineData("/play//game.invalid/")]
    // Encoded variants: a second decoding layer is refused outright, not decoded.
    [InlineData("/play/%2F%2Fgame.invalid/")]
    [InlineData("/play/%252F%252Fgame.invalid/")]
    [InlineData("/play/%2e%2e/api/servers")]
    [InlineData("/play/?x=%2F%2Fgame.invalid")]
    // Fragments, whitespace and control characters.
    [InlineData("/play/#fragment")]
    [InlineData("/play/?online=1#fragment")]
    [InlineData("/play/#")]
    [InlineData("/play/ ?online=1")]
    [InlineData(" /play/?online=1")]
    [InlineData("/play/\t?online=1")]
    [InlineData("/play/?online=1\n")]
    [InlineData("/play/?")]
    // Empty and absent.
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused_with_no_repair(string? raw)
    {
        Assert.Null(BrowserReturnTarget.Normalize(raw, Prefix));
    }

    [Fact]
    public void Oversize_target_is_refused()
    {
        string raw = "/play/?online=1&x=" + new string('a', BrowserReturnTarget.MaxLength);
        Assert.Null(BrowserReturnTarget.Normalize(raw, Prefix));
    }

    [Fact]
    public void Provider_callback_page_carries_only_a_validated_target()
    {
        Assert.Equal("/identity.html?return=%2Fplay%2Fmenu.html%3Fonline%3D1",
            BrowserReturnTarget.IdentityPage("/play/menu.html?online=1", Prefix));
        Assert.Equal("/identity.html", BrowserReturnTarget.IdentityPage("https://game.invalid/play/", Prefix));
        Assert.Equal("/identity.html", BrowserReturnTarget.IdentityPage("//game.invalid/play/", Prefix));
        Assert.Equal("/identity.html", BrowserReturnTarget.IdentityPage(null, Prefix));
    }

    [Fact]
    public async Task Enabled_build_answers_the_prefix_and_only_validated_return_targets()
    {
        using var bundle = new Bundle();
        using var app = bundle.App();
        using var client = app.CreateClient();

        var accepted = await client.GetFromJsonAsync<BrowserOffer>("/api/browser?return=%2Fplay%2F%3Fonline%3D1");
        Assert.NotNull(accepted);
        Assert.True(accepted!.Enabled);
        Assert.Equal(Prefix, accepted.Prefix);
        Assert.Equal("/play/?online=1", accepted.ReturnTarget);

        foreach (var hostile in new[] { "https%3A%2F%2Fgame.invalid%2F", "%2F%2Fgame.invalid%2F", "%2Fplay%2F%5C%5Cgame.invalid", "%2Fplay%2F%252Fx" })
        {
            var refused = await client.GetFromJsonAsync<BrowserOffer>("/api/browser?return=" + hostile);
            Assert.NotNull(refused);
            Assert.True(refused!.Enabled);
            Assert.Null(refused.ReturnTarget);
        }

        var plain = await client.GetFromJsonAsync<BrowserOffer>("/api/browser");
        Assert.NotNull(plain);
        Assert.Null(plain!.ReturnTarget);
    }

    [Fact]
    public async Task Disabled_build_advertises_nothing_and_hides_both_actions()
    {
        using var app = new LobbyTestApp
        {
            BrowserPreviewConfiguration = new BrowserPreviewSettings { Enabled = false, Prefix = Prefix },
        };
        using var client = app.CreateClient();

        var offer = await client.GetFromJsonAsync<BrowserOffer>("/api/browser?return=%2Fplay%2F%3Fonline%3D1");
        Assert.NotNull(offer);
        Assert.False(offer!.Enabled);
        Assert.Equal("", offer.Prefix);
        Assert.Null(offer.ReturnTarget);

        // The pages decide from that answer alone: the identity action starts hidden and
        // the server list only builds a browser link when the answer is enabled.
        AssertHiddenIdentityAction(await client.GetStringAsync("/identity.html"));
        string script = await client.GetStringAsync("/identity.js");
        Assert.Contains("/api/browser", script);
        Assert.Contains("offer.returnTarget", script);
        Assert.Contains("location.replace", script);
        string list = await client.GetStringAsync("/app.js");
        Assert.Contains("offer.enabled", list);
        Assert.Contains("webSocketUrl", list);
    }

    [Fact]
    public async Task Server_list_shows_the_browser_link_only_for_a_game_with_a_web_socket_route()
    {
        using var bundle = new Bundle();
        using var app = bundle.App();
        using var client = app.CreateClient();
        string html = await client.GetStringAsync("/");
        Assert.Contains("<th>Play in browser</th>", html);
        string script = await client.GetStringAsync("/app.js");
        // One link per listed game, same listing id the desktop Join link names, and only
        // when the listing advertises a route.
        Assert.Contains("browserCell", script);
        Assert.Contains("Play in browser", script);
        Assert.Contains("server.webSocketUrl.length === 0", script);
        Assert.Contains("offer.prefix + '/menu.html?online=1&server=' + encodeURIComponent(server.id)", script);
        Assert.Contains("colSpan = 10", script);
        Assert.DoesNotContain("innerHTML", script);
    }

    private static void AssertHiddenIdentityAction(string html)
    {
        int start = html.IndexOf("<a id=\"play-browser\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the identity page carries the browser action");
        int end = html.IndexOf("</a>", start);
        Assert.Contains("hidden", html[start..end]);
        Assert.Contains(">Play in browser", html[start..end]);
    }

    /// <summary>Smallest enabled browser generation the preview host accepts: an entry
    /// page, one verified artwork entry and the manifest.</summary>
    private sealed class Bundle : IDisposable
    {
        private static readonly byte[] Artwork = [1, 2, 3, 4];
        private string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1062", Guid.NewGuid().ToString("N"));
        private string ArtRoot => Path.Combine(Root, "corpus");
        internal LobbyTestApp App()
        {
            Directory.CreateDirectory(ArtRoot);
            File.WriteAllText(Path.Combine(Root, "index.html"), "<!doctype html><title>preview</title>");
            string sha = Convert.ToHexStringLower(SHA256.HashData(Artwork));
            var asset = new PreviewAsset($"art/fig05.{sha}.mdl", "fig05.mdl", Artwork.Length, sha, "application/octet-stream");
            File.WriteAllText(Path.Combine(Root, "asset-manifest.json"),
                JsonSerializer.Serialize(new PreviewManifest(1, [asset]), PreviewJson.Default.PreviewManifest));
            File.WriteAllBytes(Path.Combine(ArtRoot, "fig05.mdl"), Artwork);
            return new LobbyTestApp
            {
                BrowserPreviewConfiguration = new BrowserPreviewSettings
                {
                    Enabled = true, Prefix = Prefix, BundlePath = Root, ArtworkPath = ArtRoot,
                },
            };
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
