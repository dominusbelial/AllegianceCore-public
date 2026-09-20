using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P293 — pure-domain tests (no sockets, no server) for the treasure surface the
/// MonoGame client reads: the TREASURE(28) spawn table (<c>ClientWorld.Treasures</c>)
/// and its removal by DESTROY_TREASURE(30). Messages are constructed directly
/// (exactly like the other <c>ClientWorld*Tests</c>) and fed through
/// <see cref="ClientWorld.Apply"/> — mirroring the P291 mine plumbing tests. A
/// treasure must land in the world for P293's renderer to see it, and must leave
/// when the server says it was picked up (so its gold gem disappears).
/// </summary>
public class ClientWorldTreasureTests
{
    private static TreasureSpawn Spawn(short id, short cluster, short payload,
        short amount, TreasureCode code, float[] p0) =>
        new()
        {
            Data = new DataTreasureIGC(
                P0: p0, V0: [0f, 0f, 0f], Lifespan: 998f, Time0: 12u,
                ObjectId: id, TreasureId: payload, ClusterId: cluster,
                Amount: amount, TreasureCode: code, CreateNow: true),
        };

    [Fact]
    public void TreasureSpawn_UpsertsIntoTheTreasuresTable()
    {
        var world = new ClientWorld();

        world.Apply(Spawn(7, cluster: 3, payload: 920, amount: 440,
            TreasureCode.Cash, [1f, 2f, 3f]));
        world.Apply(Spawn(8, cluster: 3, payload: 922, amount: 450,
            TreasureCode.Part, [4f, 5f, 6f]));

        Assert.Equal(2, world.Treasures.Count);
        Assert.True(world.Treasures.TryGetValue(7, out var t7));
        Assert.Equal((short)3, t7.ClusterId);
        Assert.Equal((short)920, t7.PayloadId);
        Assert.Equal((short)440, t7.Amount);
        Assert.Equal(TreasureCode.Cash, t7.Code);
        Assert.Equal(new Vector3(1f, 2f, 3f), t7.Position);
    }

    [Fact]
    public void TreasureSpawn_IsUpsertByKey_LastWriteWins()
    {
        var world = new ClientWorld();

        world.Apply(Spawn(7, cluster: 3, payload: 910, amount: 400,
            TreasureCode.Cash, [1f, 2f, 3f]));
        world.Apply(Spawn(7, cluster: 4, payload: 944, amount: 980,
            TreasureCode.Flag, [9f, 9f, 9f]));

        Assert.Single(world.Treasures);
        Assert.Equal((short)4, world.Treasures[7].ClusterId);
        Assert.Equal((short)944, world.Treasures[7].PayloadId);
        Assert.Equal(TreasureCode.Flag, world.Treasures[7].Code);
        Assert.Equal(new Vector3(9f, 9f, 9f), world.Treasures[7].Position);
    }

    [Fact]
    public void DestroyTreasure_RemovesTheTreasureFromTheTable()
    {
        var world = new ClientWorld();
        world.Apply(Spawn(7, cluster: 3, payload: 926, amount: 447,
            TreasureCode.Cash, [1f, 2f, 3f]));

        world.Apply(new DestroyTreasure { TreasureId = 7, SectorId = 3 });

        Assert.Empty(world.Treasures);
        Assert.DoesNotContain(world.Treasures.Keys, k => k == 7);
    }

    [Fact]
    public void DestroyTreasure_ForAnUntrackedTreasureIsAHarmlessNoOp()
    {
        var world = new ClientWorld();
        // Destroying a treasure we never saw must not throw or corrupt the table.
        world.Apply(new DestroyTreasure { TreasureId = 987, SectorId = 3 });
        Assert.Empty(world.Treasures);
    }

    [Fact]
    public void TreasureSpawn_WithoutAPositionFallsBackToZero()
    {
        var world = new ClientWorld();

        world.Apply(Spawn(7, cluster: 3, payload: 929, amount: 455,
            TreasureCode.Cash, []));

        Assert.True(world.Treasures.TryGetValue(7, out var t7));
        Assert.Equal(Vector3.Zero, t7.Position);
    }

    [Fact]
    public void TreasureSpawnAndDestroy_ArrivingViaTheDispatchSwitch_StillWork()
    {
        // The wire path feeds the DECODED message into Apply(IFedMessage), whose
        // switch must route TreasureSpawn/DestroyTreasure to the per-type handlers
        // (the P264 lesson: a case-less per-type Apply is dead code). Boxing to
        // IFedMessage exercises exactly that dispatch, not the specific overload.
        var world = new ClientWorld();

        world.Apply((IFedMessage)Spawn(7, cluster: 3, payload: 935, amount: 461,
            TreasureCode.Cash, [1f, 2f, 3f]));
        Assert.Single(world.Treasures);
        Assert.Equal((short)935, world.Treasures[7].PayloadId);

        world.Apply((IFedMessage)new DestroyTreasure { TreasureId = 7, SectorId = 3 });
        Assert.Empty(world.Treasures);
    }
}
