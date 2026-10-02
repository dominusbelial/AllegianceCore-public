using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P481 — the game-over Back-to-team-lobby plumbing at the <see cref="ClientWorld"/>
/// level. Two behaviour changes make native's <c>OnButtonBack</c> stage branch
/// work in this port:
/// <list type="bullet">
/// <item><b>GAME_OVER no longer flips the stage to Over.</b> The real stage comes
/// only from <c>MISSION_STAGE</c>: a full game-over sends <c>MISSION_STAGE Over</c>
/// before the P439/P480 rotation, while the P455b side-only game-over deliberately
/// keeps the mission Started — so GAME_OVER alone must not pretend the mission
/// ended, or the Back button would misroute an eliminated player to "leave"
/// instead of the team lobby.</item>
/// <item><b>MISSION_STAGE Over resets the finished game's world</b>
/// (<see cref="ClientWorld.BeginNextMission"/>) so the P480 re-burst re-populates
/// a clean slate and re-assigns a fresh <see cref="ClientWorld.MyShipId"/> — but
/// keeps the game-over panel (<see cref="ClientWorld.GameOverResult"/>) up until
/// the Back action dismisses it (<see cref="ClientWorld.ClearGameOver"/>).</item>
/// </list>
/// </summary>
public class ClientWorldGameOverBackTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    private static void Seat(ClientWorld world, short shipId, short sideId)
    {
        world.Apply(new ExportShip { ShipId = -1, SideId = sideId });       // own dummy EXPORT seats MySideId
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(shipId, MakeShip(shipId, Vector3.Zero), 1u),
        });
    }

    private static GameOver EliminatedGameOver() => new()
    {
        Sides = new SideEndgameInfo[GameOver.MaxSides],
        GameoverMessage = "Your side has been eliminated.",
        SideWinner = GameOver.NA,
        NumSides = 3,
        EjectPods = false,
    };

    /// <summary>⭐ A side-only (P455b) GAME_OVER must NOT flip the mission stage:
    /// the mission is still Started for the other sides, and native's Back branch
    /// needs the stage ≠ Over so the eliminated player is routed to the team
    /// lobby, not "leave". Fails on the pre-P481 code (which forced the stage to
    /// Over on every GAME_OVER).</summary>
    [Fact]
    public void SideOnly_GameOver_does_not_flip_stage()
    {
        var world = new ClientWorld();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Seat(world, shipId: 7, sideId: 2);

        world.Apply(EliminatedGameOver());

        Assert.NotNull(world.GameOverResult);
        // The elimination keeps the mission Started (P455b server sends no
        // MISSION_STAGE Over); the stage must not be Over or Back would leave.
        Assert.Equal(MissionStageMessage.StageStarted, world.Mission!.Stage);
    }

    /// <summary>⭐ A full game-over's MISSION_STAGE Over triggers
    /// <see cref="ClientWorld.BeginNextMission"/>: the finished world's entities
    /// and our stale <see cref="ClientWorld.MyShipId"/> are discarded so the P480
    /// re-burst repopulates and re-assigns a fresh ship — but the game-over panel
    /// (<see cref="ClientWorld.GameOverResult"/>) and the local side are retained
    /// so it can still be titled and dismissed by the Back action.</summary>
    [Fact]
    public void MissionStage_Over_resets_world_for_rotation_but_keeps_gameover_panel()
    {
        var world = new ClientWorld();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Seat(world, shipId: 7, sideId: 0);

        // Full game-over: GAME_OVER retained + a real MISSION_STAGE Over follows.
        world.Apply(new GameOver
        {
            Sides = new SideEndgameInfo[GameOver.MaxSides],
            GameoverMessage = "Alpha won.",
            SideWinner = 0,
            NumSides = 2,
            EjectPods = false,
        });
        Assert.NotNull(world.GameOverResult);

        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });

        // The stage reflects the real Over broadcast…
        Assert.Equal(MissionStageMessage.StageOver, world.Mission!.Stage);
        // …the old world + stale MyShipId are gone so the rotation re-burst can
        // re-assign a fresh ship…
        Assert.Null(world.MyShipId);
        Assert.Empty(world.Ships);
        // …but the game-over panel survives for the Back action, and MySideId
        // stays (the rotation re-seats the player on the same side).
        Assert.NotNull(world.GameOverResult);
        Assert.Equal((short)0, world.MySideId);
    }

    /// <summary><see cref="ClientWorld.BeginNextMission"/> keeps the game-over
    /// panel and local side but clears the running-game world.</summary>
    [Fact]
    public void BeginNextMission_clears_world_but_keeps_gameover_and_side()
    {
        var world = new ClientWorld();
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Seat(world, shipId: 7, sideId: 1);
        world.Apply(EliminatedGameOver());

        world.BeginNextMission();

        Assert.Null(world.MyShipId);
        Assert.Empty(world.Ships);
        Assert.NotNull(world.GameOverResult);
        Assert.Equal((short)1, world.MySideId);
    }

    /// <summary><see cref="ClientWorld.ClearGameOver"/> dismisses the panel (the
    /// Back action's "clear GameOverResult and show the team lobby").</summary>
    [Fact]
    public void ClearGameOver_dismisses_the_panel()
    {
        var world = new ClientWorld();
        world.Apply(EliminatedGameOver());
        Assert.NotNull(world.GameOverResult);

        world.ClearGameOver();
        Assert.Null(world.GameOverResult);
    }
}
