using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P508 — the client-world side of the native own-ship model: the ship cookie flows
/// in on SET_CLUSTER(7) (join + warp) and is renewed by SHIP_RESET(183) on pose
/// rejection, and the own ship is flagged so the host re-seeds its local simulation.
/// </summary>
public sealed class ClientWorldShipResetTests
{
    private static ServerShipUpdate Pose(short sid, Vector3 pos) =>
        ServerShipUpdate.FromValues(
            shipId: sid, stateM: 0, controls: ControlData.Neutral, time: 900u,
            position: pos, orientation: Orientation.Identity, velocity: Vector3.Zero,
            turnRates: new float[3],
            hullFrac: 1f, shieldFrac: 0f, ammoFrac: 0f, fuelFrac: 1f, energyFrac: 1f, oreFrac: 0f, power: 1f);

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    [Fact]
    public void SetCluster_stamps_the_ship_cookie_on_the_own_ship()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 5, Ship = Pose(7, new Vector3(1, 2, 3)), Cookie = 0xAB });

        Assert.Equal((short)7, w.MyShipId);
        var e = w.Ships[7];
        Assert.True(e.IsMine);
        Assert.Equal((byte)0xAB, e.Cookie);
    }

    [Fact]
    public void ShipReset_adopts_new_cookie_updates_pose_and_flags_own_ship()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 5, Ship = Pose(7, new Vector3(1, 2, 3)), Cookie = 0xAB });

        var resetPose = Pose(7, new Vector3(100, 200, 300));
        w.Apply(new ShipReset { Ship = resetPose, Cookie = 0xCD });

        var e = w.Ships[7];
        Assert.Equal((byte)0xCD, e.Cookie);
        Assert.True(e.HasPendingReset, "the own ship must be flagged for the host to re-seed its local simulation");
        Assert.NotNull(e.Latest);
        Assert.True(Dist(e.Latest!.Position, new Vector3(100, 200, 300)) < 1e-3f, "the reset pose must become the authoritative pose");

        w.ClearOwnShipResetFlag(7);
        Assert.False(e.HasPendingReset, "a single consumer clears the latch");
    }

    [Fact]
    public void ShipReset_for_a_non_own_ship_updates_cookie_but_does_not_flag()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 5, Ship = Pose(7, new Vector3(1, 2, 3)), Cookie = 0xAB });

        w.Apply(new ShipReset { Ship = Pose(8, new Vector3(50, 60, 70)), Cookie = 0xEE });

        var e = w.Ships[8];
        Assert.Equal((byte)0xEE, e.Cookie);
        Assert.False(e.HasPendingReset, "only the OWN ship is flagged for local re-seed");
    }
}
