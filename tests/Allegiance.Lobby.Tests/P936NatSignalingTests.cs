using System.Net;
using System.Net.Http.Json;
using Allegiance.Core.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

public sealed class P936NatSignalingTests
{
    [Fact]
    public async Task Player_hosting_is_opt_in_and_dedicated_registration_stays_unchanged()
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        var config = await http.GetFromJsonAsync("/api/nat/config", NatJsonContext.Default.NatConfiguration);
        Assert.False(config!.PlayerHostedGames); Assert.Equal(0, config.PrimaryPort);
        using var refused = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = "host", Port = 6073, PlayerHosted = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var punch = await http.PostAsJsonAsync("/api/servers/absent/punch", new PunchRequest(""));
        Assert.Equal(HttpStatusCode.Forbidden, punch.StatusCode);
        using var dedicated = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest { Name = "dedicated", Port = 6073 });
        Assert.Equal(HttpStatusCode.OK, dedicated.StatusCode);
        Assert.DoesNotContain("playerHosted", await http.GetStringAsync("/api/servers"));
    }
    [Fact]
    public async Task Observed_endpoint_overrides_claimed_address_and_port_and_receipts_are_private()
    {
        using var app = new LobbyTestApp(configure: s => s.PlayerHostedGames = true); using var http = app.CreateClient();
        string receipt = Observe(app, new(IPAddress.Loopback, 32123));
        using var registered = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "host", Port = 6073, Address = "192.0.2.99", UdpPort = 9, Receipt = receipt, PlayerHosted = true, CurrentGames = 0, MaxGames = 99 });
        registered.EnsureSuccessStatusCode(); var host = (await registered.Content.ReadFromJsonAsync<RegisterResponse>())!;
        var listing = (await http.GetFromJsonAsync<ServerListingDto>("/api/servers/" + host.Id))!;
        Assert.True(listing.PlayerHosted); Assert.Equal("127.0.0.1", listing.Address); Assert.Equal(32123, listing.UdpPort);
        Assert.Equal(1, listing.CurrentGames); Assert.Equal(1, listing.MaxGames);
        string json = await http.GetStringAsync("/api/servers");
        Assert.DoesNotContain(receipt, json); Assert.DoesNotContain(host.Token, json);
        using var forged = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "forged", Port = 6073, Receipt = new string('A', 32), PlayerHosted = true });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        string otherIp = Observe(app, new(IPAddress.Parse("192.0.2.40"), 32124));
        using var mismatch = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "wrong source", Port = 6073, Receipt = otherIp, PlayerHosted = true });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        string secondary = Observe(app, new(IPAddress.Loopback, 32125), false);
        using var wrongPort = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "secondary", Port = 6073, Receipt = secondary, PlayerHosted = true });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPort.StatusCode);
    }
    [Fact]
    public async Task Approved_server_filter_also_applies_to_player_hosts()
    {
        using var app = new LobbyTestApp(configure: s => { s.PlayerHostedGames = true; s.AllowedServers = ["203.0.113.9"]; }, sourceIp: "192.0.2.40");
        using var http = app.CreateClient();
        string receipt = Observe(app, new(IPAddress.Parse("192.0.2.40"), 32124));
        using var refused = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "filtered", Port = 6073, Receipt = receipt, PlayerHosted = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
    [Fact]
    public async Task Introductions_are_observed_short_lived_rate_limited_and_owner_polled()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = new LobbyTestApp(time, s => s.PlayerHostedGames = true); using var http = app.CreateClient();
        var host = await Register(app, http);
        var request = new PunchRequest(Observe(app, new(IPAddress.Loopback, 32124)));
        using var unknown = await http.PostAsJsonAsync("/api/servers/unknown/punch", request);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var first = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch", request);
        var introduction = (await first.Content.ReadFromJsonAsync<PunchIntroduction>())!;
        Assert.Equal(32123, introduction.Endpoint.Port);
        Assert.InRange((int)(introduction.ExpiresUtc - time.GetUtcNow()).TotalSeconds, 1, 10);
        using var wrongOwner = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch/poll", new PunchPollRequest("wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOwner.StatusCode);
        using var poll = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch/poll", new PunchPollRequest(host.Token));
        var batch = (await poll.Content.ReadFromJsonAsync<PunchBatch>())!;
        Assert.Equal(32124, Assert.Single(batch.Introductions).Endpoint.Port);
        Assert.Equal(introduction.Secret, batch.Introductions[0].Secret);
        for (int i = 0; i < 2; i++)
        { using var allowed = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch", request); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); }
        using var limited = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch", request);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        time.Advance(TimeSpan.FromSeconds(11));
        using var expiredPoll = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch/poll", new PunchPollRequest(host.Token));
        Assert.Empty((await expiredPoll.Content.ReadFromJsonAsync<PunchBatch>())!.Introductions);
        time.Advance(TimeSpan.FromSeconds(10));
        using var expired = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch", request);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Empty(await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers") ?? []);
        Assert.Equal(0, app.Services.GetRequiredService<ServerRegistry>().Stats().Servers);
    }
    [Fact]
    public void UDP_responder_rejects_bad_packets_and_limits_each_source_and_expires_receipts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow); var observations = new NatObservationStore(time);
        var request = StunBinding.CreateRequest(); var output = new byte[256]; var source = new IPEndPoint(IPAddress.Loopback, 45678);
        for (int i = 0; i < 16; i++) Assert.Equal(52, observations.Respond(request, source, true, output));
        Assert.Equal(0, observations.Respond(request, source, true, output));
        Assert.Equal(52, observations.Respond(request, new(IPAddress.Parse("127.0.0.2"), 45678), true, output));
        time.Advance(TimeSpan.FromSeconds(1));
        int size = observations.Respond(request, source, true, output);
        Assert.True(StunBinding.TryReadSuccess(output.AsSpan(0, size), request.AsSpan(8, 12), out _, out var receipt));
        Assert.Equal(source, observations.Resolve(receipt, IPAddress.Loopback));
        Assert.Null(observations.Resolve(receipt, IPAddress.Parse("127.0.0.2")));
        Assert.Equal(0, observations.Respond(new byte[257], source, true, output));
        request[4] ^= 1; Assert.Equal(0, observations.Respond(request, source, true, output));
        time.Advance(TimeSpan.FromSeconds(31)); Assert.Null(observations.Resolve(receipt, IPAddress.Loopback));
    }
    [Fact]
    public async Task Actual_lobby_responder_observes_the_same_game_socket_on_two_ports()
    {
        using var app = new LobbyTestApp(configure: s => { s.PlayerHostedGames = true; s.StunBindAddress = "127.0.0.1"; s.StunPrimaryPort = 0; s.StunSecondaryPort = 0; });
        using var http = app.CreateClient(); var responder = app.Services.GetRequiredService<NatBindingResponder>();
        await responder.StartAsync(CancellationToken.None);
        try
        {
            using var game = new UdpTransport(new NoSite()); game.PrepareTraversal();
            var configuration = await NatTraversalClient.ConfigurationAsync(http);
            var observation = await NatTraversalClient.DiscoverStableAsync(game, configuration);
            Assert.Equal(game.SocketPort, observation.Endpoint.Port);
            Assert.Equal(observation.Endpoint, app.Services.GetRequiredService<NatObservationStore>().Resolve(observation.Receipt, IPAddress.Loopback));
        }
        finally { await responder.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public void Listing_limit_applies_across_different_requester_addresses()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var signaling = new PunchSignaling(time);
        var host = new ServerListingDto("test", "host", "127.0.0.1", 6073, 1, 8, "test", "", "",
            time.GetUtcNow(), time.GetUtcNow(), UdpPort: 32001, PlayerHosted: true);
        for (int i = 1; i <= 6; i++) Assert.NotNull(signaling.Introduce(host, new(IPAddress.Parse("192.0.2." + i), 32002)));
        Assert.Null(signaling.Introduce(host, new(IPAddress.Parse("192.0.2.7"), 32002)));
        Assert.Equal(6, signaling.Poll(host.Id).Introductions.Length);
        Assert.Empty(signaling.Poll(host.Id).Introductions);
    }
    [Fact]
    public async Task Shorter_directory_expiry_also_expires_player_lookup_heartbeat_and_introductions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var app = new LobbyTestApp(time, settings => { settings.PlayerHostedGames = true; settings.MaxAgeSeconds = 2; });
        using var http = app.CreateClient();
        var host = await Register(app, http);
        time.Advance(TimeSpan.FromSeconds(3));
        using var lookup = await http.GetAsync("/api/servers/" + host.Id);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        using var punch = await http.PostAsJsonAsync($"/api/servers/{host.Id}/punch",
            new PunchRequest(Observe(app, new(IPAddress.Loopback, 32124))));
        Assert.Equal(HttpStatusCode.NotFound, punch.StatusCode);
        using var heartbeat = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "host", Port = 6073, PlayerHosted = true, Receipt = Observe(app, new(IPAddress.Loopback, 32123)), Token = host.Token });
        Assert.Equal(HttpStatusCode.Conflict, heartbeat.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
    }
    internal static string Observe(LobbyTestApp app, IPEndPoint endpoint, bool primary = true)
    {
        var request = StunBinding.CreateRequest(); var response = new byte[256];
        int count = app.Services.GetRequiredService<NatObservationStore>().Respond(request, endpoint, primary, response);
        Assert.True(StunBinding.TryReadSuccess(response.AsSpan(0, count), request.AsSpan(8, 12), out _, out var receipt));
        return receipt!;
    }
    private static async Task<RegisterResponse> Register(LobbyTestApp app, HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/servers/register", new RegisterRequest
            { Name = "host", Port = 6073, Receipt = Observe(app, new(IPAddress.Loopback, 32123)), PlayerHosted = true });
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }
    private sealed class NoSite : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
        public void OnSessionLost() { }
    }
}
