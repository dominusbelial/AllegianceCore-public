using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P639 — tests for the RANK_INFO(13) client plumbing: the rank-name table is
/// stored on <see cref="ClientWorld"/> via <see cref="ClientWorld.Apply(IFedMessage)"/>,
/// and <see cref="RankNameLookup"/> resolves a roster rank id to its display
/// name exactly as native <c>BaseClient::LookupRankName</c> does
/// (clintlib.cpp:2670-2710). Pure-domain: no sockets, no server — messages are
/// constructed directly and fed through the same <c>Apply</c> the dispatch uses.
/// </summary>
public class ClientWorldRankTests
{
    private static RankInfoEntry Entry(short rank, string name) =>
        new(RequiredRanking: 0, Civ: -1, Rank: rank, RankName: name);

    private static RankInfo Table(params RankInfoEntry[] entries) =>
        new() { Ranks = entries };

    [Fact]
    public void RankTable_IsEmpty_UntilRankInfoArrives()
    {
        var world = new ClientWorld();
        Assert.Empty(world.RankTable);
    }

    [Fact]
    public void RankInfo_Apply_StoresWholeTable()
    {
        var world = new ClientWorld();
        world.Apply(Table(Entry(0, "Newbie"), Entry(3, "Novice 3"), Entry(31, "Master 1")));
        Assert.Equal(3, world.RankTable.Count);
        Assert.Equal("Master 1", world.RankTable[2].RankName);
    }

    [Fact]
    public void RankInfo_Apply_ReplacesPriorTableWholesale()
    {
        var world = new ClientWorld();
        world.Apply(Table(Entry(0, "Newbie")));
        world.Apply(Table(Entry(1, "Novice 1"), Entry(2, "Novice 2")));
        // last receive wins wholesale (native's delete[] + fresh memcpy)
        Assert.Equal(2, world.RankTable.Count);
        Assert.Equal("Novice 1", world.RankTable[0].RankName);
    }

    [Theory]
    [InlineData(0, "Newbie")]
    [InlineData(3, "Novice 3")]
    [InlineData(39, "Master 9")]
    public void LookupRankName_InRange_ReturnsTableName(short rank, string expected)
    {
        var table = Table(
            Entry(0, "Newbie"), Entry(1, "Novice 1"), Entry(2, "Novice 2"),
            Entry(3, "Novice 3"), Entry(39, "Master 9"));
        Assert.Equal(expected, RankNameLookup.Lookup(table.Ranks, rank));
    }

    [Fact]
    public void LookupRankName_AboveCeiling_ClampsToHighest()
    {
        var table = Table(Entry(0, "Newbie"), Entry(39, "Master 9"));
        // rank beyond the table ceiling → highest entry (clintlib.cpp:2689-2693)
        Assert.Equal("Master 9", RankNameLookup.Lookup(table.Ranks, 55));
    }

    [Fact]
    public void LookupRankName_EmptyTable_ReturnsEmpty()
    {
        // native early-out: m_cRankInfo <= 0 && !zoneclub → "" (clintlib.cpp:2678-2682)
        Assert.Equal("", RankNameLookup.Lookup(Array.Empty<RankInfoEntry>(), 3));
    }

    [Fact]
    public void LookupRankName_TrimsTrailingSpaces()
    {
        // RemoveTrailingSpaces (clintlib.cpp:2699)
        var table = Table(Entry(7, "Veteran 7  "));
        Assert.Equal("Veteran 7", RankNameLookup.Lookup(table.Ranks, 7));
    }
}
