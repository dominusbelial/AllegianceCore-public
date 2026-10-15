using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P663TreasureMotionTests
{
    [Fact]
    public void ExportedTrajectory_ClientSeesDecelerationLateArrivalAndCompleteStop()
    {
        double local = 0;
        var world = new ClientWorld { NowSeconds = () => local };
        world.ObserveServerTime(12000, 0); // arrives two seconds into flight
        var spawn = new TreasureSpawn { Data = new([10, 20, 30], [60, 80, 0], 30, 10000, 7, 101, 1, 101, TreasureCode.Cash, false) };
        // Exercise the real codec and world dispatch, preserving native time0/v0.
        world.Apply((IFedMessage)TreasureSpawn.Decode(spawn.Encode()));
        var t = world.Treasures[7];
        Assert.Equal(118, t.Position.X, 3); // distance = 100*2 - .5*10*4 = 180
        Assert.Equal(164, t.Position.Y, 3);
        Assert.Equal(80, t.Velocity.Length(), 3);
        local = 3;
        Assert.Equal(235, t.Position.X, 3); // 5s, distance = 375
        Assert.Equal(50, t.Velocity.Length(), 3);
        local = 20;
        Assert.Equal(new Vector3(310, 420, 30), t.Position);
        Assert.Equal(Vector3.Zero, t.Velocity);
        world.Apply(new DestroyTreasure { TreasureId = 7, SectorId = 1 });
        Assert.Empty(world.Treasures);
    }

    [Fact]
    public void Prediction_IntegratesTimeScaleChanges_AndSectorEntryReplacesSnapshot()
    {
        double local = 0;
        var world = new ClientWorld { NowSeconds = () => local };
        world.ObserveServerTime(10000, 0);
        world.Apply(new TreasureSpawn { Data = new([0, 0, 0], [100, 0, 0], 30, 10000, 7, 101, 1, 101, TreasureCode.Cash, false) });
        local = 2;
        world.TimeScale = 0.5;
        local = 6;
        Assert.Equal(320, world.Treasures[7].Position.X, 3); // 2 + 4*.5 = 4 sim seconds
        var reader = new WireReader(new byte[ServerShipUpdate.Width]);
        world.Apply(new SetCluster { SectorId = 2, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        Assert.Empty(world.Treasures);
        world.Reset();
        world.ObserveServerTime(1000, 0);
        world.Apply(new TreasureSpawn { Data = new([0, 0, 0], [100, 0, 0], 30, 1000, 7, 101, 1, 101, TreasureCode.Cash, false) });
        Assert.Equal(Vector3.Zero, world.Treasures[7].Position);
    }
}
