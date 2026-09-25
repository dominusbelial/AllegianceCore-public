using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P374 per-sector threat
/// consumption: <see cref="ClientWorld.Apply(ClusterWarningUpdate)"/> stores the
/// current <see cref="ClusterWarnings"/> level (driving the starmap icons),
/// fires the alert sound cue + "<c>{desc} in {sector}</c>" status line on the
/// INCREASE edge, and posts the sector-secured / sector-lost control lines +
/// cues — mirroring native <c>ClusterSiteImpl::UpdateClusterWarnings</c>
/// (trekigc.cpp:327-383).
/// </summary>
public class ClientWorldClusterWarningTests
{
    private static DataClusterIGC MakeCluster(short id) => new(
        StarSeed: 642u, LightDirection: [1f, 2f, 3f], LightColor: 0xFFAAu,
        ScreenX: 617f, ScreenY: 630f, ClusterId: id, Debris: 613, Stars: 614,
        Name: $"sector_{id}", PosterName: "", PlanetName: "",
        PlanetSinLatitude: (byte)0x40, PlanetLongitude: (byte)0x41, PlanetRadius: (byte)0x42,
        Active: false, HomeSector: false);

    private static ClientWorld WorldWithSector(short id)
    {
        var w = new ClientWorld();
        w.Apply(new ExportCluster { ObjectType = ExportCluster.OtCluster, Cluster = MakeCluster(id) });
        return w;
    }

    [Fact]
    public void Applies_StoresLevelPerSector_DefaultsNoThreat()
    {
        var w = WorldWithSector(3);
        Assert.Equal(ClusterWarnings.NoThreat, w.ClusterWarningFor(3));

        w.Apply(ClusterWarningUpdate.From(3, 13));
        Assert.Equal((short)13, w.ClusterWarningFor(3));
        // A sector we never heard about reads as no-threat.
        Assert.Equal(ClusterWarnings.NoThreat, w.ClusterWarningFor(99));
    }

    [Fact]
    public void IncreaseEdge_FiresAlertCue_AndPostsDescInSectorLine()
    {
        var w = WorldWithSector(3);
        w.Apply(ClusterWarningUpdate.From(3, 5));   // Miner at risk (>= 5)

        var cue = Assert.Single(w.ClusterWarningCues);
        Assert.Equal((short)3, cue.SectorId);
        Assert.Equal((short)5, cue.Warning);
        Assert.False(cue.Secured);
        Assert.False(cue.Lost);

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("Miner at risk in sector_3", line.Message);
        Assert.Equal(-1, line.SidSender);   // bare system line, no player prefix
    }

    [Fact]
    public void SteadyOrDecreasingLevel_ProducesNothingBeyondFirstIncrease()
    {
        var w = WorldWithSector(3);
        w.Apply(ClusterWarningUpdate.From(3, 13));   // first increase: fires a cue + line
        w.TakeClusterWarningCues();                  // drain the legitimate first cue
        w.Apply(ClusterWarningUpdate.From(3, 13));   // steady — nothing new
        w.Apply(ClusterWarningUpdate.From(3, 0));    // decrease — nothing new (icon clears)

        Assert.Empty(w.ClusterWarningCues);
        var line = Assert.Single(w.Events.Chats);    // only the first increase's line
        Assert.Equal("Station at risk in sector_3", line.Message);
        Assert.Equal(ClusterWarnings.NoThreat, w.ClusterWarningFor(3));   // icon clears
    }

    [Fact]
    public void SecuredTransition_PostsSecuredLine_AndCues()
    {
        var w = WorldWithSector(7);
        w.Apply(ClusterWarningUpdate.From(7, 0, secured: true));

        var cue = Assert.Single(w.ClusterWarningCues);
        Assert.True(cue.Secured);

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("sector_7 secured.", line.Message);
    }

    [Fact]
    public void LostTransition_PostsLostLine_AndCues()
    {
        var w = WorldWithSector(9);
        // A purely-lost sector (no enemy presence → warning stays NoThreat) posts
        // only the lost line; an enemy present in a lost sector would ALSO alert
        // (faithful to native firing both, but this test isolates the lost path).
        w.Apply(ClusterWarningUpdate.From(9, 0, lost: true));

        var cue = Assert.Single(w.ClusterWarningCues);
        Assert.True(cue.Lost);

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("sector_9 lost.", line.Message);
    }

    [Fact]
    public void TakeClusterWarningCues_Drains()
    {
        var w = WorldWithSector(3);
        w.Apply(ClusterWarningUpdate.From(3, 13));

        Assert.Single(w.TakeClusterWarningCues());
        Assert.Empty(w.ClusterWarningCues);   // drained
    }

    [Fact]
    public void Reset_ClearsWarnings_AndPendingCues()
    {
        var w = WorldWithSector(3);
        w.Apply(ClusterWarningUpdate.From(3, 13));
        w.Reset();

        Assert.Equal(ClusterWarnings.NoThreat, w.ClusterWarningFor(3));
        Assert.Empty(w.ClusterWarningCues);
    }
}
