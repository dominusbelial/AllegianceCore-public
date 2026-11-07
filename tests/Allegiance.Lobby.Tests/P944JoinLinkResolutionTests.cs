using System.Net;
using System.Net.Http.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

public sealed class P944JoinLinkResolutionTests
{
    private static JoinLink Parse(string lobby, string id)
    {
        Assert.True(JoinLink.TryParse($"allegiance://join?lobby={Uri.EscapeDataString(lobby)}&id={id}&v=1&address=evil.example&port=9&cookie=1", out var link));
        return link!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_uses_current_lobby_data_and_requires_explicit_confirmation(bool playerHosted)
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        // Registry fixture feeds the public endpoint; player-host registration's
        // endpoint-receipt checks remain covered by P936NatSignalingTests.
        var reg = app.Services.GetRequiredService<ServerRegistry>().Register(new RegisterRequest
        {
            Name = "<hostile> --auto-join", Address = "127.0.0.1", Port = 54321, UdpPort = 54322,
            PlayerHosted = playerHosted, PlayerCount = 2, MaxPlayers = 8, MissionName = "game",
            Missions = [new() { Cookie = 943, Name = "game", PlayerCount = 2 }, new() { Cookie = 944, Name = "other", PlayerCount = 1 }],
        })!;
        var confirmation = new JoinLinkConfirmation(Parse(http.BaseAddress!.ToString(), reg.Id), "https://remembered.example");
        int calls = 0; ServerListing? joined = null; Uri? lobby = null;
        void Join(ServerListing listing, Uri origin) { calls++; joined = listing; lobby = origin; }
        confirmation.Confirm(Join); Assert.Equal(0, calls);
        await confirmation.ResolveAsync(http);
        Assert.NotNull(confirmation.Listing); Assert.Equal(0, calls);
        Assert.True(confirmation.DifferentLobby);
        Assert.Equal(2, confirmation.MissionCount);
        confirmation.SelectMission(1); Assert.Equal(944u, confirmation.Listing!.MissionCookie);
        confirmation.SelectMission(-1); Assert.Equal(943u, confirmation.Listing!.MissionCookie);
        confirmation.Confirm(Join); confirmation.Confirm(Join);
        Assert.Equal(1, calls); Assert.NotNull(joined);
        Assert.Equal("127.0.0.1", joined.Address); Assert.Equal(54321, joined.Port);
        Assert.Equal(54322, joined.UdpPort); Assert.Equal(playerHosted, joined.PlayerHosted);
        Assert.Equal(943u, joined.MissionCookie);
        Assert.Null(joined.Missions);
        Assert.Equal(http.BaseAddress, lobby);
    }

    [Fact]
    public async Task Cancel_before_or_after_resolution_never_joins()
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        var reg = app.Services.GetRequiredService<ServerRegistry>().Register(new RegisterRequest { Name = "test", Address = "127.0.0.1", Port = 54321 })!;
        foreach (bool resolveFirst in new[] { true, false })
        {
            var confirmation = new JoinLinkConfirmation(Parse(http.BaseAddress!.ToString(), reg.Id), http.BaseAddress.ToString());
            Assert.False(confirmation.DifferentLobby);
            Assert.Contains(http.BaseAddress!.Host, confirmation.LobbyDescription);
            if (resolveFirst) await confirmation.ResolveAsync(http);
            confirmation.Cancel();
            await confirmation.ResolveAsync(http);
            confirmation.Confirm((_, _) => Assert.Fail("Cancelled Join link connected"));
            Assert.True(confirmation.Closed);
        }
    }

    [Fact]
    public async Task Unknown_and_expired_listings_show_a_recoverable_message()
    {
        var clock = new FakeTimeProvider();
        using var app = new LobbyTestApp(clock, settings => settings.MaxAgeSeconds = 1); using var http = app.CreateClient();
        var reg = app.Services.GetRequiredService<ServerRegistry>().Register(new RegisterRequest { Name = "old", Address = "127.0.0.1", Port = 54321 })!;
        clock.Advance(TimeSpan.FromSeconds(2));
        foreach (string id in new[] { "unknown", reg.Id })
        {
            var confirmation = new JoinLinkConfirmation(Parse(http.BaseAddress!.ToString(), id), "https://saved.example");
            await confirmation.ResolveAsync(http);
            Assert.Null(confirmation.Listing); Assert.False(confirmation.Resolving);
            Assert.Contains("unknown or has expired", confirmation.Message);
            confirmation.Confirm((_, _) => Assert.Fail("Missing listing connected"));
        }
    }

    [Fact]
    public async Task Unreachable_lobby_is_recoverable()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var http = new HttpClient();
        var confirmation = new JoinLinkConfirmation(Parse($"http://127.0.0.1:{port}", "missing"), "https://saved.example");
        await confirmation.ResolveAsync(http);
        Assert.Null(confirmation.Listing); Assert.False(confirmation.Resolving);
        Assert.Contains("could not be reached", confirmation.Message);
    }
    [Theory]
    [InlineData("{not-json}", "application/json", 200)]
    [InlineData("<html>error</html>", "text/html", 200)]
    [InlineData("null", "application/json", 200)]
    [InlineData("{}", "application/json", 200)]
    [InlineData("{}", "application/json", 302)]
    [InlineData("{}", "application/json", 500)]
    public async Task Invalid_or_redirected_lobby_response_is_recoverable(string body, string contentType, int status)
    {
        using var http = new HttpClient(new ReplyHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) })));
        var confirmation = new JoinLinkConfirmation(Parse("https://lobby.example.org", "one"), "https://saved.example");
        await confirmation.ResolveAsync(http);
        Assert.Null(confirmation.Listing); Assert.False(confirmation.Resolving);
        Assert.DoesNotContain("Looking up", confirmation.Message);
    }

    [Fact]
    public async Task Resolution_is_bounded_in_size_and_cancellable()
    {
        using var huge = new HttpClient(new ReplyHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 256 * 1024 + 1)) })));
        var oversized = new JoinLinkConfirmation(Parse("https://lobby.example.org", "one"), "https://saved.example");
        await oversized.ResolveAsync(huge);
        Assert.Null(oversized.Listing); Assert.Contains("could not be reached", oversized.Message);
        using var slow = new HttpClient(new ReplyHandler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var pending = new JoinLinkConfirmation(Parse("https://lobby.example.org", "one"), "https://saved.example");
        await pending.ResolveAsync(slow, cancel.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.Resolving); Assert.Null(pending.Listing);
        Assert.Contains("could not be reached", pending.Message);
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => reply(request, cancellationToken);
    }

}
