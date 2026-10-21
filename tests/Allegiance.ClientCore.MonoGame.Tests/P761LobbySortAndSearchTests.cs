using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P761 — the SERVER LIST's sort stack, filters and find-player/details search
/// (native gamescreen.cpp:858-898 OnButtonBarGames, :900-927 ResetFilters,
/// :1348-1352 find player via FindDialogPopup, :1308-1326 details).
/// </summary>
public class P761LobbySortAndSearchTests
{
    private static ServerListing Listing(string name, string mission = "XXV_simple.igc",
        string core = "PCore001.igc", int players = 0, DateTimeOffset? registered = null) => new(
        Id: "id-" + name + mission, Name: name, Address: "127.0.0.1", Port: 6777,
        PlayerCount: players, MaxPlayers: 8, MissionName: mission, CoreFile: core,
        Version: "0.1", RegisteredUtc: registered ?? DateTimeOffset.UnixEpoch,
        LastSeenUtc: DateTimeOffset.UnixEpoch);

    // --- the sort stack (OnButtonBarGames, gamescreen.cpp:858-898) ----------

    [Fact]
    public void Click_MakesTheColumnTheLastCriterion_InARemovingStack()
    {
        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColName);
        sort.Click(LobbyGameSort.ColCore);
        Assert.Equal(
            new[] { LobbyGameSort.ColName, LobbyGameSort.ColCore },
            sort.Stack.Select(c => c.Column));
        // The last click is the PRIMARY key (it sorts last in the stable passes).
        Assert.Equal(LobbyGameSort.ColCore, sort.Stack[^1].Column);

