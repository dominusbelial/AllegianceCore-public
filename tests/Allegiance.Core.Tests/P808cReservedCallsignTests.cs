using System.Net;
using System.Text;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using FltBot = Allegiance.Bot.Bot;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P808c — reserved-callsign protection. The lobby exposes the registered
/// (account-held) callsigns; the server caches them (short TTL, fail open) and
/// REFUSES an anonymous logon that would take a registered player's name.
/// Three layers are proven: the cache fetch + case-insensitive membership +
/// fail-open, and a real TCP anonymous logon being refused for a reserved name
/// while an unreserved name still joins.
/// </summary>
public sealed class P808cReservedCallsignTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond());
    }

    private static HttpClient OkClient(params string[] names) => new(new StubHandler(() =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(names), Encoding.UTF8, "application/json") }))
    { BaseAddress = new Uri("http://lobby.test") };

    [Fact]
    public async Task Cache_fetches_normalizes_case_and_membership_is_case_insensitive()
    {
        using var cache = new RegisteredCallsignCache(OkClient("AlicePilot", "BobBomber"));
        await cache.PumpAsync();
        Assert.True(cache.Contains("AlicePilot"));
        Assert.True(cache.Contains("alicepilot"));       // case-insensitive match
        Assert.True(cache.Contains("BOBBOMBER"));
        Assert.False(cache.Contains("Charlie"));
        Assert.False(cache.Contains(""));                // empty never matches
        Assert.Equal(2, cache.Snapshot.Count);
    }

    [Fact]
    public async Task Cache_fails_open_when_lobby_is_unreachable_or_errors()
    {
        // Connection failure -> stays empty (protects nothing, anonymous play safe).
        using var failing = new RegisteredCallsignCache(new HttpClient(new StubHandler(() =>
            throw new HttpRequestException("lobby down"))) { BaseAddress = new Uri("http://lobby.test") });
        await failing.PumpAsync();
        Assert.False(failing.Contains("Anything"));

        // Non-2xx status -> keeps the LAST snapshot (still fail open, never locks out).
        using var badStatus = new RegisteredCallsignCache(new HttpClient(new StubHandler(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("http://lobby.test") });
        await badStatus.PumpAsync();
        Assert.False(badStatus.Contains("Anything"));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Anonymous_logon_refused_for_a_registered_callsign_but_free_names_join()
    {
        Require.Artwork();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "reserved-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "public.pem"); File.WriteAllText(file, key.ExportSubjectPublicKeyInfoPem());
        using var server = Boot(new ServerIdentityConfig { Issuer = "issuer", Audience = "server", PublicKeyFile = file, AllowAnonymous = true });

        // A populated reserved-callsign cache (as if the lobby listed "RegPilot").
        using var cache = new RegisteredCallsignCache(OkClient("RegPilot"));
        cache.PumpAsync().GetAwaiter().GetResult();
        server.ReservedCallsignsCache = cache;

        // Anonymous logon trying to take the registered name -> refused.
        using var impostor = new FltBot { AutoSelectSideAfterLogon = false };
        impostor.Connect(server.BoundPort, "RegPilot"); impostor.SendLogonRequest(Request("RegPilot", null));
        Pump(server, () => impostor.Ack is not null);
        Assert.False(impostor.Ack!.FValidated);
        Assert.Contains("registered", impostor.Ack.CharNameOrFailureReason);

        // A free name still joins anonymously (fail-open never broke anonymous play).
        using var legit = new FltBot { AutoSelectSideAfterLogon = false };
        legit.Connect(server.BoundPort, "FreePilot"); legit.SendLogonRequest(Request("FreePilot", null));
        Pump(server, () => legit.Ack is not null);
        Assert.True(legit.Ack!.FValidated);
        Assert.Equal("FreePilot", legit.Ack.CharNameOrFailureReason);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Standalone_server_without_identity_does_not_reserve_callsigns()
    {
        Require.Artwork();
        using var server = Boot(null);   // no identity configured
        using var cache = new RegisteredCallsignCache(OkClient("RegPilot"));
        cache.PumpAsync().GetAwaiter().GetResult();
        server.ReservedCallsignsCache = cache;   // field present, but identity is null
        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        bot.Connect(server.BoundPort, "RegPilot"); bot.SendLogonRequest(Request("RegPilot", null));
        Pump(server, () => bot.Ack is not null);
        Assert.True(bot.Ack!.FValidated);   // standalone keeps its historical behaviour
    }

    private static LogonRequest Request(string name, string? token) => new()
    {
        CharacterName = name, ZoneTicket = token is null ? null : Encoding.UTF8.GetBytes(token),
        FedsrvVer = LogonRequest.MsgVer, CDKey = "test", Time = 1,
    };

    private static Srv Boot(ServerIdentityConfig? identity)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20, Identity = identity,
            Games = [new GameConfig { Name = "Reserved loopback", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        return server;
    }

    private static void Pump(Srv server, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (!done() && DateTime.UtcNow < until) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "Loopback message did not arrive");
    }
}
