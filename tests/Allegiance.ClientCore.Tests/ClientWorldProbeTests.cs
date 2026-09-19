using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P260 — pure-domain tests (no sockets, no server) for the probe surface the
/// MonoGame client reads: the EXPORT(26)/OT_probe table (<c>ClientWorld.Probes</c>),
/// its removal by PROBE_DESTROYED(85), and the <c>MySideId</c> capture from the
/// own-ship dummy EXPORT. Messages are constructed directly (exactly like the
/// other <c>ClientWorld*Tests</c>) and fed through <see cref="ClientWorld.Apply"/>.
/// These are the sound-plumbing tests behind the P260 audio: a probe must land in
/// the world for the deploy/ambient planner to see it, and it must leave when the
/// server says it is destroyed.
/// </summary>
public class ClientWorldProbeTests
{
    private static ExportProbe Export(short id, short side, short cluster, short launcher,
        Vector3 pos) =>
        new()
        {
            Probe = new DataProbeExport(
                P0: pos, Time0: 12u, ProbeId: id, ExportF: true, ProbetypeId: 901,
                SideId: side, ClusterId: cluster, ShipId: launcher,
                OtTarget: -1, OidTarget: -1, CreateNow: true),
        };

    [Fact]
    public void ExportProbe_UpsertsIntoTheProbesTable()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, side: 2, cluster: 3, launcher: 5, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(8, side: 2, cluster: 3, launcher: 6, new Vector3(4f, 5f, 6f)));

        Assert.Equal(2, world.Probes.Count);
        Assert.True(world.Probes.TryGetValue(7, out var p7));
        Assert.Equal((short)2, p7.SideId);
        Assert.Equal((short)5, p7.LauncherShipId);
        Assert.Equal((short)3, p7.ClusterId);
        Assert.Equal(new Vector3(1f, 2f, 3f), p7.Position);
    }

    [Fact]
    public void ExportProbe_IsUpsertByKey_LastWriteWins()
    {
        var world = new ClientWorld();

        world.Apply(Export(7, side: 1, cluster: 3, launcher: 5, new Vector3(1f, 2f, 3f)));
        world.Apply(Export(7, side: 9, cluster: 4, launcher: 5, new Vector3(9f, 9f, 9f)));

        Assert.Single(world.Probes);
        Assert.Equal((short)9, world.Probes[7].SideId);
        Assert.Equal((short)4, world.Probes[7].ClusterId);
    }

    [Fact]
    public void ProbeDestroyed_RemovesTheProbeFromTheTable()
    {
        var world = new ClientWorld();
        world.Apply(Export(7, side: 2, cluster: 3, launcher: 5, new Vector3(1f, 2f, 3f)));

        world.Apply(new ProbeDestroyed { ProbeId = 7, ClusterId = 3 });

        Assert.Empty(world.Probes);
        Assert.DoesNotContain(world.Probes.Keys, k => k == 7);
    }

    [Fact]
    public void OwnShipDummyExport_CapturesMySideId()
    {
        var world = new ClientWorld();
        Assert.Null(world.MySideId);

        // The server's own-ship EXPORT carries ShipId = NA(-1) with the real
        // SideId (Server.cs:~1781); the client captures the side, discards the
        // phantom ship entry (no Ships[-1]).
        world.Apply(new ExportShip
        {
            ShipId = -1,
            SideId = 711,
            HullId = 912,
            CharacterName = "me",
        });

        Assert.Equal((short)711, world.MySideId);
        Assert.False(world.Ships.ContainsKey(-1), "the phantom dummy ship must not enter the table");
    }
}
