using System.Security.Cryptography;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>P1061 — the version and password gates are unchanged server checks: a
/// client with no/old build identity is refused with the update reason, a wrong
/// game password gets DPR_BadPassword, and only the right version and password
/// with a valid ticket are admitted to the same mission.</summary>
[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P1061JoinGateTests(ITestOutputHelper output)
{
    private const string Version = "2.0.0";
    private const string Password = "p1061-gate";

    [SkippableFact]
    public async Task Version_password_and_ticket_gates_admit_only_the_matching_client()
    {
        Require.Artwork();
        string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1061", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Path.Combine(scratch, "public.pem");
        await File.WriteAllTextAsync(publicKey, key.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1",
                UpdatesPerSecond = 20, MinimumClientVersion = Version,
                Discovery = new() { Enabled = false },
                Identity = new() { Issuer = "p1061", Audience = "game", PublicKeyFile = publicKey, AllowAnonymous = false },
                Games = [new() { Name = "P1061", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                    Password = Password, AutoStart = true, StartCountdownSeconds = 0, AllowEmptyTeams = true }],
            });

            // 1. A build that reports 0.0.0 is refused with the update reason.
            var old = await Attempt(server, key, "OldPilot", clientVersion: "0.0.0", password: Password, ticket: true);
            Assert.True(old.Faulted);
            Assert.Contains("older than this server's minimum", old.Error);
            output.WriteLine("old build refusal: " + old.Error);

            // 2. No build identity at all is refused fail-closed.
            var missing = await Attempt(server, key, "NoPilot", clientVersion: null, password: Password, ticket: true);
            Assert.True(missing.Faulted);
            Assert.Contains("did not report a version", missing.Error);
            output.WriteLine("missing build refusal: " + missing.Error);

            // 3. Correct version, wrong password: the ordinary join gate answers BadPassword.
            var badPassword = await Attempt(server, key, "WrongPilot", clientVersion: Version, password: "wrong", ticket: true);
            Assert.False(badPassword.Faulted);
            Assert.Equal(DelPositionReqReason.BadPassword, badPassword.Refusal);
            output.WriteLine("wrong password refusal: " + badPassword.Refusal);

            // 4. No ticket: identity is mandatory, the message is a clear refusal.
            var noTicket = await Attempt(server, key, "NoTicketPilot", clientVersion: Version, password: Password, ticket: false);
            Assert.True(noTicket.Faulted);
            output.WriteLine("missing ticket refusal: " + noTicket.Error);

            // 5. The same version, password and ticket are admitted.
            var admitted = await Attempt(server, key, "GoodPilot", clientVersion: Version, password: Password, ticket: true);
            Assert.False(admitted.Faulted);
            Assert.Null(admitted.Refusal);
            Assert.True(admitted.Validated);
            output.WriteLine("admitted: " + admitted.Refusal);
        }
        finally { Directory.Delete(scratch, true); }
    }

    private sealed record Outcome(bool Faulted, string? Error, DelPositionReqReason? Refusal, bool Validated);

    private static async Task<Outcome> Attempt(Srv server, ECDsa key, string name, string? clientVersion, string? password, bool ticket)
    {
        using var session = new ConnectionSession { TransportProtocol = "tcp", ClientVersion = clientVersion };
        var request = new LogonRequest
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue, CrcFileList = -1, Time = 1,
            ClientVersion = clientVersion, MissionPassword = password,
        };
        var join = session.JoinAsync(server.BoundPort, name, request, identityTicket: ticket
            ? _ => Task.FromResult(JoinTokens.Issue(key, "p1061", "game", "0123456789abcdef0123456789abcdef", name, DateTimeOffset.UtcNow))
            : null);
        using var deadline = new CancellationTokenSource(8000);
        while (!join.IsCompleted && !deadline.IsCancellationRequested) { server.TickSim(); await Task.Delay(2); }
        if (!join.IsCompleted) throw new TimeoutException("join gate did not answer: " + name);
        if (join.IsFaulted) return new(true, join.Exception!.GetBaseException().Message, null, false);
        return new(false, null, (await join)?.Reason, session.Ack?.FValidated == true);
    }
}
