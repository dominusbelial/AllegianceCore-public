using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P267 — ClientWorld's wiring of the one-shot sound events from their real
/// wire messages: STATION_DESTROYED(59), STATION_CAPTURE(58) and
/// DEV_COMPLETED(8) each record a <see cref="ClientSoundEvent"/> that
/// <c>Game1</c> drains into the audio manager, with the own-vs-enemy variant
/// decided from the local side (native appmsg.cpp:548-551, 674-677, 3326).
/// </summary>
public class ClientWorldSoundEventTests
{
    private static DataStationIGC Station(short id, short side, short cluster = 1) =>
        new(
            Position: new float[] { 0f, 0f, 0f },
            Up: new float[] { 0f, 1f, 0f },
            Forward: new float[] { 0f, 0f, -1f },
            Rotation: new float[] { 0f, 0f, 0f, 0f },
            ClusterId: cluster, SideId: side, StationId: id, StationTypeId: 1,
            BpHull: 0, BpShield: 0, Name: "base");

    /// <summary>Seed the client's own side id via the own-ship dummy EXPORT
    /// (ShipId = NA(-1)), the only wire source of the player's own team.</summary>
    private static void SetMySide(ClientWorld world, short side) =>
        world.Apply(new ExportShip { ShipId = -1, SideId = side });

    [Fact]
    public void DevCompleted_RecordsDevChimeEvent()
    {
        var world = new ClientWorld();
        world.Apply(new DevCompleted { DevId = 41 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.DevCompleted, ev.Kind);
        Assert.Equal(41, ev.Id);
        Assert.Empty(world.TakeSoundEvents());   // drained exactly once
    }

    [Fact]
    public void OwnStationDestroyed_RecordsDestroyed_AndRemovesStation()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 2) });

        world.Apply(new StationDestroyed { StationId = 7, Launcher = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationDestroyed, ev.Kind);
        Assert.Equal(7, ev.Id);
        Assert.False(world.Stations.ContainsKey(7), "destroyed station must leave the world table");
    }

    [Fact]
    public void EnemyStationDestroyed_RecordsEnemyDestroyed()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 9) });   // enemy

        world.Apply(new StationDestroyed { StationId = 7, Launcher = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationEnemyDestroyed, ev.Kind);
    }

    [Fact]
    public void LostOurStationToEnemy_RecordsCaptured_AndReSides()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 2) });

        // sidOld == ours (we lost it to the enemy) → the "captured" readout.
        world.Apply(new StationCapture { StationId = 7, SidOld = 2, SidNew = 9, ShipIdCredit = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationCaptured, ev.Kind);
        Assert.Equal(9, world.Stations[7].SideId);   // re-sided to the new owner
    }

    [Fact]
    public void TookEnemyStation_RecordsEnemyCaptured()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 9) });

        // sidNew == ours (we captured an enemy base) → the "enemy captured" readout.
        world.Apply(new StationCapture { StationId = 7, SidOld = 9, SidNew = 2, ShipIdCredit = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationEnemyCaptured, ev.Kind);
        Assert.Equal(2, world.Stations[7].SideId);
    }

    [Fact]
    public void SoundEvents_AreBoundedAndAtomic()
    {
        var world = new ClientWorld();
        world.Apply(new DevCompleted { DevId = 1 });
        world.Apply(new DevCompleted { DevId = 2 });

        var all = world.TakeSoundEvents();
        Assert.Equal(2, all.Count);
        Assert.Empty(world.TakeSoundEvents());   // second drain is empty
    }
}
