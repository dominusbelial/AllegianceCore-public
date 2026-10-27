using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Lhb = global::Allegiance.Server.LobbyHeartbeat;
using Lmr = global::Allegiance.Server.LobbyMatchReport;
using Lsgr = global::Allegiance.Server.LobbySquadGameResult;

namespace Allegiance.Core.Tests;

/// <summary>
/// P855 / SIM-0765 full loop: a verified (enrolled) game server's
/// <see cref="Lhb.ReportResultsAsync"/> POST — the same path career results
/// ride — carries the finished squad game's squad section (the port of native
/// <c>RecordSquadGame</c>, FedSrv.CPP:1198-1300, gated at
/// fsmission.cpp:3421-3427) and the lobby credits the winner squad one win and
/// each loser squad one loss. Proves the server→lobby contract end to end over
/// real HTTP with the source-generated AOT-safe serializer.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P855SquadGameLoopbackTests
{
    [Fact]
    public async Task Squad_game_report_updates_both_squads_over_http()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p855-loopback-" + Guid.NewGuid().ToString("N"));
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
        var squads = app.Services.GetRequiredService<SquadStore>();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();

        var leaderW = store.CreateAccount("P855LoopWin");
        var leaderL = store.CreateAccount("P855LoopLoss");
        Assert.True(squads.CreateSquad(leaderW.Id, new SquadContracts.CreateSquadRequest("Loop Alpha", "LPA", "", "", 1), now).Ok);
        Assert.True(squads.CreateSquad(leaderL.Id, new SquadContracts.CreateSquadRequest("Loop Bravo", "LPB", "", "", 3), now).Ok);
        string winId = Assert.Single(squads.Find("Loop Alpha")).Id;
        string loseId = Assert.Single(squads.Find("Loop Bravo")).Id;

        var operatorAcct = store.CreateAccount("Operator");
        string serverId = "srv-p855", secret = "secret-p855";
        store.Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)",
            serverId, IdentityStore.Hash(secret), operatorAcct.Id, "P855 test server");

        string enrollmentFile = Path.Combine(directory, "server.json");
        File.WriteAllText(enrollmentFile, JsonSerializer.Serialize(new ServerEnrollment(serverId, secret), IdentityJsonContext.Default.ServerEnrollment));
        var config = new global::Allegiance.Server.LobbyConfig { Url = "http://localhost", DisplayName = "P855", EnrollmentFile = enrollmentFile };

        using var hb = new Lhb(http, config, () => 0, "Test", "Core", "version", 6073, clock);
        // The native gate never reached in a draw: m_psideWon null ⇒ no report.
        // A qualifying game reports the winner squad + every losing squad.
        await hb.ReportResultsAsync(new Lmr("squad-match-1", [], SquadGame: new Lsgr(winId, [loseId])));
        // A replay of the same match must not double-apply (lobby-side idempotence).
        await hb.ReportResultsAsync(new Lmr("squad-match-1", [], SquadGame: new Lsgr(winId, [loseId])));

        var rows = (await http.GetFromJsonAsync<List<SquadContracts.SquadSummary>>("/api/squads?sort=Name&count=84"))!
            .ToDictionary(r => r.Name);
        Assert.Equal((1, 0), (rows["Loop Alpha"].Wins, rows["Loop Alpha"].Losses));
        Assert.Equal((0, 1), (rows["Loop Bravo"].Wins, rows["Loop Bravo"].Losses));
    }
}
