using System.Net.Http.Json;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P820a — the lobby's read-only squad web pages (list + profile). Proves the
/// pages/scripts are served with the same safe DOM-only discipline as the other
/// lobby pages, and that a seeded squad shows up on the APIs the pages consume.
/// </summary>
public sealed class P820SquadPagesTests
{
    [Fact]
    public async Task Squad_pages_and_scripts_are_served_and_DOM_safe()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        foreach (var path in new[] { "/squads.html", "/squads.js", "/squad-profile.html", "/squad-profile.js" })
        {
            using var resp = await client.GetAsync(path);
            Assert.True(resp.IsSuccessStatusCode, path + " was not served");
        }
        foreach (var js in new[] { "/squads.js", "/squad-profile.js" })
        {
            var text = await client.GetStringAsync(js);
            Assert.Contains("createElement", text);
            Assert.Contains(".textContent", text);
            Assert.DoesNotContain("innerHTML", text);
        }
        // The squads list is reachable from the server-directory page.
        var index = await client.GetStringAsync("/");
        Assert.Contains("/squads.html", index);
    }

    [Fact]
    public async Task Seeded_squad_appears_on_the_list_and_profile_apis()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();
        var identities = app.Services.GetRequiredService<IdentityStore>();
        var squads = app.Services.GetRequiredService<SquadStore>();

        var founder = identities.CreateAccount("Founder");
        var result = squads.CreateSquad(founder.Id, new SquadContracts.CreateSquadRequest("Web Squad", "WEB", "A demo squad", "https://example.web", 1), identities.Now);
        Assert.True(result.Ok);

        // List: the squad is in the all-squads page data.
        var list = (await client.GetFromJsonAsync<SquadContracts.SquadSummary[]>("/api/squads/?sort=1&count=200"))!;
        var row = Assert.Single(list, s => s.Name == "Web Squad");
        Assert.Equal("Founder", row.LeaderCallsign);

        // Profile: details + member the profile page renders.
        var details = (await client.GetFromJsonAsync<SquadContracts.SquadDetails>($"/api/squads/{row.Id}"))!;
        Assert.Equal("A demo squad", details.Description);
        var leader = Assert.Single(details.Members);
        Assert.Equal(SquadContracts.SquadRole.Leader, leader.Role);
        Assert.Equal("Founder", leader.Callsign);

        // Log the profile page renders.
        var log = (await client.GetFromJsonAsync<SquadContracts.SquadLogEntry[]>($"/api/squads/{row.Id}/log"))!;
        Assert.NotEmpty(log);
    }
}
