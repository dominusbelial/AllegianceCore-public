using Allegiance.ClientCore.MonoGame;
using Allegiance.Update;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P810 — the client-side minimum-client-version gate on lobby listings
/// (<see cref="ServerListing.RequiresNewerClient"/>): a server's
/// <c>minClientVersion</c> hides the listing from clients older than it — the
/// advertised, browse-time form of native's LOGON <c>crcFileList</c> gate
/// (<c>lobby/client.cpp:272-281</c>; no new data on the game wire). Pure rules,
/// both intro and connect screens consume the same filtered list.
/// </summary>
public class ServerListingMinClientVersionTests
{
    private static ServerListing Listing(string? minClientVersion) => new(
        Id: "id", Name: "Server", Address: "1.2.3.4", Port: 6073,
        PlayerCount: 0, MaxPlayers: 8, MissionName: "m", CoreFile: "PCore001.igc",
        Version: "0.1.0", RegisteredUtc: default, LastSeenUtc: default,
        Game: null, MinClientVersion: minClientVersion);

    [Fact]
    public void A_server_with_no_requirement_is_never_hidden()
    {
        Assert.False(Listing(null).RequiresNewerClient("0.0.0"));
        Assert.False(Listing("").RequiresNewerClient("0.0.0"));
    }

    [Fact]
    public void An_older_client_is_hidden()
        => Assert.True(Listing("2.0.0").RequiresNewerClient("1.9.9"));

    [Fact]
    public void A_current_client_is_shown()
        => Assert.False(Listing("2.0.0").RequiresNewerClient("2.0.0"));

    [Fact]
    public void A_never_updated_client_counts_as_too_old()
        => Assert.True(Listing("1.0.0").RequiresNewerClient(UpdateState.ResolveInstalledVersion(Path.GetTempPath())));

    [Fact]
    public void An_unparsable_minimum_never_hides_anyone()
        => Assert.False(Listing("banana").RequiresNewerClient("0.0.0"));
}
