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

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotation_retires_viewer_references_preserves_accounting_and_invalidates_world_sync(bool pipeline)
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        var template = P660NextMatchLiveTests.Config(true);
        var config = new Allegiance.Server.ServerConfig
        {
            Port = 0, LiveSettings = template.LiveSettings, Games = template.Games,
            SideSelectAutoSeatSeconds = 30,   // P763: ConnectionSession peers never send POSITIONREQ
            Broadcast = new() { ImmutableSnapshot = pipeline, EncodeOnce = pipeline, InterestTiers = pipeline },
        };
        server.Boot(config);
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
        var oldTreasures = old.Sectors.SelectMany(s => s.Models.OfType<Treasure>()).ToArray();
        if (pipeline)
        {
            Assert.True(server._broadcastSnapshotEnabled);
            Assert.True(server._broadcastEncodeOnceEnabled);
            Assert.True(server._broadcastInterestTiersEnabled);
            var warmedPeers = (Array)typeof(Srv).GetField("_broadcastPeerBuffer", flags)!.GetValue(server)!;
            Assert.NotEmpty(warmedPeers.Cast<object>());
            Assert.All(warmedPeers.Cast<object>(), entry =>
                Assert.NotNull(entry.GetType().GetProperty("Value")!.GetValue(entry)));
        }
        P660NextMatchLiveTests.End(server);
        // No viewer cache can retain old ships/sectors after reseating, even
        // before the first fresh-world broadcast. Its byte window is retained.
        Assert.Empty(viewers.Keys.Cast<object>());
        Assert.Empty(warnings.Keys.Cast<object>());
        foreach (string name in new[] { "_sharedHeavy", "_sharedLight", "_tierHeavy",
            "_interestSnapshot", "_crossSideExportPackets", "_broadcastMates", "_sideConnections" })
            Assert.Empty(((IDictionary)typeof(Srv).GetField(name, flags)!.GetValue(server)!).Keys.Cast<object>());
        foreach (string name in new[] { "_broadcastMateBuffers", "_broadcastSideSectors", "_sideConnectionBuffers" })
            foreach (IEnumerable values in ((IDictionary)typeof(Srv).GetField(name, flags)!.GetValue(server)!).Values)
                Assert.Empty(values.Cast<object>());
        var peerBuffer = (Array)typeof(Srv).GetField("_broadcastPeerBuffer", flags)!.GetValue(server)!;
        foreach (var entry in peerBuffer)
            Assert.Null(entry!.GetType().GetProperty("Value")!.GetValue(entry));
        Assert.NotSame(clusterInfo, typeof(Srv).GetField("_clusterInfoBytes", flags)!.GetValue(server));
        string accounting = server.TakeUpdateAgeReport(1);
        Assert.DoesNotContain("shipsBytes=0 ", accounting);
        var fresh = server.Missions[0];
        Assert.All(fresh.Sectors.SelectMany(s => s.StationRuntimes), s => Assert.DoesNotContain(s, oldStations));
        Assert.All(fresh.Sectors.SelectMany(s => s.Mineable), r => Assert.DoesNotContain(r, oldRocks));
        Assert.All(fresh.Sectors, s => Assert.Empty(s.Models.OfType<Probe>()));
        // Native file-map generation creates fresh starting treasures (P864c,
        // missionigc.cpp:4048-4051). Verify retirement by identity, as for rocks.
        var newTreasures = fresh.Sectors.SelectMany(s => s.Models.OfType<Treasure>()).ToArray();
        Assert.NotEmpty(newTreasures);
        Assert.All(newTreasures, t => Assert.DoesNotContain(t, oldTreasures));
        Assert.All(server.Ships, l => Assert.Same(fresh, l.Ship.Mission));
    }
}
