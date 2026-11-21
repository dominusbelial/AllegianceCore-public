using System.Net.Http.Json;
using System.Security.Cryptography;
using FltBot = Allegiance.Bot.Bot;
using Allegiance.Lobby;
using Allegiance.Server;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P1068GuestDiscoveryTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task Server_advertises_its_actual_anonymous_admission_through_heartbeat_and_lobby(bool identity, bool allowAnonymous, bool expected)
    {
        Require.Artwork();
        string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p1068", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Path.Combine(scratch, "public.pem"); File.WriteAllText(publicKey, key.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var lobby = new LobbyHarness();
            using var http = new HttpClient(lobby.Server.CreateHandler()) { BaseAddress = lobby.ClientOptions.BaseAddress };
            LobbyHeartbeat? heartbeat = null;
            using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
            server.HeartbeatFactory = cfg => heartbeat = new LobbyHeartbeat(http, cfg, () => 0, "guest fixture", "PCore001.igc", "1.0.0", 6073);
            server.Boot(new ServerConfig {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1",
                Identity = identity ? new() { Issuer = "fixture", Audience = "server", PublicKeyFile = publicKey, AllowAnonymous = allowAnonymous } : null,
                Lobby = new() { Url = http.BaseAddress.AbsoluteUri, DisplayName = "guest fixture", PublicAddress = "127.0.0.1" },
                Games = [new() { Name = "guest fixture", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            Assert.NotNull(heartbeat); Assert.Equal(expected, heartbeat.AcceptsGuests);
            await heartbeat.PumpAsync();
            var listing = Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
            Assert.Equal(expected, listing.AcceptsGuests);
            string json = await http.GetStringAsync("/api/servers");
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(publicKey, json, StringComparison.Ordinal);

            // The exact no-ticket LOGONREQ used by the browser follows that advertised fact.
            using var guest = new FltBot { AutoSelectSideAfterLogon = false };
            guest.Connect(server.BoundPort, "Pilot-0123456789");
            guest.SendLogonRequest(new LogonRequest { CharacterName = "Pilot-0123456789", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1, Time = 1 });
            Pump(server, () => guest.Ack is not null);
            Assert.Equal(expected, guest.Ack!.FValidated);
            if (!expected) Assert.Contains("requires lobby sign-in", guest.Ack.CharNameOrFailureReason);
            if (expected)
            {
                using var duplicate = new FltBot { AutoSelectSideAfterLogon = false };
                duplicate.Connect(server.BoundPort, "pilot-0123456789");
                duplicate.SendLogonRequest(new LogonRequest { CharacterName = "pilot-0123456789", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1, Time = 1 });
                Pump(server, () => duplicate.Ack is not null);
                Assert.False(duplicate.Ack!.FValidated);
                Assert.Contains("already in use", duplicate.Ack.CharNameOrFailureReason);
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static void Pump(Allegiance.Server.Server server, Func<bool> done)
    {
        long until = Environment.TickCount64 + 6000;
        while (!done() && Environment.TickCount64 < until) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "Loopback LOGONACK did not arrive");
    }
}
