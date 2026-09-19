using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P274 lobby text filter (<see cref="ServerListing.Matches"/>)
/// — the substring match that decides which advertised servers survive the
/// browser's live filter (native FilterDialogPopup/FindDialogPopup game-name
/// substring, WinTrek/gamescreen.cpp:997-999). Kept out of the renderer so it is
/// unit-testable without a graphics device (same discipline as ServerListingTests).
/// </summary>
public class LobbyFilterTests
{
    private static ServerListing Listing(string name, string mission = "XXV_simple.igc", string addr = "127.0.0.1")
        => new(
            Id: "id-" + name, Name: name, Address: addr, Port: 6777, PlayerCount: 0,
            MaxPlayers: 8, MissionName: mission, CoreFile: "PCore001.igc",
            Version: "0.1", RegisteredUtc: DateTimeOffset.UtcNow, LastSeenUtc: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Matches_EmptyOrWhitespaceOrNull_ReturnsTrue(string? needle)
    {
        // An empty filter is no filter: every server stays visible.
        Assert.True(Listing("alpha").Matches(needle!));
    }

    [Fact]
    public void Matches_NameSubstring_IsCaseInsensitive()
    {
        // Faithful to native's ToLower().Find() — a substring of the server name,
        // case-insensitive (gamescreen.cpp:997-999).
        Assert.True(Listing("Domino's Fortress").Matches("fort"));
        Assert.True(Listing("Domino's Fortress").Matches("FORTRESS"));
        Assert.True(Listing("Domino's Fortress").Matches("'s "));
        Assert.False(Listing("Domino's Fortress").Matches("zzz"));
    }

    [Fact]
    public void Matches_NameIsNotAnchoredToStart()
    {
        // Substring, not prefix: native Find() scans the whole name.
        Assert.True(Listing("Fred Zone").Matches("Zone"));
    }

    [Fact]
    public void Matches_MissionNameColumn_IsSearched()
    {
        // The port extends native's name-only match to the other genuinely-carried
        // string column shown in the row — the mission filename.
        Assert.True(Listing("alpha", mission: "XXV_simple.igc").Matches("simple"));
        Assert.True(Listing("alpha", mission: "Zone Conquest XXV").Matches("conquest"));
        Assert.False(Listing("alpha", mission: "ZZZ_storm.igc").Matches("simple"));
    }

    [Fact]
    public void Matches_AddressColumn_IsSearched()
    {
        Assert.True(Listing("alpha", addr: "192.168.0.140").Matches("192.168"));
        Assert.False(Listing("alpha", addr: "192.168.0.140").Matches("10.0"));
    }

    [Fact]
    public void Matches_NullColumns_DoesNotThrow()
    {
        // Defensive: a listing could carry null/blank text fields; the filter
        // must treat them as non-matching rather than throw.
        var l = Listing("alpha") with { Name = "", MissionName = "", Address = "" };
        Assert.True(l.Matches(""));              // empty still matches all
        Assert.False(l.Matches("anything"));     // nothing to search
    }
}
