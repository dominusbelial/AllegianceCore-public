using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P629ShipWarningTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Wire_roundtrip_and_rising_edges_are_idempotent(byte bits)
    {
        var message = new ShipWarningUpdate { ShipId = 123, Mask = (ShipWarningMask)bits };
        var decoded = ShipWarningUpdate.Decode(message.Encode());
        Assert.Equal(message.ShipId, decoded.ShipId); Assert.Equal(message.Mask, decoded.Mask);
        var state = new ShipWarningState(); state.Apply(decoded.Mask); state.Apply(decoded.Mask);
        Assert.Equal(message.Mask, state.TakeRisingEdges()); Assert.Equal(ShipWarningMask.None, state.TakeRisingEdges());
        Assert.Equal(bits != 0, state.LoopActive);
        state.Apply(0); Assert.False(state.LoopActive); state.Apply(message.Mask);
        Assert.Equal(message.Mask, state.TakeRisingEdges());
    }

    [Fact]
    public void Both_bits_share_loop_and_reset_discards_pending_audio()
    {
        var state = new ShipWarningState(); state.Apply(ShipWarningMask.OutOfBounds);
        Assert.Equal(ShipWarningMask.OutOfBounds, state.TakeRisingEdges());
        state.Apply(ShipWarningMask.OutOfBounds | ShipWarningMask.CrowdedSector);
        Assert.Equal(ShipWarningMask.CrowdedSector, state.TakeRisingEdges());
        state.Apply(ShipWarningMask.CrowdedSector); Assert.True(state.LoopActive);
        Assert.Equal(ShipWarningMask.None, state.TakeRisingEdges());
        state.Reset(); Assert.False(state.LoopActive); Assert.Equal(ShipWarningMask.None, state.TakeRisingEdges());
    }

    [Fact]
    public void World_applies_owner_only_and_clears_on_sector_change_disconnect_and_rotation()
    {
        var world = new ClientWorld();
        var ship = new Ship(123, Allegiance.Core.DataHullTypeIGC.Decode(new byte[Allegiance.Core.DataHullTypeIGC.Size]),
            Vector3.Zero, Vector3.Zero, Orientation.Identity);
        SetCluster Seat(short sector) => new() { SectorId = sector, Ship = ServerShipUpdate.FromModel(123, ship, 1) };
        world.Apply(Seat(0));
        world.Apply(new ShipWarningUpdate { ShipId = 124, Mask = ShipWarningMask.CrowdedSector });
        Assert.Equal(ShipWarningMask.None, world.ShipWarnings.Mask);
        world.Apply(new ShipWarningUpdate { ShipId = 123, Mask = ShipWarningMask.CrowdedSector });
        Assert.Equal(ShipWarningMask.CrowdedSector, world.ShipWarnings.TakeRisingEdges());
        world.Apply(Seat(1)); Assert.Equal(ShipWarningMask.None, world.ShipWarnings.Mask);
        world.Apply(new ShipWarningUpdate { ShipId = 123, Mask = ShipWarningMask.OutOfBounds });
        world.BeginNextMission(); Assert.Equal(ShipWarningMask.None, world.ShipWarnings.Mask);
        world.ShipWarnings.Apply(ShipWarningMask.CrowdedSector); world.Reset();
        Assert.Equal(ShipWarningMask.None, world.ShipWarnings.Mask);
    }

    [Fact]
    public void Malformed_wire_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ShipWarningUpdate.Decode(new byte[7]));
        var bytes = new ShipWarningUpdate().Encode(); bytes[2] = 0;
        Assert.Throws<ArgumentException>(() => ShipWarningUpdate.Decode(bytes));
    }
}
