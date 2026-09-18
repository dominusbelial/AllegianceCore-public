using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P255 — the ClientWorld half of client-visible mining state: the owner-only
/// <c>OwnShipMiningUpdate(206)</c> populates <see cref="ClientWorld.MiningAsteroidId"/>
/// (the rock the local ship is drilling), and an NA message clears it back to
/// not-mining. Pure-domain (construct-then-Apply, no sockets — the
/// ClientWorldUnitTests pattern).
/// </summary>
public class ClientWorldMiningTests
{
    [Fact]
    public void MiningUpdate_Sets_The_Rock_Being_Drilled()
    {
        var world = new ClientWorld();
        Assert.Equal(-1, world.MiningAsteroidId);   // not mining until told otherwise

        world.Apply(new OwnShipMiningUpdate { ClusterId = 3, AsteroidId = 503 });
        Assert.Equal((short)503, world.MiningAsteroidId);

        // Another rock while still mining just retargets.
        world.Apply(new OwnShipMiningUpdate { ClusterId = 3, AsteroidId = 612 });
        Assert.Equal((short)612, world.MiningAsteroidId);
    }

    [Fact]
    public void Na_MiningUpdate_Clears_Back_To_Not_Mining()
    {
        var world = new ClientWorld();
        world.Apply(new OwnShipMiningUpdate { ClusterId = 3, AsteroidId = 503 });
        Assert.Equal((short)503, world.MiningAsteroidId);

        world.Apply(new OwnShipMiningUpdate { ClusterId = -1, AsteroidId = -1 });
        Assert.Equal(-1, world.MiningAsteroidId);
    }
}
