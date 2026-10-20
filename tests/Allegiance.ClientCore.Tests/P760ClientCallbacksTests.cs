using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P760ClientCallbacksTests
{
    internal static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
                Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1)
        });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "Self" });
        world.Apply(new ExportShip { ShipId = 11, SideId = 1, CharacterName = "Target" });
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, ParentId = -1, StationId = -1 });
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, ParentId = -1, StationId = -1 });
        return world;
    }

    [Fact]
    public void KillShip_enqueues_TargetKilledModel_with_victim_type_and_id()
    {
        var world = World();

        world.Apply(new KillShip { ShipId = 11, IdCredit = 10, BDeathCredit = true });
        var kill = Assert.Single(world.TakeTargetKilledModels());
        Assert.Equal(IgcObjectType.Ship, kill.ObjectType);
        Assert.Equal(11, kill.ModelId);

        // Drained once — the game thread takes each kill exactly once.
        Assert.Empty(world.TakeTargetKilledModels());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KillShip_only_enqueues_on_true_death(bool deathCredit)
    {
        var world = World();

        world.Apply(new KillShip { ShipId = 11, IdCredit = 10, BDeathCredit = deathCredit });
        var kills = world.TakeTargetKilledModels();
        Assert.Equal(deathCredit ? 1 : 0, kills.Count);
    }
}
