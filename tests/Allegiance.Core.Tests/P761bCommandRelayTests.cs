using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P761b — the accepted-command relay that makes the team pane's OBJECTIVE
/// column sortable by command TEXT (native <c>CurrentCommandText</c>,
/// <c>WinTrek/teampane.cpp:1528-1534, 1660-1676</c>):
/// <list type="bullet">
/// <item>the verb table is native's <c>c_cdAllCommands[cid].szVerb</c>
/// (<c>Igc/common.cpp:41-52</c>) and the text is "verb targetName" /
/// "verb" (<c>CurrentCommandText</c>);</item>
/// <item>the server forwards a ship's changed <c>c_cmdAccepted</c> slot to the
/// whole side, the port of <c>FedSrvSiteBase::CommandChangedEvent</c>
/// (<c>FedSrv/FedSrv.CPP:13695-13732</c>);</item>
/// <item>the receiving client applies it to whichever ship it names
/// (<c>clintlib/appmsg.cpp:1831-1855</c>).</item>
/// </list>
/// </summary>
public sealed class P761bCommandRelayTests
{
    [Fact]
    public void CommandVerbTable_MatchesNative_c_cdAllCommands()
    {
        // Igc/common.cpp:41-52 — goto, attack, capture, defend, pickup, goto,
        // repair, join, mine, build.
        Assert.Equal("goto", OrderChange.AcceptedCommandText(OrderChange.CidDefault, null));
        Assert.Equal("attack", OrderChange.AcceptedCommandText(OrderChange.CidAttack, null));
        Assert.Equal("capture", OrderChange.AcceptedCommandText(OrderChange.CidCapture, null));
        Assert.Equal("defend", OrderChange.AcceptedCommandText(OrderChange.CidDefend, null));
        Assert.Equal("pickup", OrderChange.AcceptedCommandText(OrderChange.CidPickup, null));
        Assert.Equal("goto", OrderChange.AcceptedCommandText(OrderChange.CidGoto, null));
        Assert.Equal("repair", OrderChange.AcceptedCommandText(OrderChange.CidRepair, null));
        Assert.Equal("join", OrderChange.AcceptedCommandText(OrderChange.CidJoin, null));
        Assert.Equal("mine", OrderChange.AcceptedCommandText(OrderChange.CidMine, null));
        Assert.Equal("build", OrderChange.AcceptedCommandText(OrderChange.CidBuild, null));
    }

    [Fact]
    public void AcceptedCommandText_IsVerbPlusTargetName_OrTheBareVerb()
    {
        // CurrentCommandText (teampane.cpp:1660-1676): verb + " " + GetModelName.
        Assert.Equal("attack Bogey", OrderChange.AcceptedCommandText(OrderChange.CidAttack, "Bogey"));
        // Unset slot / out-of-range id → "" (native's !LegalCommand branch).
        Assert.Equal("", OrderChange.AcceptedCommandText(OrderChange.CidNone, "Bogey"));
        Assert.Equal("", OrderChange.AcceptedCommandText(OrderChange.CidMax, "Bogey"));
        Assert.False(OrderChange.TryCommandVerb(OrderChange.CidNone, out _));
        Assert.False(OrderChange.TryCommandVerb(OrderChange.CidMax, out _));
    }