        // Clicking an earlier column removes its earlier entry and re-pushes it
        // as the new last criterion (m_vuSorts.Find/Remove/PushEnd).
        sort.Click(LobbyGameSort.ColName);
        Assert.Equal(new[] { LobbyGameSort.ColCore, LobbyGameSort.ColName },
            sort.Stack.Select(c => c.Column));
    }

    [Fact]
    public void Click_TogglesReverseOnlyOnTheCurrentTopEntry()
    {
        // Native reads the header's Checked2 for the CLICKED column: it is set
        // only while that column is the (unreversed) top criterion, so clicking
        // the top column flips its direction and a fresh column starts normal.
        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColPlayers);
        Assert.False(sort.Stack[^1].Reversed);
        sort.Click(LobbyGameSort.ColPlayers);
        Assert.True(sort.Stack[^1].Reversed);
        sort.Click(LobbyGameSort.ColPlayers);
        Assert.False(sort.Stack[^1].Reversed);

        sort.Click(LobbyGameSort.ColPlayers);            // normal
        sort.Click(LobbyGameSort.ColName);               // new column: normal
        sort.Click(LobbyGameSort.ColPlayers);            // re-clicked: normal again (old entry removed)
        Assert.All(sort.Stack, c => Assert.False(c.Reversed));
    }

    [Fact]
    public void ResetFilters_SortBecomesPlayersReversed_Alone()
    {
        // ResetFilters (gamescreen.cpp:900-927, WLP 2005): clear the stack then
        // push column 6 (players) reversed — "the big numbers on top".
        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColName);
        sort.Click(LobbyGameSort.ColCore);
        sort.ResetFilters();
        var criterion = Assert.Single(sort.Stack);
        Assert.Equal(LobbyGameSort.ColPlayers, criterion.Column);
        Assert.True(criterion.Reversed);
    }

    [Fact]
    public void Apply_LastCriterionDominates_EarlierOnesBreakTies()
    {
        // UpdateGameList wraps one stable sort per stack entry: the LAST criterion
        // is primary, earlier criteria break its ties (gamescreen.cpp:1185-1238).
        var a = Listing("alpha", mission: "M1", core: "A.igc");
        var b = Listing("bravo", mission: "M1", core: "B.igc");
        var c = Listing("charlie", mission: "M2", core: "A.igc");
        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColCore);   // tie-breaker
        sort.Click(LobbyGameSort.ColName);   // primary (M1 group)
        var ordered = sort.Apply(new[] { c, b, a }, (l, col) => LobbyGameSort.ListingKey(l, col));
        Assert.Equal([a, b, c], ordered);   // M1 group first (name asc); inside it, A.igc before B.igc (core tie-break)

        // Reversed pass inverts its own criterion only (SortingList/ReverseCompare).
        var rev = new LobbyGameSort();
        rev.Click(LobbyGameSort.ColPlayers);
        rev.Click(LobbyGameSort.ColPlayers); // toggled to reversed
        var down = rev.Apply(new[] { Listing("x", players: 1), Listing("y", players: 5) },
            (l, col) => LobbyGameSort.ListingKey(l, col));
        Assert.Equal(5, down[0].PlayerCount);
    }

    // --- the filters (ResetFilters + FilterDialogPopup semantics) -----------

    [Fact]
    public void Filters_ResetSetsEveryFlagDontCare_AndClearsTheSubstring()
    {
        var f = new LobbyGameFilters
        {
            Developments = LobbyGameSort.BooleanFilter.Yes,
            Conquest = LobbyGameSort.BooleanFilter.No,
            NameSubstring = "war",
        };
        f.Reset();
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, f.Developments);
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, f.Conquest);
        Assert.Equal("", f.NameSubstring);
        // A clean filter keeps everything visible.
        Assert.True(f.Apply(Listing("any")));
    }

    [Fact]
    public void Filters_SubstringApplies_ThroughTheListingMatch()
    {
        var f = new LobbyGameFilters { NameSubstring = "wing" };
        Assert.True(f.Apply(Listing("alpha", mission: "Wing War")));
        Assert.False(f.Apply(Listing("alpha", mission: "Storm")));
    }

    // --- find player + details (gamescreen.cpp:1348-1352, 1308-1326) --------
    // P761b: the open listing carries NO rosters (privacy fix), so the search is
    // a per-name LOBBY QUERY — native's FM_C_FIND_PLAYER — rather than a local
    // scan of the fetched payload.

    [Fact]
    public void FindPlayerRequestPath_AsksAboutExactlyOneEscapedName()
    {
        Assert.Equal("/api/servers/findplayer?name=PilotOne",
            ServerDirectorySearch.FindPlayerRequestPath("PilotOne"));
        Assert.Equal("/api/servers/findplayer?name=Pilot%20One",
            ServerDirectorySearch.FindPlayerRequestPath("  Pilot One "));
        // No name → no request (native does not ask about an empty name).
        Assert.Null(ServerDirectorySearch.FindPlayerRequestPath("   "));
        Assert.Null(ServerDirectorySearch.FindPlayerRequestPath(null));
    }

    [Fact]
    public void IndexOfListing_SelectsTheRowTheLobbyAnsweredWith()
    {
        var a = Listing("alpha");
        var b = Listing("bravo");
        var list = new[] { a, b };
        Assert.Equal(1, ServerDirectorySearch.IndexOfListing(list, b.Id));
        Assert.Equal(-1, ServerDirectorySearch.IndexOfListing(list, "not-a-listing"));
        Assert.Equal(-1, ServerDirectorySearch.IndexOfListing(list, null));
    }

    [Fact]
    public void DetailsLines_ShowTheDirectorysGenuineFields_AndNoPilotNames()
    {
        var l = Listing("Domino's Fortress", mission: "Wing War", core: "PCore001.igc", players: 2);
        var lines = ServerDirectorySearch.DetailsLines(l);
        Assert.Contains(lines, s => s.StartsWith("GAME:") && s.Contains("Wing War"));
        Assert.Contains(lines, s => s.StartsWith("CORE:") && s.Contains("PCore001"));
        Assert.Contains(lines, s => s.StartsWith("PLAYERS: 2"));
        // P761b — the public listing carries a COUNT, never rosters.
        Assert.DoesNotContain(lines, s => s.Contains("PILOTS"));
    }
}
