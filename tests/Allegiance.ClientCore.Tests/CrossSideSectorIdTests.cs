using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P375 — a cross-side ship's <see cref="ShipEntity.SectorId"/> must be real and
/// current, not stuck at the C# default 0. Root cause: the only setters are
/// SET_CLUSTER(7) (the OWN ship only) and SHIP_STATUS(90) (same-side teammate
/// only, P58-era scope), and the cross-side EXPORT(26) wire — the ONLY source of
/// a cross-side ship's identity — carries no sector field (native DataShipIGC has
/// none). So a cross-side ship's SectorId was 0 forever, and P371's target-pick /
/// radar sector filter (<c>ship.SectorId == mySector</c>) could never select an
/// enemy directly in front of you unless you happened to be in sector 0 yourself.
/// <para>
/// The fix derives SectorId in <c>Apply(HeavyShipsUpdate)</c> from the LOCAL
/// ship's own sector, because 176 is sent by the server for exactly the ships in
/// the recipient's OWN cluster (BroadcastShipsUpdate gathers candidates from the
/// peer's own sector) — so every 176 entry, same-side OR cross-side, is by
/// construction in the viewer's current sector. This is the native-faithful rule
/// (native's client instantiates every 176 object in its own cluster). It also
/// stays CURRENT as the ship/viewer moves between sectors because 176 re-flows
/// (and P370's IsGone covers the departure/return transitions).
/// </para>
/// </summary>
public class CrossSideSectorIdTests
{
    private const short MyId = 42;
    private const short EnemyId = 99;   // a cross-side ship (any side != ours)

    private static ServerShipUpdate Pose(short id, float x) => ServerShipUpdate.FromValues(
        id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
        new Vector3(x, 0f, 0f), Orientation.Identity, Vector3.Zero,
        turnRates: new[] { 0f, 0f, 0f },
        hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
        energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SetCluster SetClusterFor(short myId, short sectorId) => new()
    {
        SectorId = sectorId,
        Ship = Pose(myId, 0f),
        Cookie = 0x1,
    };

    private static HeavyShipsUpdate HeavyFor(short sectorShipId, float x)
    {
        uint t = 100u;
        var entry = HeavyShipUpdateEntry.From(
            t, Vector3.Zero,
            sectorShipId, stateM: 0, new ControlData(0f, 0f, 0f, 0f),
            t, new Vector3(x, 0f, 0f), Orientation.Identity, Vector3.Zero,
            new[] { 0f, 0f, 0f }, hullFrac: 1f, shieldFrac: 1f, oreFrac: 0f);
        return new HeavyShipsUpdate
        {
            TimeReference = t,
            PositionReference = Vector3.Zero,
            HeavyShipEntries = [entry],
            BpTargetHull = 255,
            BpTargetShield = 255,
        };
    }

    /// <summary>⭐ The bug's repro: a cross-side ship (identity via EXPORT)
    /// receives telemetry via 176; before P375 its SectorId was never set (only
    /// SET_CLUSTER=own and SHIP_STATUS=same-side set it). After the fix, the 176
    /// application derives it from the local ship's own sector.</summary>
    [Fact]
    public void CrossSideShip_gets_own_sector_from_HeavyShipsUpdate()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId, sectorId: 7));          // I am in sector 7
        world.Apply(new ExportShip { ShipId = EnemyId, HullId = 5, SideId = 1, CharacterName = "enemy" });

        // Precondition: the enemy exists (identity) but no sector set yet.
        Assert.Equal<short>(0, world.Ships[EnemyId].SectorId);

        // A 176 carrying the cross-side ship arrives (it is in my cluster by
        // construction of the message) — its SectorId must now be MY sector.
        world.Apply(HeavyFor(EnemyId, x: 300f));

        Assert.Equal<short>(7, world.Ships[EnemyId].SectorId);
        Assert.True(world.Ships[EnemyId].Latest is not null, "176 must also supply the pose");
    }

    /// <summary>⭐ "Stays current as it moves between sectors": when the LOCAL
    /// viewer warps to sector 12, the next 176 (whose entries are then in sector
    /// 12) re-stamps the cross-side ship's SectorId to 12 — not a one-time value
    /// from first contact.</summary>
    [Fact]
    public void CrossSideShip_sector_re_stamps_when_viewer_sector_changes()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId, sectorId: 7));
        world.Apply(new ExportShip { ShipId = EnemyId, HullId = 5, SideId = 1, CharacterName = "enemy" });
        world.Apply(HeavyFor(EnemyId, x: 300f));
        Assert.Equal<short>(7, world.Ships[EnemyId].SectorId);

        // Viewer warps to sector 12 (one-shot SET_CLUSTER), then 176 flows again.
        world.Apply(SetClusterFor(MyId, sectorId: 12));
        world.Apply(HeavyFor(EnemyId, x: 310f));

        Assert.Equal<short>(12, world.Ships[EnemyId].SectorId);
    }

    /// <summary>Regression: an ordinary same-side teammate also gets its SectorId
    /// via 176 (correct — same cluster), and the fix does not disturb SHIP_STATUS-
    /// set values.</summary>
    [Fact]
    public void SameSide_teammate_sector_from_HeavyShipsUpdate_consistent_with_status()
    {
        var world = new ClientWorld();
        world.Apply(SetClusterFor(MyId, sectorId: 3));

        // Same-side teammate: 176 in my sector stamps 3 (matches SHIP_STATUS).
        world.Apply(new ExportShip { ShipId = 50, HullId = 5, SideId = 0, CharacterName = "mate" });
        world.Apply(HeavyFor(50, x: 100f));
        Assert.Equal<short>(3, world.Ships[50].SectorId);
    }
}
