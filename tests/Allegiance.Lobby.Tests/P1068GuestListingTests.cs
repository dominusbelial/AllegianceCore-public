using System.Net.Http.Json;
using System.Text.Json;

namespace Allegiance.Lobby.Tests;

public sealed class P1068GuestListingTests
{
    [Fact]
    public async Task Capability_defaults_closed_and_refreshes_without_exposing_secrets()
    {
        using var app = new LobbyTestApp(); using var client = app.CreateClient();
        var first = await (await client.PostAsJsonAsync("/api/servers/register", new {
            name = "Guest fixture", port = 6073, coreFile = "PCore001.igc",
        })).Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(first);
        var listing = Assert.Single((await client.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
        Assert.False(listing.AcceptsGuests);
        foreach (bool accepts in new[] { true, false })
        {
            (await client.PostAsJsonAsync("/api/servers/register", new {
                name = "Guest fixture", port = 6073, coreFile = "PCore001.igc", token = first.Token, acceptsGuests = accepts,
            })).EnsureSuccessStatusCode();
            string json = await client.GetStringAsync("/api/servers");
            using var document = JsonDocument.Parse(json);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(first.Token, json, StringComparison.Ordinal);
            listing = Assert.Single((await client.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
            Assert.Equal(accepts, listing.AcceptsGuests); Assert.Equal(first.Id, listing.Id);
            var single = await client.GetFromJsonAsync<ServerListingDto>("/api/servers/" + first.Id);
            Assert.Equal(accepts, single!.AcceptsGuests);
        }
    }
}
