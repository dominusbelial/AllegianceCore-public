using Allegiance.ClientCore.Browser.Pages;

namespace Allegiance.ClientCore.Game.Browser.Tests;

/// <summary>P1061 — the public join decision: same-origin HTTPS page and wss route,
/// verified identity, starter core, and a reported build identity when the server
/// enforces a minimum client version. Password games are admitted; the server checks it.</summary>
public sealed class P1061BrowserJoinPolicyTests
{
    private static BrowserServer Server(string? route = "wss://game.invalid/game", bool verified = true,
        string? identity = "id", string core = "PCore001.igc", bool password = false, string? min = null) =>
        new("P1061", "public", 6073, "XXV_simple", core, verified, identity, route, password, min);

    [Theory]
    [InlineData("https://game.invalid", "wss://game.invalid/game")]
    [InlineData("https://game.invalid:8443", "wss://game.invalid:8443/game")]
    [InlineData("http://localhost:8048", "ws://localhost:8048/game")]
    [InlineData("https://localhost:8443", "wss://localhost:8443/game")]
    public void Same_origin_route_on_a_secure_or_loopback_page_is_available(string page, string route)
    {
        Assert.Null(BrowserJoinPolicy.Refusal(new Uri(page), Server(route), hasClientVersion: false));
    }

    [Theory]
    [InlineData("https://game.invalid", "wss://other.invalid/game")]
    [InlineData("https://game.invalid", "wss://game.invalid:8443/game")]
    [InlineData("https://game.invalid:8443", "wss://game.invalid/game")]
    [InlineData("http://game.invalid", "wss://game.invalid/game")]
    [InlineData("https://game.invalid", "ws://game.invalid/game")]
    [InlineData("http://localhost:8048", "ws://127.0.0.1:8048/game")]
    [InlineData("http://localhost:8048", "ws://localhost:8049/game")]
    public void Foreign_origin_scheme_or_port_is_refused(string page, string route)
    {
        Assert.NotNull(BrowserJoinPolicy.Refusal(new Uri(page), Server(route), hasClientVersion: false));
    }

    [Theory]
    [InlineData(false, true, "PCore001.igc", "wss://game.invalid/game")]
    [InlineData(true, false, "PCore001.igc", "wss://game.invalid/game")]
    [InlineData(true, true, "PCore002.igc", "wss://game.invalid/game")]
    public void Unverified_or_wrong_core_is_refused(bool verified, bool hasIdentity, string core, string route)
    {
        Assert.NotNull(BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"),
            Server(route, verified, hasIdentity ? "id" : null, core), hasClientVersion: true));
    }

    [Fact]
    public void Minimum_version_needs_a_reported_build_identity_but_never_bypasses_the_server()
    {
        var server = Server(min: "2.0.0");
        var refusal = BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"), server, hasClientVersion: false);
        Assert.Contains("2.0.0", refusal);
        Assert.Contains("client version", refusal);
        Assert.Null(BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"), server, hasClientVersion: true));
    }

    [Fact]
    public void Password_games_are_admitted_and_the_advertisement_is_not_a_refusal()
    {
        Assert.Null(BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"),
            Server(password: true), hasClientVersion: true));
    }

    [Fact]
    public void Missing_route_is_refused_without_throwing()
    {
        Assert.NotNull(BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"), Server(route: null), hasClientVersion: true));
        Assert.NotNull(BrowserJoinPolicy.Refusal(new Uri("https://game.invalid"), Server(route: "wss://10.0.0.1/game"), hasClientVersion: true));
    }
}
