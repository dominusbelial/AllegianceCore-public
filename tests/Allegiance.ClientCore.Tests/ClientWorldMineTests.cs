using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P291 — pure-domain tests (no sockets, no server) for the mine surface the
/// MonoGame client reads: the EXPORT(26)/OT_mine table (<c>ClientWorld.Mines</c>)
/// and its removal by MINE_DESTROYED(84). Messages are constructed directly
/// (exactly like the other <c>ClientWorld*Tests</c>) and fed through
/// <see cref="ClientWorld.Apply"/> — mirroring the P260 probe plumbing tests.
/// A mine must land in the world for P291's renderer to see it, and must leave
/// when the server says it detonated/expired (so its decal field disappears).
/// </summary>
public class ClientWorldMineTests
{
    private static ExportMine Export(short id, short side, short cluster, short launcher,
        short minetypeId, Vector3 pos) =>
        new()
        {
            Mine = new DataMineExport(
                P0: pos, Time0: 12u, MineId: id, ExportF: true, ClusterId: cluster,
                MinetypeId: minetypeId, LauncherId: launcher, SideId: side,
                Fraction: 240, CreateNow: true),
        };

    [Fact]
    public void ExportMine_UpsertsIntoTheMinesTable()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, side: 2, cluster: 3, launcher: 5, minetypeId: 911, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(8, side: 2, cluster: 3, launcher: 6, minetypeId: 921, new Vector3(4f, 5f, 6f)));

        Assert.Equal(2, world.Mines.Count);
        Assert.True(world.Mines.TryGetValue(7, out var m7));
        Assert.Equal((short)2, m7.SideId);
        Assert.Equal((short)5, m7.LauncherShipId);
        Assert.Equal((short)3, m7.ClusterId);
        Assert.Equal((short)911, m7.MinetypeId);
        Assert.Equal(new Vector3(1f, 2f, 3f), m7.Position);
    }

    [Fact]
    public void ExportMine_IsUpsertByKey_LastWriteWins()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, side: 1, cluster: 3, launcher: 5, minetypeId: 910, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(7, side: 9, cluster: 4, launcher: 5, minetypeId: 940, new Vector3(9f, 9f, 9f)));

        Assert.Single(world.Mines);
        Assert.Equal((short)9, world.Mines[7].SideId);
        Assert.Equal((short)4, world.Mines[7].ClusterId);
        Assert.Equal((short)940, world.Mines[7].MinetypeId);
    }

    [Fact]
    public void MineDestroyed_RemovesTheMineFromTheTable()
    {
        var world = new ClientWorld();
        world.Apply(Export(7, side: 2, cluster: 3, launcher: 5, minetypeId: 930, new Vector3(1f, 2f, 3f)));

        world.Apply(new MineDestroyed { MineId = 7, ClusterId = 3 });

        Assert.Empty(world.Mines);
        Assert.DoesNotContain(world.Mines.Keys, k => k == 7);
    }

    [Fact]
    public void MineDestroyed_ForAnUntrackedMineIsAHarmlessNoOp()
    {
        var world = new ClientWorld();
        // Destroying a mine we never saw must not throw or corrupt the table.
        world.Apply(new MineDestroyed { MineId = 987, ClusterId = 3 });
        Assert.Empty(world.Mines);
    }

    [Fact]
    public void ExportMine_ArrivingViaTheDispatchSwitch_StillPopulatesTheTable()
    {
        // The wire path feeds the DECODED message into Apply(IFedMessage), whose
        // switch must route ExportMine/MineDestroyed to the per-type handlers
        // (the P264 lesson: a case-less per-type Apply is dead code). Boxing to
        // IFedMessage exercises exactly that dispatch, not the specific overload.
        var world = new ClientWorld();

        world.Apply((IFedMessage)Export(7, side: 2, cluster: 3, launcher: 5, minetypeId: 933, new Vector3(1f, 2f, 3f)));
        Assert.Single(world.Mines);
        Assert.Equal((short)933, world.Mines[7].MinetypeId);

        world.Apply((IFedMessage)new MineDestroyed { MineId = 7, ClusterId = 3 });
        Assert.Empty(world.Mines);
    }
}
