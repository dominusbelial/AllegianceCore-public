using System.Security.Cryptography;
using System.Text;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using FltBot = Allegiance.Bot.Bot;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P808IdentityLoopbackTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Valid_ticket_binds_canonical_identity_and_existing_wire_messages_then_replay_is_refused()
    {
        Require.Artwork();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "public.pem"); File.WriteAllText(file, key.ExportSubjectPublicKeyInfoPem());
        const string issuer = "https://lobby.example.invalid", audience = "server-test";
        string account = Guid.NewGuid().ToString("N");
        using var server = Boot(new ServerIdentityConfig { Issuer = issuer, Audience = audience, PublicKeyFile = file, AllowAnonymous = false });
        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        string token = JoinTokens.Issue(key, issuer, audience, account, "CanonicalPilot", DateTimeOffset.UtcNow);
        bot.Connect(server.BoundPort, "SpoofedName"); bot.SendLogonRequest(Request("SpoofedName", token));
        Pump(server, () => bot.Ack is not null);
        Assert.True(bot.Ack!.FValidated); Assert.Equal("CanonicalPilot", bot.Ack.CharNameOrFailureReason);
        Assert.Equal(account, server.AccountId(new ConnectionId(1)));
        short ship = bot.Ack.ShipID;
        bot.SendLogonRequest(Request("DifferentSpoof", token));
        for (int i = 0; i < 20; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.Equal(ship, bot.Ack.ShipID); Assert.Contains("CanonicalPilot", bot.Ack.CharNameOrFailureReason);
        using var replay = new FltBot { AutoSelectSideAfterLogon = false };
        replay.Connect(server.BoundPort, "Replay"); replay.SendLogonRequest(Request("Replay", token)); Pump(server, () => replay.Ack is not null);
        Assert.False(replay.Ack!.FValidated); Assert.Contains("already used", replay.Ack.CharNameOrFailureReason);
        bot.SendPositionReq(0); Pump(server, () => bot.ShipAssignment is not null);
        Assert.Contains("CanonicalPilot", server.LivePlayerNames);
        Assert.Single(server.Ships);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Forged_expired_wrong_audience_tokens_refused_and_anonymous_obeys_configuration(bool allowAnonymous)
    {
        Require.Artwork(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "public.pem"); File.WriteAllText(file, key.ExportSubjectPublicKeyInfoPem());
        using var server = Boot(new ServerIdentityConfig { Issuer = "issuer", Audience = "server", PublicKeyFile = file, AllowAnonymous = allowAnonymous });
        string id = Guid.NewGuid().ToString("N");
        string[] invalid =
        [
            JoinTokens.Issue(attacker, "issuer", "server", id, "ForgedPilot", DateTimeOffset.UtcNow),
            JoinTokens.Issue(key, "issuer", "server", id, "ExpiredPilot", DateTimeOffset.UtcNow.AddMinutes(-2)),
            JoinTokens.Issue(key, "issuer", "other-server", id, "WrongAudience", DateTimeOffset.UtcNow),
            "malformed.token.value",
        ];
        foreach (string token in invalid)
        {
            using var bot = new FltBot { AutoSelectSideAfterLogon = false };
            bot.Connect(server.BoundPort, "Untrusted"); bot.SendLogonRequest(Request("Untrusted", token)); Pump(server, () => bot.Ack is not null);
            Assert.False(bot.Ack!.FValidated); Assert.Equal(0, bot.Ack.ShipID);
        }
        Assert.Equal(0, server.ConnectionScopedCacheCounts().PendingSideSelect);
        using var anonymous = new FltBot { AutoSelectSideAfterLogon = false };
        anonymous.Connect(server.BoundPort, "Anonymous"); anonymous.SendLogonRequest(Request("Anonymous", null)); Pump(server, () => anonymous.Ack is not null);
        Assert.Equal(allowAnonymous, anonymous.Ack!.FValidated);
        Assert.Equal(allowAnonymous ? "Anonymous" : "this server requires lobby sign-in", anonymous.Ack.CharNameOrFailureReason);   // native: the accepted name on success (appmsg.cpp:39-40)
    }
    private static LogonRequest Request(string name, string? token) => new()
    {
        CharacterName = name, ZoneTicket = token is null ? null : Encoding.UTF8.GetBytes(token), FedsrvVer = LogonRequest.MsgVer, CDKey = "test", Time = 1,
    };
    private static Srv Boot(ServerIdentityConfig identity)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20, Identity = identity,
            Games = [new GameConfig { Name = "Identity loopback", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
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
