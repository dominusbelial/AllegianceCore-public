using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public class P675CommandLiveTests
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Real_session_loads_core_accepts_order_and_joins_wing_without_replacing_accepted_target()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship? own = null; Mission? mission = null; Sector? sector = null;
        server.ShipSpawner = (core, m, id) =>
        {
            mission = m; sector = m.Sectors[0];
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            own = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = m.Sides[0].Id };
            m.PlaceShip(own, sector); return (own, sector);
        };
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            SideSelectAutoSeatSeconds = 30,   // P763: this ConnectionSession peer never sends POSITIONREQ
            Games = [new Allegiance.Server.GameConfig { Name = "P675", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p675");
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p675", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1, SteamAuthTicket = new byte[16],
        });
        bool Pump(Func<bool> done)
        {
            for (int i = 0; i < 4000; i++)
            {
                lock (session.Gate) if (done()) return true;
                server.TickSim(); Thread.Sleep(1);
            }
            return false;
        }
        Assert.True(Pump(() => session.World.MyShipId is { } id && session.World.Ships[id].Latest is not null));
        Assert.NotNull(session.World.MissionCore);
        Assert.Equal(own!.Id, session.World.MyShipId);
        Assert.DoesNotContain(JoinedMission.Fmid, session.UnknownFmids);
        var first = server.SpawnNpcShip(mission!, sector!, own.SideId!.Value, new Vector3(100, 0, 0), null)!;
        var leader = server.SpawnNpcShip(mission!, sector!, own.SideId.Value, new Vector3(200, 0, 0), null)!;
        leader.WingId = 4;
        Assert.True(Pump(() => session.World.Ships.ContainsKey(first.Id) && session.World.Ships.ContainsKey(leader.Id)));
        session.World.Apply(new SetWingId { ShipID = leader.Id, WingId = 4 });
        server.QueueCommandForPlayer(own, OrderChange.CidGoto, first);
        Assert.True(Pump(() => session.World.MyQueuedCommand?.TargetId == first.Id));
        lock (session.Gate) Assert.NotNull(session.World.AcceptQueuedCommand());
        session.SendOrderChange(own.Id, OrderChange.CidGoto, OrderChange.CmdQueued, (IgcObjectType)(-1), -1);
        Assert.True(Pump(() => own.AcceptedShipTarget == first && own.QueuedCommandId == OrderChange.CidNone));
        Assert.Equal(first.Id, session.World.MyAcceptedCommand!.Value.TargetId);
        server.QueueCommandForPlayer(own, OrderChange.CidJoin, leader);
        Assert.True(Pump(() => session.World.MyQueuedCommand?.TargetId == leader.Id));
        lock (session.Gate) Assert.NotNull(session.World.AcceptQueuedCommand());
        session.SendWingId(own.Id, 4);
        session.SendOrderChange(own.Id, OrderChange.CidJoin, OrderChange.CmdQueued, (IgcObjectType)(-1), -1);
        Assert.True(Pump(() => own.WingId == 4 && own.QueuedCommandId == OrderChange.CidNone));
        Assert.Same(first, own.AcceptedShipTarget);
        Assert.Equal(first.Id, session.World.MyAcceptedCommand!.Value.TargetId);
        Assert.False(session.Lost);
    }
}
