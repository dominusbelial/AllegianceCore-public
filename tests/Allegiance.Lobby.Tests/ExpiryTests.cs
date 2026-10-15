using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// Expiry: entries older than the staleness window disappear once the sweep prunes them.
/// Verified two ways — deterministically at the registry level with a fake clock, and
/// end-to-end over HTTP where the fake clock also drives the hosted sweep's timer.
/// </summary>
public class ExpiryTests
{
    private static object Payload(string? token = null) => new
    {
        name = "Test Server",
        address = "1.2.3.4",
        port = 6073,
        playerCount = 5,
        maxPlayers = 64,
        missionName = "Wing War",
        coreFile = "PCore001.igc",
        version = "0.1.0",
        token,
    };

    [Fact]
    public void Registry_prunes_entries_older_than_the_expiry_window()
    {
        // Deterministic, timer-free: drive the registry directly on a fake clock.
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var registry = new ServerRegistry(fake, maxAge: TimeSpan.FromSeconds(90));

        var created = registry.Register(new RegisterRequest
        {
            Name = "Srv", Address = "1.2.3.4", Port = 5555,
            PlayerCount = 1, MaxPlayers = 10, MissionName = "M",
            CoreFile = "c.igc", Version = "v1",
        });
        Assert.NotNull(created);

        // Live now: present in both detail and list.
        Assert.NotNull(registry.Get(created!.Id));
        Assert.Contains(created.Id, registry.List().Select(s => s.Id));

        fake.Advance(TimeSpan.FromSeconds(100));            // 100s > 90s window

        // Get() filters expired entries immediately (the detail endpoint 404s once stale)…
        Assert.Null(registry.Get(created.Id));
        // P655: expired listings are hidden immediately, before the sweep.
        Assert.DoesNotContain(created.Id, registry.List().Select(s => s.Id));

        registry.PruneExpired(TimeSpan.FromSeconds(90));    // sweep runs

        Assert.Null(registry.Get(created.Id));
        Assert.DoesNotContain(created.Id, registry.List().Select(s => s.Id));  // gone from list
    }

    [Fact]
    public async Task Expired_entry_disappears_from_GET_once_stale_and_pruned()
    {
        // A huge sweep interval keeps the hosted timer inert for this test; expiry/pruning
        // is driven explicitly so the assertions are deterministic (no timer races).
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var app = new LobbyTestApp(fake, s =>
        {
            s.SweepIntervalSeconds = 7200;
            s.MaxAgeSeconds = 0.3;
        });
        using var client = app.CreateClient();

        var reg = await (await client.PostAsJsonAsync("/api/servers/register", Payload()))
            .Content.ReadFromJsonAsync<RegisterResponse>();

        // Fresh: in the public list.
        Assert.Contains(reg!.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));

        // Drive the injected clock past the expiry window.
        fake.Advance(TimeSpan.FromSeconds(1));

        // The detail endpoint 404s immediately once stale (Get() filters expired) — even
        // before any sweep runs.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/servers/{reg.Id}")).StatusCode);

        // The public list also hides it before the background sweep.
        Assert.DoesNotContain(reg.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));
        app.Services.GetRequiredService<ServerRegistry>().PruneExpired(TimeSpan.FromSeconds(0.3));

        // Now gone from the public list.
        Assert.DoesNotContain(reg.Id, (await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!
            .Select(s => s.Id));
    }
}
