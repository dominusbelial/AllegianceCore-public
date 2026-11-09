using System.Net;
using System.Net.Http.Json;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P970 — the directory half of the advertised "password required" fact: a
/// registering server's <c>passwordRequired</c> (server-level and per mission) is
/// stored, refreshed by a heartbeat and published on the public listing, and the
/// password VALUE has no field to land in at all.
/// <para>Native carried no such flag (<c>lobby/messagesall.h:25-63</c> has no
/// password field on <c>LOBBYMISSIONINFO</c>); this is the port's early-warning
/// form of the refusal the game server sends (<c>FedSrv/FedSrv.CPP:648-661</c>).</para>
/// </summary>
public class P970ListingPasswordTests
{
    private static object Payload(bool passwordRequired, string? token = null, params (uint Cookie, string Name, bool Guarded)[] missions) => new
    {
        name = "P970 Guarded Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount = 2,
        maxPlayers = 16,
        missionName = missions.Length > 0 ? missions[0].Name : "Guarded",
        coreFile = "PCore001.igc",
        version = "0.1.0",
        passwordRequired,
        token,
        missions = missions.Select(m => new { cookie = m.Cookie, name = m.Name, playerCount = 1, passwordRequired = m.Guarded }),
    };

    private static async Task<List<ServerListingDto>> List(HttpClient client)
        => (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!;

    [Fact]
    public async Task A_registered_password_requirement_is_published_on_the_listing()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var post = await client.PostAsJsonAsync("/api/servers/register", Payload(true));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var listing = Assert.Single(await List(client));
        Assert.True(listing.PasswordRequired);
    }

    [Fact]
    public async Task A_pre_P970_payload_without_the_field_lists_as_open()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        // No passwordRequired member at all: a pre-P970 host must register
        // byte-identically and read as "no password advertised".
        var post = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "Old Server", address = "1.2.3.5", port = 6074, playerCount = 0,
            maxPlayers = 16, missionName = "Open", coreFile = "PCore001.igc", version = "0.1.0",
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.False(Assert.Single(await List(client)).PasswordRequired);
    }

    [Fact]
    public async Task A_heartbeat_refreshes_the_requirement_and_the_per_mission_flags()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        var first = await (await client.PostAsJsonAsync("/api/servers/register",
            Payload(false, null, (1u, "Open one", false), (2u, "Guarded one", false))))
            .Content.ReadFromJsonAsync<RegisterResponse>();
        var before = Assert.Single(await List(client));
        Assert.False(before.PasswordRequired);
        Assert.All(before.Missions!, m => Assert.False(m.PasswordRequired));

        // The host loads a password on one of its two games: the same listing must
        // now advertise the lock, with the per-mission fact kept apart.
        var second = await (await client.PostAsJsonAsync("/api/servers/register",
            Payload(true, first!.Token, (1u, "Open one", false), (2u, "Guarded one", true))))
            .Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.Equal(first.Id, second!.Id);

        var after = Assert.Single(await List(client));
        Assert.True(after.PasswordRequired);
        Assert.False(Assert.Single(after.Missions!, m => m.Name == "Open one").PasswordRequired);
        Assert.True(Assert.Single(after.Missions!, m => m.Name == "Guarded one").PasswordRequired);

        // And clearing it is published at once too (a heartbeat, not a new listing).
        await client.PostAsJsonAsync("/api/servers/register",
            Payload(false, first.Token, (1u, "Open one", false), (2u, "Guarded one", false)));
        var cleared = Assert.Single(await List(client));
        Assert.False(cleared.PasswordRequired);
        Assert.All(cleared.Missions!, m => Assert.False(m.PasswordRequired));
    }

    [Fact]
    public async Task The_web_page_shows_a_lock_for_a_protected_game()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        string js = await client.GetStringAsync("/app.js");
        // The lock is appended to the mission cell's TEXT (never markup), so a
        // hostile server name still cannot inject anything.
        Assert.Contains("passwordRequired", js, StringComparison.Ordinal);
        Assert.Contains("\\uD83D\\uDD12", js, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", js);
    }

    [Fact]
    public async Task The_listing_never_carries_a_password_value()
    {
        using var app = new LobbyTestApp();
        using var client = app.CreateClient();

        // A hostile/naive host that posts the password itself: the contract has no
        // field for it, so it must not survive into the public answer.
        var post = await client.PostAsJsonAsync("/api/servers/register", new
        {
            name = "Leaky Server", address = "1.2.3.6", port = 6075, playerCount = 1,
            maxPlayers = 16, missionName = "Guarded", coreFile = "PCore001.igc", version = "0.1.0",
            passwordRequired = true, password = "  not-on-any-listing  ",
            missions = new[] { new { cookie = 7u, name = "Guarded", playerCount = 1, passwordRequired = true, password = "not-on-any-listing" } },
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        string json = await client.GetStringAsync("/api/servers");
        Assert.DoesNotContain("not-on-any-listing", json, StringComparison.Ordinal);
        Assert.Contains("passwordRequired", json, StringComparison.Ordinal);
        Assert.True(Assert.Single(await List(client)).PasswordRequired);
    }
}
