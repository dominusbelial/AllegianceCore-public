using Allegiance.ClientCore.Session;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P655PositionRefusalTests
{
    [Fact]
    public void Session_tracks_only_own_refusal_and_clears_it_before_retry()
    {
        using var session = new ConnectionSession();
        void Feed(IFedMessage message) => ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));
        Feed(new LogonAck { FValidated = true, ShipID = 42 });
        Feed(new DelPositionReq { ShipId = 43, SideId = 0, Reason = DelPositionReqReason.TeamFull });
        Assert.Null(session.LastPositionRefusal);
        Feed(new DelPositionReq { ShipId = 42, SideId = 0, Reason = DelPositionReqReason.TeamBalance });
        Assert.Equal(DelPositionReqReason.TeamBalance, session.LastPositionRefusal!.Reason);
        session.SendPositionReq(-1);
        Assert.Null(session.LastPositionRefusal);
    }
}
