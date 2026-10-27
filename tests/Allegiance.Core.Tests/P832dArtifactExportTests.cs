using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

public sealed partial class ProsperityArtifactLiveTests
{
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void P832d_New_and_dropped_artifacts_are_exported_only_after_the_peer_side_sees_them(bool dropped)
    {
        Require.Artwork();
        using var h = SeatArtifactCarrier();
        var mission = h.Server.Missions[0];
        var sector = h.Carrier.Sector!;
        // Keep the test object outside the simulation: exercise the actual wire
        // export without an unrelated scanner sweep making it visible first.
        var data = new DataTreasureIGC([20000, 0, 0], [0, 0, 0], 9999, 0, 31000,
            Side.ArtifactSentinelFlag, sector.Id, 0, TreasureCode.Flag, false);
        var artifact = new Treasure(data.ObjectId, sector, data, h.Server.SimNow, mission);
        var method = typeof(Srv).GetMethod(dropped ? "SendTreasureSpawnSectorWide" : "BroadcastGeneratedTreasure",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Export() => method.Invoke(h.Server, dropped ? [sector, artifact] : [artifact]);
        Assert.False(artifact.SeenBySide(h.OwnSide));
        Export();
        // A following known object is a reliable-stream barrier for the first
        // export, so the negative assertion does not depend on an arbitrary sleep.
        var barrier = new Treasure(31001, sector, data with { ObjectId = 31001 }, h.Server.SimNow, mission);
        barrier.SideVisibility.RevealToSide(h.OwnSide);
        method.Invoke(h.Server, dropped ? [sector, barrier] : [barrier]);
        Assert.True(PumpUntil(h.Server, () => h.Bot.TreasureSpawns.Any(t => t.Data.ObjectId == 31001), 3000) < 3000);
        Assert.DoesNotContain(h.Bot.TreasureSpawns, t => t.Data.ObjectId == artifact.Id);
        artifact.SideVisibility.RevealToSide(h.OwnSide);
        Export();
        Assert.True(PumpUntil(h.Server, () => h.Bot.TreasureSpawns.Any(t => t.Data.ObjectId == artifact.Id), 3000) < 3000);
        Assert.Single(h.Bot.TreasureSpawns, t => t.Data.ObjectId == artifact.Id);
    }
}
