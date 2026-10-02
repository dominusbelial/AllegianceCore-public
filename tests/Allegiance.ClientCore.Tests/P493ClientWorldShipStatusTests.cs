using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P493 — a cross-side <c>SHIP_STATUS(90)</c> entry (the spotted/hidden stream
/// the server's <c>SweepCrossSideShipStatuses</c> sends when a side's scanner
/// net — probes/ships/scan range — comes to see or loses sight of an enemy)
/// must update the enemy's LAST-KNOWN sector and state in
/// <see cref="ClientWorld.Apply(ShipStatusMessage)"/>. The client records it on
/// the <see cref="ShipEntity"/> (SectorId / State / Unknown), which is exactly
/// the "where was the enemy last seen" datum the sector map's
/// <see cref="SectorMapQueries.KnownEnemySectors"/> consumes (and the radar /
/// target-pick paths read the same fields).
/// </summary>
public class P493ClientWorldShipStatusTests
{
    private const short MyId = 42;
    private const short EnemyId = 99;
    private const short EnemySector = 1052;   // a real sector id, far from the C# default 0

    private static SetCluster SetClusterFor(short myId, short sectorId) => new()
    {
        SectorId = sectorId,
        Ship = ServerShipUpdate.FromValues(
            myId, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            new Vector3(0f, 0f, 0f), Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f),
        Cookie = 0x1,
    };

    /// <summary>A cross-side SPOTTED status for the enemy: flying, hull known,
    /// sector = where our scanner net sees it (EnemySector), unknown=false.</summary>
    private static ShipStatusMessage SpottedStatus() => new()
    {
        ShipId = EnemyId,
        HullId = 5,
        SectorId = EnemySector,
        StationId = -1,
        ParentId = -1,
        State = ShipStatusMessage.StateFlying,
        Unknown = false,
        Detected = false,
        StateTime = 0,
    };

    /// <summary>A cross-side HIDDEN status: native ShipStatusHidden preserves the
    /// last-known sector/state and only sets the unknown bit (fsship.cpp:290).</summary>
    private static ShipStatusMessage HiddenStatus()
    {
        var s = SpottedStatus();
        s.Unknown = true;
        return s;
    }

    [Fact]
    public void CrossSide_spotted_status_records_last_known_sector_and_state()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId, sectorId: 3));          // I am elsewhere; the enemy is NOT in my sector
        world.Apply(new ExportShip { ShipId = EnemyId, HullId = 5, SideId = 1, CharacterName = "enemy" });

        // Our probe/scout in sector EnemySector spots the enemy → cross-side status.
        world.Apply(SpottedStatus());

        var e = world.Ships[EnemyId];
        Assert.Equal(EnemySector, e.SectorId);          // last-known sector, in a sector we are NOT in
        Assert.Equal(ShipStatusMessage.StateFlying, e.State);
        Assert.False(e.Unknown);
        Assert.Equal<short>(5, e.HullId);
    }

    [Fact]
    public void CrossSide_hidden_status_flips_unknown_keeping_last_known_fields()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId, sectorId: 3));
        world.Apply(new ExportShip { ShipId = EnemyId, HullId = 5, SideId = 1, CharacterName = "enemy" });

        world.Apply(SpottedStatus());
        Assert.False(world.Ships[EnemyId].Unknown);

        // The enemy leaves probe range / the probe dies → the hidden transition.
        world.Apply(HiddenStatus());

        var e = world.Ships[EnemyId];
        Assert.True(e.Unknown, "hidden transition must flip Unknown");
        Assert.Equal(EnemySector, e.SectorId);          // last-known sector preserved (native SetUnknown keeps it)
        Assert.Equal(ShipStatusMessage.StateFlying, e.State);
    }
}
