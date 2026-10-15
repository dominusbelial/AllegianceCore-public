using System.Collections;
using System.Reflection;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P682MissionStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_mission_discards_research_attributes_and_bucket_funding(bool reconnect)
    {
        var world = new ClientWorld();
        var gas = Enumerable.Repeat(2f, GlobalAttributeSet.Count).ToArray();
        world.Apply(new SideAttributeChange { SideId = 0, GasAttributes = gas });
        world.Apply(new BucketStatus { SideId = 0, IBucket = 3, MoneyTotal = 100, TimeTotal = 200 });
        Assert.Equal(2f, world.GlobalAttributesFor(0).Get(GlobalAttributeSet.ScanRange));
        Assert.NotNull(world.BucketFundingOf(0, 3));
        if (reconnect) world.Reset(); else world.BeginNextMission();
        Assert.Empty(world.GlobalAttributesBySideId);
        Assert.Empty(world.BucketFundingFor(0));
        Assert.Equal(1f, world.GlobalAttributesFor(0).Get(GlobalAttributeSet.ScanRange));
    }

    [SkippableFact]
    public void Rotation_retires_viewer_references_preserves_accounting_and_invalidates_world_sync()
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(true));
        using var a = P660NextMatchLiveTests.Connect(server, "P682-cache-A", 68201);
        using var b = P660NextMatchLiveTests.Connect(server, "P682-cache-B", 68202);
        var old = server.Missions[0]; old.Stage = MissionStage.Started;
        for (int i = 0; i < 30; i++) server.TickSim();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var viewers = (IDictionary)typeof(Srv).GetField("_viewerPoseCadence", flags)!.GetValue(server)!;
        var warnings = (IDictionary)typeof(Srv).GetField("_lastSectorAssetMaskBySide", flags)!.GetValue(server)!;
        Assert.Equal(2, viewers.Count);
        Assert.NotEmpty(warnings.Keys.Cast<object>());
        var clusterInfo = typeof(Srv).GetField("_clusterInfoBytes", flags)!.GetValue(server);
        var oldStations = old.Sectors.SelectMany(s => s.StationRuntimes).ToArray();
        var oldRocks = old.Sectors.SelectMany(s => s.Mineable).ToArray();
        P660NextMatchLiveTests.End(server);
        // No viewer cache can retain old ships/sectors after reseating, even
        // before the first fresh-world broadcast. Its byte window is retained.
        Assert.Empty(viewers.Keys.Cast<object>());
        Assert.Empty(warnings.Keys.Cast<object>());
        Assert.NotSame(clusterInfo, typeof(Srv).GetField("_clusterInfoBytes", flags)!.GetValue(server));
        string accounting = server.TakeUpdateAgeReport(1);
        Assert.DoesNotContain("shipsBytes=0 ", accounting);
        var fresh = server.Missions[0];
        Assert.All(fresh.Sectors.SelectMany(s => s.StationRuntimes), s => Assert.DoesNotContain(s, oldStations));
        Assert.All(fresh.Sectors.SelectMany(s => s.Mineable), r => Assert.DoesNotContain(r, oldRocks));
        Assert.All(fresh.Sectors, s => Assert.Empty(s.Models.OfType<Probe>()));
        Assert.All(fresh.Sectors, s => Assert.Empty(s.Models.OfType<Treasure>()));
        Assert.All(server.Ships, l => Assert.Same(fresh, l.Ship.Mission));
    }
}