    /// <summary>P761c — the relay's wire type comes from the target model, not a
    /// hardcoded Ship: a ship, a probe, and a non-Model station target.</summary>
    [Fact]
    public void AcceptedCommandOf_DerivesTypeFromTheModel_AndKeepsTheWireTarget()
    {
        var ship = new Ship(101, default, Vector3.Zero, Vector3.Zero, new Orientation());

        // A ship target is OT_ship.
        var targetShip = new Ship(202, default, Vector3.Zero, Vector3.Zero, new Orientation());
        ship.AcceptedShipCommandId = OrderChange.CidAttack;
        ship.AcceptedShipTarget = targetShip;
        Assert.Equal((OrderChange.CidAttack, (short)IgcObjectType.Ship, (short)202),
            global::Allegiance.Server.Server.AcceptedCommandOf(ship));

        // A probe is a Model but NOT a Ship (EquipmentTarget.TypeOf -> OT_probe).
        var probe = new Probe(303, new Sector(default), default, Vector3.Zero, 0, sideId: 1);
        ship.AcceptedShipCommandId = OrderChange.CidDefend;
        ship.AcceptedShipTarget = probe;
        Assert.Equal((OrderChange.CidDefend, (short)IgcObjectType.Probe, (short)303),
            global::Allegiance.Server.Server.AcceptedCommandOf(ship));

        // No Model for a station: the queued order's original wire triple rides
        // the accepted slot (Server.ShipCommands.cs QueueCommandForPlayer ->
        // promotion), so the target is not lost.
        ship.AcceptedShipCommandId = OrderChange.CidGoto;
        ship.AcceptedShipTarget = null;
        ship.AcceptedShipWireType = IgcObjectType.Station;
        ship.AcceptedShipWireId = 5101;
        Assert.Equal((OrderChange.CidGoto, (short)IgcObjectType.Station, (short)5101),
            global::Allegiance.Server.Server.AcceptedCommandOf(ship));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AcceptedOrder_OnOnePlayer_ReachesTheTeammate_WithItsCommandText()
    {
        Require.Artwork();
        using var server = new global::Allegiance.Server.Server();
        server.Boot(new global::Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            Games = [new global::Allegiance.Server.GameConfig
            {
                Name = "P761b relay", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"],
            }],
        });
        // P180 — this test assumes an immediately-flyable/started mission
        // (bypass the ready-up lobby; ORDER_CHANGE is gated on STAGE_STARTED,
        // FedSrv.CPP:7266).
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var a = new ConnectionSession();
        using var b = new ConnectionSession();
        a.Connect(server.BoundPort, "relayA");
        b.Connect(server.BoundPort, "relayB");
        Logon(a, "relayA");
        Logon(b, "relayB");
        WaitSeated(server, a);
        WaitSeated(server, b);
        short aShip = a.World.MyShipId!.Value;
        short bShip = b.World.MyShipId!.Value;

        // B must know A's ship before it can receive a command about it.
        int n = 0;
        while (!b.World.Ships.ContainsKey(aShip) && n++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(b.World.Ships.ContainsKey(aShip), "B must know A's ship");

        // A accepts an attack order on B's ship: ORDER_CHANGE(93)
        // c_cmdAccepted/c_cidAttack naming B (WinTrek.cpp:10681-10682 sends the
        // same pmodel as SetTarget, FedSrv.CPP:7335-7339).
        a.SendOrderChange(aShip, OrderChange.CidAttack, OrderChange.CmdAccepted,
            IgcObjectType.Ship, bShip);

        // The server's sweep relays A's accepted slot to the side group
        // (FedSrv.CPP:13695-13732) and B's client applies it to A's ship
        // (appmsg.cpp:1831-1855), giving the team pane the text it sorts by.
        n = 0;
        while (b.World.AcceptedCommandText(aShip) == "" && n++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.Equal("attack relayB", b.World.AcceptedCommandText(aShip));
        // P761c — A's own text comes from the client-local MyAcceptedCommand (set
        // by SendOrderChange's local Apply), never from a server echo: native's
        // client ignores ORDER_CHANGE for its own ship (appmsg.cpp:1838), and the
        // own ship entity slot must therefore stay untouched.
        Assert.Equal("attack relayB", a.World.AcceptedCommandText(aShip));
        Assert.Null(a.World.Ships[aShip].AcceptedTargetType);
    }

    /// <summary>P761c — the regression: accepting a QUEUED station goto must keep
    /// the target on the accepting ship's own client (the server must not echo
    /// ORDER_CHANGE back to the commanded ship, appmsg.cpp:1838) while the
    /// teammate receives the real station target, not (cid, -1, -1). This is the
    /// team pane's "goto &lt;station&gt;" text (teampane.cpp:1577-1621, 1660-1676).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate()
    {
        Require.Artwork();
        using var server = new global::Allegiance.Server.Server();
        server.Boot(new global::Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            Games = [new global::Allegiance.Server.GameConfig
            {
                Name = "P761c relay", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"],
            }],
        });
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var a = new ConnectionSession();
        using var b = new ConnectionSession();
        a.Connect(server.BoundPort, "stackA");
        b.Connect(server.BoundPort, "stackB");
        Logon(a, "stackA");
        Logon(b, "stackB");
        WaitSeated(server, a);
        WaitSeated(server, b);
        short aShip = a.World.MyShipId!.Value;
        short bShip = b.World.MyShipId!.Value;
        short aSide = a.World.MySideId!.Value;

        int n = 0;
        while (!b.World.Ships.ContainsKey(aShip) && n++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(b.World.Ships.ContainsKey(aShip), "B must know A's ship");
        // The teammate must have the station in its table for the name to resolve.
        n = 0;
        while (b.World.Stations.Values.All(st => st.SideId != aSide) && n++ < 8000)
        { server.TickSim(); Thread.Sleep(1); }
        var station = b.World.Stations.Values.First(st => st.SideId == aSide);

        var aServerShip = server.Ships.Single(l => l.Ship.Id == aShip).Ship;
        server.QueueCommandForPlayer(aServerShip, OrderChange.CidGoto, null,
            IgcObjectType.Station, station.StationId, senderId: bShip);

        n = 0;
        while (a.World.MyQueuedCommand is not { TargetType: IgcObjectType.Station } && n++ < 8000)
        { server.TickSim(); Thread.Sleep(1); }
        Assert.NotNull(a.World.MyQueuedCommand);

        // Accept exactly as the client key path does: set the local accepted slot,
        // then send the ORDER_CHANGE(c_cmdQueued) promotion.
        QueuedCommandState? accepted;
        lock (a.Gate) accepted = a.World.AcceptQueuedCommand();
        Assert.NotNull(accepted);
        Assert.Equal(IgcObjectType.Station, accepted!.Value.TargetType);
        Assert.Equal(station.StationId, accepted.Value.TargetId);
        a.SendOrderChange(aShip, OrderChange.CidGoto, OrderChange.CmdQueued,
            (IgcObjectType)(-1), -1);

        n = 0;
        while (b.World.AcceptedCommandText(aShip) == "" && n++ < 8000)
        { server.TickSim(); Thread.Sleep(1); }
        Assert.Equal($"goto {station.Name}", b.World.AcceptedCommandText(aShip));
        Assert.Equal((short)IgcObjectType.Station, b.World.Ships[aShip].AcceptedTargetType);
        Assert.Equal(station.StationId, b.World.Ships[aShip].AcceptedTargetId);

        // Give the old echo plenty of ticks to arrive. MyAcceptedCommand must
        // still hold the Station target the client set locally.
        for (int i = 0; i < 40; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.NotNull(a.World.MyAcceptedCommand);
        Assert.Equal(IgcObjectType.Station, a.World.MyAcceptedCommand!.Value.TargetType);
        Assert.Equal(station.StationId, a.World.MyAcceptedCommand.Value.TargetId);
        // The own ship's entity slot is NOT the server echo's (native skips it).
        Assert.Null(a.World.Ships[aShip].AcceptedTargetType);
    }

    private static void Logon(ConnectionSession session, string name)
    {
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = name, ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 446u, DwCookie = uint.MaxValue,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
            SteamID = 90200000000000077ul, DrmHash = "p761brelay",
        });
        // P281 (master merge) — a logged-on peer now waits for a post-connect
        // side choice; POSITIONREQ with NA auto-picks, exactly as the real client
        // does. Without it the ship is never spawned and MyShipId stays null.
        session.SendPositionReq(-1);
    }

    private static void WaitSeated(global::Allegiance.Server.Server server, ConnectionSession session)
    {
        int n = 0;
        while (session.World.MyShipId is null && n++ < 8000) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(session.World.MyShipId is not null, "peer must be seated");
        n = 0;
        while (session.World.Ships[session.World.MyShipId!.Value].Player is null && n++ < 8000)
        { server.TickSim(); Thread.Sleep(1); }
    }
}
