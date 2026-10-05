using System.Text.Json;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P540c — the spectator frame (<see cref="SpectatorFrame.BuildSectorFrame"/>)
/// must report each warp's destination as the destination SECTOR, resolved from
/// the warp's PARTNER (native <c>warpIGC.h:129-141</c>), never the raw warp id.
/// The old code fed <c>w.DestinationId</c> straight through, misreading the
/// partner-warp id as a sector id (a phantom sector).
/// </summary>
public class SpectatorFrameWarpDestResolutionTests
{
    private static ExportWarp Export(short id, short dest, short cluster) =>
        new()
        {
            Warp = new DataWarpIGC(
                WarpId: id, DestinationId: dest, Radius: 50,
                TextureName: "", IconName: "", Name: "W" + id,
                Position: [10f, 20f, 30f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], Signature: 1f, ClusterId: cluster),
        };

    [Fact]
    public void BuildSectorFrame_ResolvesDestSector_FromPartnersCluster()
    {
        var world = new ClientWorld();
        world.Apply(Export(id: 10, dest: 20, cluster: 100));   // warp 10 in sector 100 -> partner 20
        world.Apply(Export(id: 20, dest: 10, cluster: 200));   // partner warp 20 in sector 200

        var json = SpectatorFrame.BuildSectorFrame(world, 100, id => "S" + id)!;
        using var doc = JsonDocument.Parse(json);
        var warps = doc.RootElement.GetProperty("warps");
        Assert.Equal(1, warps.GetArrayLength());
        // Destination sector is the PARTNER's cluster (200), NOT the warp id (20).
        Assert.Equal((short)200, warps[0].GetProperty("destSector").GetInt16());
        Assert.Equal((short)10, warps[0].GetProperty("id").GetInt16());
    }

    [Fact]
    public void BuildSectorFrame_SkipsWarpWhosePartnerIsUnknown_NeverGuesses()
    {
        var world = new ClientWorld();
        world.Apply(Export(id: 10, dest: 991, cluster: 100));   // partner 991 not in the world

        // The only member of sector 100 is a warp whose partner is unknown: it is
        // SKIPPED (never guessed as sector 991), so the sector has no geometry to
        // announce and BuildSectorFrame returns null.
        var json = SpectatorFrame.BuildSectorFrame(world, 100, id => "S" + id);
        Assert.Null(json);
    }
}
