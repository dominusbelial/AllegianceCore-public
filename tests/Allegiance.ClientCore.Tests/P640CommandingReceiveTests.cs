using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public class P640CommandingReceiveTests
{
    public static ExportCluster Cluster(short id) => new() { Cluster = new DataClusterIGC(
        1, [0, 0, 1], 0xffffffff, 0, 0, id, 0, 0, $"sector {id}", "", "", 0, 0, 0, true, false) };
    public static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0, default, 123,
        new Vector3(123, 456, 789), Orientation.Identity, new Vector3(10, 0, 0), [0, 0, 0], .7f, .8f, .5f, .6f, .4f, 0, 1);
    private static void Seat(ClientWorld world, short own = 2)
    {
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(own), Cookie = 7 });
        foreach (short id in new short[] { 1, 2, 3 })
        {
            world.Apply(new ExportShip { ShipId = id, SideId = 0, CharacterName = $"pilot {id}", HullId = id == 1 ? (short)792 : (short)504, Parts = id == 1 ? [new PartData(55, 100, 0)] : [] });
            world.Apply(new ShipStatusMessage { ShipId = id, HullId = id == 1 ? (short)792 : (short)504,
                SectorId = 1, StationId = -1, ParentId = id == 1 ? (short)-1 : (short)1,
                State = id == 1 ? ShipStatusMessage.StateFlying : ShipStatusMessage.StateTurret });
            world.Apply(new SingleShipUpdate { Ship = Pose(id), OidTarget = id });
        }
    }
    private static void Feed(ConnectionSession session, IFedMessage message) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));

    [Theory]
    [InlineData(1, "You have been demoted to a turret gunner")]
    [InlineData(2, "You have been promoted to pilot")]
    [InlineData(3, null)]
    public void Promote_dispatch_swaps_hull_seats_children_and_preserves_identity(short own, string? notice)
    {
        using var session = new ConnectionSession();
        Seat(session.World, own);
        var world = session.World;
        var oldPose = world.Ships[1].Latest!;
        var oldLoadout = world.Ships[1].Loadout;
        Feed(session, new Promote { ShipidPromoted = 2 });
        Assert.DoesNotContain(Promote.Fmid, session.UnknownFmids);
        Assert.Equal(own, world.MyShipId);
        Assert.Equal(-1, world.Ships[2].ParentId);
        Assert.Equal(2, world.Ships[1].ParentId);
        Assert.Equal(2, world.Ships[3].ParentId);
        Assert.Equal(792, world.Ships[2].HullId);
        Assert.Same(oldLoadout, world.Ships[2].Loadout);
        Assert.Null(world.Ships[1].Loadout);
        Assert.Equal(ShipStatusMessage.StateFlying, world.Ships[2].State);
        Assert.Equal(ShipStatusMessage.StateTurret, world.Ships[1].State);
        Assert.Equal("pilot 2", world.Ships[2].CharacterName);
        Assert.Equal(oldPose.Position, world.Ships[2].Latest!.Position);
        Assert.Equal(oldPose.Velocity, world.Ships[2].Latest!.Velocity);
        Assert.Equal(oldPose.HullFraction, world.Ships[2].Latest!.HullFraction);
        Assert.Equal(2, world.Ships[2].Latest!.ShipId);
        Assert.Equal(own == 2, world.Ships[2].HasPendingReset);
        if (notice is not null) Assert.Contains(world.Events.Chats, c => c.Message == notice);
        else Assert.Empty(world.Events.Chats);
        int lines = world.Events.Chats.Count;
        Feed(session, new Promote { ShipidPromoted = 2 }); // repeated ack, already promoted
        Feed(session, new Promote { ShipidPromoted = 99 }); // missing object
        Assert.Equal(lines, world.Events.Chats.Count);
        Assert.Equal(-1, world.Ships[2].ParentId);
    }

    [Fact]
    public void Promotion_transfers_mounted_fractions_and_cargo_and_preserves_own_ship_cookie()
    {
        var world = new ClientWorld();
        Seat(world, own: 1);
        world.Apply(new BuyLoadoutAck { HullId = 792, Parts = [new ExpandedPartData(55, 10, 0, 100), new ExpandedPartData(56, 11, 255, 200)] });
        var loadout = world.Ships[1].Loadout;
        var cargo = world.Ships[1].Cargo;
        world.Apply(new Promote { ShipidPromoted = 2 });
        Assert.Same(loadout, world.Ships[2].Loadout);
        Assert.Same(cargo, world.Ships[2].Cargo);
        Assert.Equal(100, world.Ships[2].Loadout![0].FractionMounted);
        Assert.Equal(56, world.Ships[2].Cargo![0].PartId);
        Assert.Null(world.Ships[1].Cargo);
        Assert.Equal(7, world.Ships[1].Cookie);
    }

    [Fact]
    public void Highlight_dispatch_is_per_sector_clearable_and_resets_with_session()
    {
        using var session = new ConnectionSession();
        session.World.Apply(Cluster(1)); session.World.Apply(Cluster(2));
        Feed(session, new HighlightCluster { ClusterId = 1, Highlight = true });
        Feed(session, new HighlightCluster { ClusterId = 2, Highlight = true });
        Assert.Equal(2, session.World.HighlightedClusters.Count);
        Assert.Equal(2, session.World.TakeSoundEvents().Count(e => e.Kind == ClientSoundEventKind.SectorHighlighted));
        Feed(session, new HighlightCluster { ClusterId = 1, Highlight = false });
        Assert.DoesNotContain((short)1, session.World.HighlightedClusters);
        Assert.Contains((short)2, session.World.HighlightedClusters);
        Assert.Empty(session.World.TakeSoundEvents());
        Feed(session, new HighlightCluster { ClusterId = 99, Highlight = true });
        Assert.DoesNotContain((short)99, session.World.HighlightedClusters);
        Assert.DoesNotContain(HighlightCluster.Fmid, session.UnknownFmids);
        session.World.Reset();
        Assert.Empty(session.World.HighlightedClusters);
    }

    [Fact]
    public void View_reply_changes_preview_without_reseating_the_pilot()
    {
        using var session = new ConnectionSession();
        Seat(session.World);
        session.World.Apply(Cluster(9));
        Feed(session, new ViewClusterReply { ClusterId = 9, BUsePosition = true, Position = new Vector3(4, 5, 6) });
        Assert.Equal<short?>(9, session.World.ViewedClusterId);
        Assert.Equal(new Vector3(4, 5, 6), session.World.ViewedClusterPosition);
        Assert.Equal<short?>(2, session.World.MyShipId);
        Assert.Equal(1, session.World.Ships[2].SectorId);
        Assert.Equal(1, session.World.ViewClusterVersion);
        Feed(session, new ViewClusterReply { ClusterId = 9, BUsePosition = false });
        Assert.Null(session.World.ViewedClusterPosition);
        Feed(session, new ViewClusterReply { ClusterId = 99 });
        Assert.Equal<short?>(9, session.World.ViewedClusterId);
        Assert.Equal(2, session.World.ViewClusterVersion);
        Assert.DoesNotContain(ViewClusterReply.Fmid, session.UnknownFmids);
        session.World.Reset();
        Assert.Null(session.World.ViewedClusterId);
        Assert.Null(session.World.ViewedClusterPosition);
    }
}
