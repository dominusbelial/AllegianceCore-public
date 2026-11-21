using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>
/// P1062 — the browser client's URL contract. <see cref="BrowserEntry.Parse"/> is the
/// auto-pickup: <c>?online=1</c> makes the client open the online panel by itself after
/// the lobby sends the player back, and <c>?server=</c> preselects the listed game.
/// <see cref="BrowserEntry.ReturnTarget"/> is what the client hands the identity page;
/// the lobby validates it again, so this side only ever produces a path under the
/// client's own path.
/// </summary>
public sealed class P1062BrowserEntryTests
{
    [Theory]
    [InlineData("?online=1", true, null)]
    [InlineData("online=1", true, null)]
    [InlineData("?online=1&server=6f1c6d1e-6a5f-4a63-9d0e-2d5f6e7a8b90", true, "6f1c6d1e-6a5f-4a63-9d0e-2d5f6e7a8b90")]
    [InlineData("?server=abc123&online=1", true, "abc123")]
    [InlineData("?online=0", false, null)]
    [InlineData("?online=yes", false, null)]
    [InlineData("?online", false, null)]
    [InlineData("?dev=1", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void Entry_flags_are_read_defensively(string? query, bool online, string? server)
    {
        var entry = BrowserEntry.Parse(query);
        Assert.Equal(online, entry.Online);
        Assert.Equal(server, entry.Server);
    }

    [Theory]
    [InlineData("?server=<script>alert(1)</script>")]
    [InlineData("?server=../api")]
    [InlineData("?server=")]
    [InlineData("?server=" + "0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void An_unusable_server_id_is_ignored(string query)
    {
        Assert.Null(BrowserEntry.Parse(query).Server);
        Assert.False(BrowserEntry.Parse(query).Online);
    }

    [Theory]
    [InlineData("/play/", null, "/play/?online=1")]
    [InlineData("/play/index.html", null, "/play/index.html?online=1")]
    [InlineData("/play/menu.html?dev=1", null, "/play/menu.html?online=1")]
    [InlineData("/play/", "abc123", "/play/?online=1&server=abc123")]
    public void Return_target_is_the_clients_own_path_plus_the_entry_flags(string pathAndQuery, string? server, string expected)
    {
        Assert.Equal(expected, BrowserEntry.ReturnTarget(pathAndQuery, server));
    }

    [Theory]
    // The client never forwards caller text that could name another location.
    [InlineData("https://game.invalid/play/")]
    [InlineData("//game.invalid/play/")]
    [InlineData("/play//game.invalid/")]
    [InlineData("/play/\\game.invalid/")]
    [InlineData("/play/%2F%2Fgame.invalid/")]
    [InlineData("/play/#fragment")]
    [InlineData("")]
    [InlineData(null)]
    public void Odd_paths_fall_back_to_the_root_instead_of_being_forwarded(string? pathAndQuery)
    {
        string target = BrowserEntry.ReturnTarget(pathAndQuery);
        Assert.Equal("/?online=1", target);
        Assert.DoesNotContain("game.invalid", target);
        Assert.DoesNotContain("%", target);
    }

    [Fact]
    public void Sign_in_href_carries_the_target_on_the_lobby_identity_page()
    {
        string href = BrowserEntry.SignInHref("/play/?online=1&server=abc123");
        Assert.Equal("/identity.html?return=%2Fplay%2F%3Fonline%3D1%26server%3Dabc123", href);
    }
}
