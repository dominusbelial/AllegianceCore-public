using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for P366 — the leader-only bot
/// dock/launch command. They cover two layers of the gate:
/// <list type="bullet">
/// <item><see cref="ClientWorld.IsTeamLeader"/> — derived from the whole-mission
/// <c>SET_TEAM_LEADER(121)</c> broadcast + the client's own ship/side.</item>
/// <item><see cref="LeaderBotCommandResolver"/> — the pure decision that a
/// NON-leader sees no command, and that a leader only gets one for a same-side
/// bot (not a real peer, not a cross-side ship, not self/a corpse).</item>
/// </list>
/// Messages are built directly and fed through <see cref="ClientWorld.Apply"/>,
/// the same shape as the other <c>ClientWorld*Tests</c>.
/// </summary>
public class LeaderBotCommandTests
{
    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>A world where the local player owns <paramref name="myShipId"/>
    /// on <paramref name="mySide"/> and is that side's elected team leader.</summary>
    private static ClientWorld LeaderWorld(short myShipId, short mySide)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        world.Apply(new ExportShip { ShipId = -1, SideId = mySide });   // own-ship EXPORT sets MySideId
        world.Apply(new SetTeamLeader { SideId = mySide, ShipId = myShipId });
        return world;
    }

    /// <summary>Register a flying same-side bot in the world (no PlayerInfo →
    /// a bot per the P366 discriminator), with a real pose so it is targetable.</summary>
    private static void AddFlyingBot(ClientWorld world, short botId, short side, string name = "drone")
    {
        world.Apply(new ExportShip { ShipId = botId, SideId = side, CharacterName = name });
        world.Apply(new SingleShipUpdate { OidTarget = botId, Ship = ServerShipUpdate.FromModel(botId, new Ship(botId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u) });
        world.Apply(new ShipStatusMessage { ShipId = botId, StationId = -1, SectorId = 1 });
    }

    private const short Me = 10;
    private const short Side = 5;
    private const short Bot = 20;

    // ── IsTeamLeader derivation ────────────────────────────────────────────

    [Fact]
    public void IsTeamLeader_DefaultsFalse_WithoutLeadershipAnnouncement()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(Me, new Ship(Me, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u), Cookie = 1 });
        world.Apply(new ExportShip { ShipId = -1, SideId = Side });
        Assert.False(world.IsTeamLeader);   // no SET_TEAM_LEADER yet
    }

    [Fact]
    public void IsTeamLeader_True_WhenOurShipIsAnnouncedLeader()
    {
        var world = LeaderWorld(Me, Side);
        Assert.True(world.IsTeamLeader);
        Assert.Equal((short)Me, world.TeamLeaderShipIdFor(Side));
    }

    [Fact]
    public void IsTeamLeader_False_AfterLeadershipTransfersAway()
    {
        var world = LeaderWorld(Me, Side);
        world.Apply(new SetTeamLeader { SideId = Side, ShipId = 999 });   // someone else leads now
        Assert.False(world.IsTeamLeader);
        Assert.Equal((short)999, world.TeamLeaderShipIdFor(Side));
    }

    [Fact]
    public void IsTeamLeader_False_WhenNotLeader_OfOurOwnSide()
    {
        // We are leader of side 5, but our own ship is on side 6.
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(Me, new Ship(Me, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u), Cookie = 1 });
        world.Apply(new ExportShip { ShipId = -1, SideId = 6 });          // MySideId = 6
        world.Apply(new SetTeamLeader { SideId = 5, ShipId = Me });        // leader of a DIFFERENT side
        Assert.False(world.IsTeamLeader);
    }

    [Fact]
    public void Reset_ClearsLeadershipState()
    {
        var world = LeaderWorld(Me, Side);
        world.Reset();
        Assert.False(world.IsTeamLeader);
    }

    // ── LeaderBotCommandResolver gates ─────────────────────────────────────

    [Fact]
    public void NonLeader_SeesNoCommand_EvenWithSameSideBotSelected()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(Me, new Ship(Me, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u), Cookie = 1 });
        world.Apply(new ExportShip { ShipId = -1, SideId = Side });        // MySideId known, but NO leader announcement
        AddFlyingBot(world, Bot, Side);

        Assert.Null(LeaderBotCommandResolver.Resolve(world, Bot));
    }

    [Fact]
    public void Leader_CommandsFlyingBot_OffersDock()
    {
        var world = LeaderWorld(Me, Side);
        AddFlyingBot(world, Bot, Side);

        var cmd = LeaderBotCommandResolver.Resolve(world, Bot);
        Assert.NotNull(cmd);
        Assert.Equal(Bot, cmd.Value.TargetShipId);
        Assert.False(cmd.Value.TargetDocked);
        Assert.StartsWith("DOCK", cmd.Value.Label);
    }

    [Fact]
    public void Leader_CommandsDockedBot_OffersLaunch()
    {
        var world = LeaderWorld(Me, Side);
        world.Apply(new ExportShip { ShipId = Bot, SideId = Side, CharacterName = "drone" });
        world.Apply(new SingleShipUpdate { OidTarget = Bot, Ship = ServerShipUpdate.FromModel(Bot, new Ship(Bot, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity), 1u) });
        world.Apply(new ShipStatusMessage { ShipId = Bot, StationId = 77, SectorId = 1 });   // docked

        var cmd = LeaderBotCommandResolver.Resolve(world, Bot);
        Assert.NotNull(cmd);
        Assert.Equal(Bot, cmd.Value.TargetShipId);
        Assert.True(cmd.Value.TargetDocked);
        Assert.StartsWith("LAUNCH", cmd.Value.Label);
    }

    [Fact]
    public void Leader_CannotCommandARealPeer_OnTheirOwnSide()
    {
        var world = LeaderWorld(Me, Side);
        AddFlyingBot(world, Bot, Side);
        // Now a real peer's PLAYERINFO lands for that same ship -> no longer a bot.
        // HUD-20 — native marks a human with dtidDrone == NA (FedSrv.CPP:2088-2091);
        // a drone carries its type id.
        world.Apply(new PlayerInfo { ShipId = Bot, ISide = Side, CharacterName = "human",
            PilotType = PlayerInfo.PilotTypePlayer, DtidDrone = PlayerInfo.NA });

        Assert.Null(LeaderBotCommandResolver.Resolve(world, Bot));
    }

    [Fact]
    public void Leader_CannotCommandCrossSideBot()
    {
        var world = LeaderWorld(Me, Side);
        AddFlyingBot(world, Bot, side: (short)(Side + 1));   // enemy-side bot

        Assert.Null(LeaderBotCommandResolver.Resolve(world, Bot));
    }

    [Fact]
    public void Leader_NoCommand_WithoutSelectionOrUnknownShip()
    {
        var world = LeaderWorld(Me, Side);
        AddFlyingBot(world, Bot, Side);

        Assert.Null(LeaderBotCommandResolver.Resolve(world, null));          // nothing selected
        Assert.Null(LeaderBotCommandResolver.Resolve(world, (short)999));    // unknown ship id
    }
}
