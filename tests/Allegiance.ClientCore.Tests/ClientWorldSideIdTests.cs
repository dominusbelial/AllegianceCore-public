using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P369 — the local player's OWN ship must carry its real side on
/// <see cref="ShipEntity.SideId"/>, never the C# default 0 (which is a real,
/// valid side in some missions, so the local hull silently resolves as the
/// wrong civ — e.g. a Ga'Taraan/Bravo pilot whose own ship renders as if it
/// were an Alpha craft).
/// <para>
/// Wire truth: the server's own-ship <c>EXPORT(26)</c> deliberately carries
/// <c>ShipId = NA(-1)</c> (the native "this is mine" dummy) and seats the real
/// side into <see cref="ClientWorld.MySideId"/>; the real own-ship entity is
/// keyed by the real <c>MyShipId</c>, which only <c>SET_CLUSTER(7)</c> assigns.
/// <c>ShipEntity.SideId</c> has exactly ONE other assignment site
/// (the real-id branch of <c>Apply(ExportShip)</c>), which the dummy-id export
/// always skips — so without P369 the own-ship entity's <c>SideId</c> stayed 0
/// forever. P369 stamps it from <c>MySideId</c> in both orderings of
/// dummy-EXPORT vs SET_CLUSTER.
/// </para>
/// </summary>
public class ClientWorldSideIdTests
{
    private static SetCluster SetClusterFor(short myId) => new()
    {
        SectorId = 1,
        Ship = ServerShipUpdate.FromValues(
            myId, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f),
        Cookie = 0x1,
    };

    private const short MyId = 42;

    /// <summary>⭐ The bug's exact order (the usual join burst): the own-ship
    /// dummy EXPORT (ShipId = -1, carries MySideId) arrives, THEN SET_CLUSTER
    /// assigns MyShipId. The entity created by SET_CLUSTER must pick up the
    /// already-known MySideId.</summary>
    [Fact]
    public void OwnShip_gets_side_when_SetCluster_follows_dummy_export()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });   // own side = 3 (Bravo/Ga'Taraan)
        world.Apply(SetClusterFor(MyId));

        Assert.Equal<short?>(3, world.MySideId);
        Assert.Equal(MyId, world.MyShipId);
        Assert.True(world.Ships[MyId].IsMine, "SET_CLUSTER marks the own ship");
        // The whole point of P369: the own entity's SideId must be the REAL side.
        Assert.Equal<short>(3, world.Ships[MyId].SideId);
    }

    /// <summary>⭐ The converse ordering: SET_CLUSTER (MyShipId = 42) first, then
    /// the own-ship dummy EXPORT arrives with MySideId. Once MySideId is known,
    /// the dummy branch stamps the already-existing own entity's SideId.</summary>
    [Fact]
    public void OwnShip_gets_side_when_dummy_export_follows_SetCluster()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId));
        Assert.Equal<short>(0, world.Ships[MyId].SideId);   // correct: side not known yet
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });

        Assert.Equal<short?>(3, world.MySideId);
        Assert.Equal<short>(3, world.Ships[MyId].SideId);
    }

    /// <summary>A real-id EXPORT of our own ship (the join-refresh path) still
    /// sets the side via the existing real-id branch — P369 must not regress it.
    /// A side of 0 is legitimate here because the wire genuinely says 0 (the
    /// exporter's side), not a default.</summary>
    [Fact]
    public void RealId_export_of_own_ship_still_sets_side()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 3 });
        world.Apply(SetClusterFor(MyId));
        world.Apply(new ExportShip { ShipId = MyId, HullId = 13, SideId = 3, CharacterName = "me" });

        Assert.Equal<short>(3, world.Ships[MyId].SideId);
        Assert.Equal<short>(13, world.Ships[MyId].HullId);
    }
}
