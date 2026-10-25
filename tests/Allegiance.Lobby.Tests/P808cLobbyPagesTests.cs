using System.Net.Http.Json;
using Allegiance.Lobby.Career;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P808c — the lobby's read-only career web pages (ladder + player profile).
/// Proves (a) the static pages and scripts are served, (b) they render with the
/// same safe DOM-only discipline as the server-list page (callsigns are
/// player-supplied), and (c) the data plumbing works: a seeded career line
/// shows up on both the ladder API and the profile API the pages consume.
/// </summary>
public sealed class P808cLobbyPagesTests
{
    [Fact]
    public async Task Career_pages_and_scripts_are_served_and_DOM_safe()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        foreach (var path in new[] { "/ladder.html", "/ladder.js", "/profile.html", "/profile.js", "/career.css" })
        {
            using var resp = await client.GetAsync(path);
            Assert.True(resp.IsSuccessStatusCode, path + " was not served");
        }

        foreach (var js in new[] { "/ladder.js", "/profile.js" })
        {
            var text = await client.GetStringAsync(js);
            Assert.Contains("createElement", text);
            Assert.Contains(".textContent", text);
            Assert.DoesNotContain("innerHTML", text);
        }

        // The ladder is reachable from the server-directory page.
        var index = await client.GetStringAsync("/");
        Assert.Contains("/ladder.html", index);
    }

    [Fact]
    public async Task Seeded_career_data_is_visible_to_ladder_and_profile_apis()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IdentityStore>();
        var career = app.Services.GetRequiredService<CareerStore>();
        var clock = app.Services.GetRequiredService<TimeProvider>();

        var player = store.CreateAccount("WebPilot");
        string serverId = "srv-web";
        // Seed one match result for civ 2 (native SetCharStats path).
        career.ApplyMatchReport(new CareerContracts.MatchReport("m-web-1",
            [new CareerContracts.MatchResultEntry(player.Id, 2, 550, 130, 4, 2, 1, 0, 25, true, false, false, false, true)]),
            serverId, clock.GetUtcNow().ToUnixTimeSeconds());

        // Ladder: the pilot appears in the all-civs board with the scored total.
        var board = (await client.GetFromJsonAsync<CareerContracts.LeaderboardResponse>(
            "/api/career/leaderboard?civ=-1&sort=3"))!;
        Assert.Null(board.FailReason);
        var entry = Assert.Single(board.Entries, e => e.AccountId == player.Id);
        Assert.Equal("WebPilot", entry.Callsign);
        Assert.Equal(550f, entry.TotalScore);
        Assert.Equal(4, entry.Kills);

        // Profile: the per-civ line the profile page renders.
        var info = (await client.GetFromJsonAsync<CareerContracts.CareerInfo>($"/api/career/{player.Id}"))!;
        Assert.Equal("WebPilot", info.Callsign);
        var civ = Assert.Single(info.Civs);
        Assert.Equal(2, civ.Civ);
        Assert.Equal(550f, civ.Score);
        Assert.Equal(130, civ.Rating);
    }
}
