using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Lobby.Events;
using Allegiance.Lobby.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P820b — the lobby zone-events store and HTTP API. Exercises create/edit/
/// delete + the operator-only permission gate (anonymous &rarr; 401,
/// signed-in non-operator &rarr; 403, operator &rarr; 200) and the read-only
/// player list, mirroring native <c>WinTrek/zoneeventsscreen.cpp</c>. Run 3×
/// alone (rules light gate).
/// </summary>
public sealed class P820bEventsTests
{
    private static (IdentityStore store, EventStore events, string dir) Stores(TimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "events-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate();
        return (store, new EventStore(settings), dir);
    }

    private static EventContracts.CreateEventRequest Req(string name, string date = "2026-11-05", string time = "19:00",
        string desc = "", string url = "", string[]? files = null)
        => new(date, time, name, desc, url, files ?? []);

    [Fact]
    public void Create_validates_name_date_and_files()
    {
        var (_, events, _) = Stores();
        long now = 1000;
        Assert.False(events.Create(Req("   ", date: "  "), now).Ok);                 // no date
        Assert.False(events.Create(Req("   "), now).Ok);                              // no name
        Assert.False(events.Create(Req(new string('x', 65)), now).Ok);                // name too long
        Assert.False(events.Create(Req("Tourney", files: ["bad\nname"]), now).Ok);    // newline in file
        Assert.True(events.Create(Req("Season opener"), now).Ok);
    }

    [Fact]
    public void Create_list_update_delete_round_trip_in_native_field_order()
    {
        var (_, events, _) = Stores();
        long now = 100;
        Assert.True(events.Create(Req("Alpha tourney", date: "2026-11-05", time: "19:00",
            desc: "Five-a-side", url: "https://signup.example/a", files: ["rules.pdf", "map.jpg"]), now).Ok);
        Assert.True(events.Create(Req("Beta duel", date: "2026-11-03", time: "12:00"), now + 1).Ok);

        // Native EventListing field order: date/time/name/description/signUpUrl/files.
        var list = events.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("2026-11-03", list[0].Date);                                       // sorted by date
        Assert.Equal("Beta duel", list[0].Name);
        var alpha = list[1];
        Assert.Equal("2026-11-05", alpha.Date);
        Assert.Equal("19:00", alpha.Time);
        Assert.Equal("Alpha tourney", alpha.Name);
        Assert.Equal("Five-a-side", alpha.Description);
        Assert.Equal("https://signup.example/a", alpha.SignUpUrl);
        Assert.Equal(new[] { "rules.pdf", "map.jpg" }, alpha.Files);                    // files round-trip

        // Update (full replace).
        Assert.True(events.Update(alpha.Id, new EventContracts.UpdateEventRequest("2026-11-06", "20:00", "Renamed", "New", "", []), now + 2).Ok);
        var updated = events.Get(alpha.Id)!;
        Assert.Equal("Renamed", updated.Name);
        Assert.Empty(updated.Files);

        // Delete.
        Assert.True(events.Delete(alpha.Id));
        Assert.False(events.Delete(alpha.Id));                                          // already gone
        Assert.Single(events.List());
    }

    [Fact]
    public async Task Events_list_is_public_and_writes_require_an_operator()
    {
        const string opId = "00000000000000000000000000000001";
        using var app = new LobbyTestApp();
        app.IdentityConfiguration = new IdentitySettings
        {
            ConnectionString = "Data Source=" + Path.Combine(app.IdentityDirectory, "accounts.db"),
            SigningKeyFile = Path.Combine(app.IdentityDirectory, "key.pem"),
            PublicKeyFile = Path.Combine(app.IdentityDirectory, "public.pem"),
            OperatorAccountIds = [opId],
        };
        using var anon = app.CreateClient();

        // Players read the list without signing in.
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/config/zone-web")).StatusCode);

        // Anonymous writes are unauthorized.
        using var createAnon = await anon.PostAsJsonAsync("/api/events", Req("Anon"));
        Assert.Equal(HttpStatusCode.Unauthorized, createAnon.StatusCode);

        var identities = app.Services.GetRequiredService<IdentityStore>();
        var events = app.Services.GetRequiredService<EventStore>();

        // A signed-in non-operator cannot write.
        var player = identities.CreateAccount("Player");
        using var asPlayer = App(app, identities.Session(player.Id));
        using var createPlayer = await asPlayer.PostAsJsonAsync("/api/events", Req("Player event"));
        Assert.Equal(HttpStatusCode.Forbidden, createPlayer.StatusCode);

        // The operator can create, edit and delete.
        var op = identities.CreateAccount("Operator", opId);
        using var asOp = App(app, identities.Session(op.Id));
        using var create = await asOp.PostAsJsonAsync("/api/events", Req("Zone cup", desc: "Big one", files: ["pack.zip"]));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var made = Assert.Single(events.List());

        using var edit = await asOp.PutAsJsonAsync($"/api/events/{made.Id}", new EventContracts.UpdateEventRequest("2026-12-01", "18:00", "Zone cup finale", "", "", []));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("Zone cup finale", events.Get(made.Id)!.Name);

        using var del = await asOp.DeleteAsync($"/api/events/{made.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Empty(events.List());
    }

    private static HttpClient App(LobbyTestApp app, string bearer)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }
}
