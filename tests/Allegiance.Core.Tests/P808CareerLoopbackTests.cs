using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Lhb = global::Allegiance.Server.LobbyHeartbeat;
using Lmr = global::Allegiance.Server.LobbyMatchReport;
using Lmre = global::Allegiance.Server.LobbyMatchResultEntry;

namespace Allegiance.Core.Tests;

/// <summary>
/// P808b full-loop career test: a verified (enrolled) game server's
/// <see cref="Lhb.ReportResultsAsync"/> POST reaches the real in-process lobby,
/// which ingests the match result into the career line; the character-info
/// endpoint then returns it. Exercises the native persist/rank path end to end
/// over real HTTP (anti-abuse: the report is only accepted because the server
/// presents a valid enrollment; the payload is the server's local mirror of the
/// lobby contract, serialized by the source-generated AOT-safe context).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P808CareerLoopbackTests
{
    [Fact]
    public async Task Verified_server_report_lands_in_career_and_character_info()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "career-loopback-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings
        {
            ConnectionString = "Data Source=" + Path.Combine(directory, "accounts.db"),
            SigningKeyFile = Path.Combine(directory, "signing.pem"), PublicKeyFile = Path.Combine(directory, "public.pem"),
        };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var original = new LobbyHarness();
        using var app = original.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>(); services.AddSingleton(settings);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        }));
        using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<IdentityStore>();

        var player = store.CreateAccount("CareerPilot");
        var operatorAcct = store.CreateAccount("Operator");
        string serverId = "srv-career", secret = "secret-career";
        store.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)",
            serverId, IdentityStore.Hash(secret), operatorAcct.Id, "Career test server");

        string enrollmentFile = Path.Combine(directory, "server.json");
        File.WriteAllText(enrollmentFile, JsonSerializer.Serialize(new ServerEnrollment(serverId, secret), IdentityJsonContext.Default.ServerEnrollment));
        var config = new global::Allegiance.Server.LobbyConfig { Url = "http://localhost", DisplayName = "Career", EnrollmentFile = enrollmentFile };

        using var hb = new Lhb(http, config, () => 0, "Test", "Core", "version", 6073, clock);
        var report = new Lmr("match-career-1", [new Lmre(player.Id, 2, 500, 120, 3, 1, 1, 0, 30, true, false, false, false, true)]);
        await hb.ReportResultsAsync(report);

        using var infoResp = await http.GetAsync($"/api/career/{player.Id}");
        infoResp.EnsureSuccessStatusCode();
        var info = (await infoResp.Content.ReadFromJsonAsync<CareerContracts.CareerInfo>())!;
        Assert.Equal("CareerPilot", info.Callsign);
        var civ = Assert.Single(info.Civs);
        Assert.Equal(2, civ.Civ);
        Assert.Equal(500f, civ.Score);
        Assert.Equal(3, civ.Kills);
        Assert.Equal(1, civ.Deaths);
        Assert.Equal(120, civ.Rating);
    }
}
