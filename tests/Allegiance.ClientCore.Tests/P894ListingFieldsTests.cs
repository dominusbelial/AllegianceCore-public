using System.Text.Json;
using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P894 — the client side of the richer server listing: the fields native's server
/// list carried and the port now relays (LOCATION, games/load: LOB-0018/LOB-0029),
/// the lobby-normalized start time (LOB-0017), and the browse-time half of the logon
/// version gate (LOB-0020/LOB-0026). Pure model logic, so it is testable headlessly.
/// </summary>
public sealed class P894ListingFieldsTests
{
    private static ServerListing Listing(
        string? location = null, int percentLoad = 0, int currentGames = 0, int maxGames = 0,
        DateTimeOffset? start = null, string? minClientVersion = null) => new(
        Id: "id", Name: "Server", Address: "1.2.3.4", Port: 6073,
        PlayerCount: 3, MaxPlayers: 16, MissionName: "Wing War", CoreFile: "PCore001.igc",
        Version: "0.1.0", RegisteredUtc: default, LastSeenUtc: default,
        MinClientVersion: minClientVersion, CurrentGames: currentGames, MaxGames: maxGames,
        Location: location ?? "", PercentLoad: percentLoad, StartTimeUtc: start);

    [Fact]
    public void The_row_shows_the_location_and_games_like_the_native_server_list()
    {
        // Native's server picker drew name | location | games cur/max | ping
        // (WinTrek/CreateGameDialog.h:79-100).
        string row = Listing(location: "EU North", currentGames: 1, maxGames: 4, percentLoad: 7).FormatRow();

        Assert.Contains("[EU North]", row);
        Assert.Contains("games 1/4", row);
        Assert.Contains("load 7%", row);
    }

    [Fact]
    public void An_unadvertised_location_and_capacity_add_nothing()
    {
        string row = Listing().FormatRow();

        Assert.DoesNotContain("[", row.Replace("[unverified]", ""));
        Assert.DoesNotContain("games", row);
        Assert.DoesNotContain("load", row);
    }

    [Fact]
    public void The_details_panel_shows_location_games_and_the_normalized_start_time()
    {
        var start = new DateTimeOffset(2032, 1, 2, 3, 4, 0, TimeSpan.Zero);
        var lines = ServerDirectorySearch.DetailsLines(
            Listing(location: "EU North", currentGames: 2, maxGames: 8, percentLoad: 11, start: start));

        Assert.Contains(lines, l => l.StartsWith("LOCATION:", StringComparison.Ordinal) && l.Contains("EU North"));
        Assert.Contains(lines, l => l.StartsWith("GAMES:", StringComparison.Ordinal) && l.Contains("2/8") && l.Contains("11%"));
        Assert.Contains(lines, l => l.StartsWith("STARTS:", StringComparison.Ordinal) && l.Contains("2032-01-02 03:04 UTC"));
    }

    [Fact]
    public void No_start_time_keeps_the_pre_p894_panel()
    {
        var lines = ServerDirectorySearch.DetailsLines(Listing());

        Assert.DoesNotContain(lines, l => l.StartsWith("STARTS:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("LOCATION:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("GAMES:", StringComparison.Ordinal));
    }

    [Fact]
    public void Listings_requiring_a_newer_client_are_dropped_and_counted()
    {
        ServerListing[] listings =
        [
            Listing(),
            Listing(minClientVersion: "2.0.0"),
            Listing(minClientVersion: "0.5.0"),
        ];

        var (visible, hidden) = ServerListing.FilterForClient(listings, "1.0.0");

        Assert.Equal(2, visible.Count);
        Assert.Equal(1, hidden);
        Assert.All(visible, l => Assert.False(l.RequiresNewerClient("1.0.0")));
    }

    [Fact]
    public void A_client_with_no_declared_minimum_never_hides_anything()
    {
        var (visible, hidden) = ServerListing.FilterForClient([Listing(), Listing()], "0.0.0");

        Assert.Equal(2, visible.Count);
        Assert.Equal(0, hidden);
    }

    [Fact]
    public void The_lobby_wire_fields_bind_by_name()
    {
        // The directory serializes camelCase (P894 appended location/percentLoad/
        // startTimeUtc/currentGames/maxGames); the client mirror binds the same names.
        const string json = """
        {
          "id": "s1", "name": "Server", "address": "1.2.3.4", "port": 6073,
          "playerCount": 5, "maxPlayers": 32, "missionName": "Wing War",
          "coreFile": "PCore001.igc", "version": "0.1.0",
          "registeredUtc": "2031-05-04T12:00:00+00:00",
          "lastSeenUtc": "2031-05-04T12:00:10+00:00",
          "location": "EU North", "percentLoad": 7, "currentGames": 2, "maxGames": 40,
          "startTimeUtc": "2031-05-04T12:01:00+00:00"
        }
        """;

        var listing = JsonSerializer.Deserialize<ServerListing>(json, ServerListing.JsonOptions);

        Assert.NotNull(listing);
        Assert.Equal("EU North", listing!.Location);
        Assert.Equal(7, listing.PercentLoad);
        Assert.Equal(2, listing.CurrentGames);
        Assert.Equal(40, listing.MaxGames);
        Assert.Equal(new DateTimeOffset(2031, 5, 4, 12, 1, 0, TimeSpan.Zero), listing.StartTimeUtc);
    }

    [Fact]
    public void The_core_list_binds_by_name()
    {
        const string json = """{ "cores": [ { "file": "PCore001.igc", "servers": 2 } ], "servers": 2 }""";

        var cores = JsonSerializer.Deserialize<LobbyCoreList>(json, ServerListing.JsonOptions);

        Assert.NotNull(cores);
        var core = Assert.Single(cores!.Cores);
        Assert.Equal("PCore001.igc", core.File);
        Assert.Equal(2, core.Servers);
    }
}
