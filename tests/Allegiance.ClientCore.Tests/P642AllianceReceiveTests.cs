using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public class P642AllianceReceiveTests
{
    private static void Feed(ConnectionSession session, IFedMessage message) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));
    [Fact]
    public void Production_dispatch_updates_all_groups_and_changes_remove_old_allies()
    {
        using var session = new ConnectionSession();
        Feed(session, new ChangeAlliances { Allies = [0, 0, 1, 1, -1, -1] });
        Assert.DoesNotContain(ChangeAlliances.Fmid, session.UnknownFmids);
        Assert.True(session.World.AreAllied(0, 1));
        Assert.True(session.World.AreAllied(2, 3));
        Assert.False(session.World.AreAllied(0, 2));
        Assert.False(session.World.AreAllied(4, 5));
        Assert.False(session.World.AreAllied(0, 0));
        Assert.False(session.World.AreAllied(-1, 1));
        Assert.False(session.World.AreAllied(0, 6));
        Feed(session, new ChangeAlliances { Allies = [-1, -1, -1, -1, -1, -1] });
        Assert.False(session.World.AreAllied(0, 1));
        Assert.False(session.World.HasAllies(0));
    }
    [Fact]
    public void Late_join_snapshot_preserves_existing_groups_and_unassigned_sides()
    {
        var civilization = default(Allegiance.Core.DataCivilizationIGC) with
        { TtbmBaseTechs = new byte[50], GasBaseAttributes = Enumerable.Repeat(1f, 25).ToArray() };
        var sides = new Allegiance.Core.Model.Side[]
        {
            new(0, "A", civilization) { AllyGroup = 0 }, new(1, "B", civilization) { AllyGroup = 0 }, new(3, "C", civilization),
        };
        var snapshot = Allegiance.Server.Server.CreateAllianceSnapshot(sides);
        Assert.Equal(new sbyte[] { 0, 0, -1, -1, -1, -1 }, snapshot.Allies);
        using var session = new ConnectionSession();
        Feed(session, snapshot);
        Assert.True(session.World.AreAllied(0, 1));
        Assert.False(session.World.AreAllied(2, 3));
    }
    [Fact]
    public void Mission_rotation_clears_old_alliance_groups()
    {
        using var session = new ConnectionSession();
        Feed(session, new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Feed(session, new MissionStageMessage { Stage = MissionStageMessage.StageOver });
        Assert.False(session.World.AreAllied(0, 1));
    }
}
