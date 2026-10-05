using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P540c — the client-side warp-destination resolver
/// (<see cref="WarpDestinationResolver.DestinationSector"/>): a warp's
/// <c>DestinationId</c> names its PARTNER warp (native <c>warpIGC.h:129-141</c>),
/// and the destination SECTOR is that partner warp's own <c>ClusterId</c>. This
/// is the exact resolution the server's transit performs
/// (<c>Mission.Assemble</c> lines 518-542) and the one the client consumers
/// (<c>Game1.AllWarps</c>, <c>SpectatorFrame.BuildSectorFrame</c>) must use so
/// they never aim at a phantom sector (a warp id fed in as a sector id).
/// </summary>
public class WarpDestinationResolverTests
{
    private static DataWarpIGC Warp(short id, short dest, short cluster) =>
        new(id, dest, 50, "", "", $"W{id}", [0f, 0f, 0f], [1f, 0f, 0f],
            [0f, 0f, 0f, 1f], 1f, cluster);

    [Fact]
    public void ResolvesDestinationSector_FromPartnersCluster()
    {
        // warp 10 sits in sector 100; its DestinationId 20 names its PARTNER warp,
        // which sits in sector 200. The destination sector is 200 (the partner's
        // ClusterId), NOT 20 (a warp id).
        var warps = new Dictionary<short, DataWarpIGC>
        {
            [10] = Warp(id: 10, dest: 20, cluster: 100),
            [20] = Warp(id: 20, dest: 10, cluster: 200),
        };

        Assert.Equal((short)200, WarpDestinationResolver.DestinationSector(warps, 10));
        Assert.Equal((short)100, WarpDestinationResolver.DestinationSector(warps, 20));
    }

    [Fact]
    public void UnknownPartner_ReturnsNull_NeverGuessesWarpIdAsSector()
    {
        // warp 10's DestinationId (20) is NOT in the table. The resolver must
        // return null (the caller skips the edge) — it must NEVER answer 20, i.e.
        // treat the warp id as if it were a sector id (the phantom-sector bug).
        var warps = new Dictionary<short, DataWarpIGC>
        {
            [10] = Warp(id: 10, dest: 20, cluster: 100),
        };

        Assert.Null(WarpDestinationResolver.DestinationSector(warps, 10));
    }

    [Fact]
    public void UnknownWarpId_OrNullTable_ReturnsNull()
    {
        var warps = new Dictionary<short, DataWarpIGC>
        {
            [10] = Warp(id: 10, dest: 20, cluster: 100),
        };

        Assert.Null(WarpDestinationResolver.DestinationSector(warps, 99));
        Assert.Null(WarpDestinationResolver.DestinationSector(null!, 10));
    }
}
