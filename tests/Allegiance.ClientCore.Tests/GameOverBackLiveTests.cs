using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P481 — end-to-end loopback (real in-process server + real TCP
/// <see cref="ConnectionSession"/> peer, the same data plane the MonoGame
/// client uses) proving the game-over Back action's two team-lobby cases work
/// at the <see cref="ClientWorld"/> level:
/// <list type="number">
/// <item><b>After a full game-over + rotation (P480)</b> the client leaves the
/// game-over state and can send ready for the next game. The server's real
/// game-over → rotation re-burst re-assigns a FRESH ship id (the P481 world
/// reset lets SET_CLUSTER re-point <see cref="ClientWorld.MyShipId"/>), and once
/// the Back action clears <see cref="ClientWorld.GameOverResult"/> the client
/// can ready up and the next game auto-starts.</item>
/// <item><b>After a side-only elimination (P455b)</b> the eliminated player on
/// an inactive side can request and be seated on a still-active side. The
/// eliminated client state is constructed from the exact broadcasts P455b's
/// server sends to an eliminated side (GAME_OVER + SIDE_INACTIVE; the mission
/// stays Started); the Back plan routes to side-select, and a real
/// FM_C_POSITIONREQ over the wire re-seats the peer onto an active side.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class GameOverBackLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly MethodInfo DeclareGameOverMethod =
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.DeclareGameOver method not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static void DeclareGameOver(Srv server, Mission mission, short winnerId, string reason) =>
        DeclareGameOverMethod.Invoke(server, new object[] { mission, winnerId, reason });

    private static Cfg Config(string name, int teams, int[] civs, string[] sideNames) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: some test clients never send POSITIONREQ
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = teams, CivIds = civs, SideNames = sideNames },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 977u, DwCookie = 0x977u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = steam, DrmHash = "p481",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name, steam));
        return s;
    }

    private static void ApplyUnderGate(ConnectionSession s, IFedMessage msg)
    {
        lock (s.Gate) s.World.Apply(msg);
    }

    /// <summary>⭐ After a real full game-over + rotation, the client leaves the
    /// game-over state and can send ready for the next game. The P481 world reset
    /// on MISSION_STAGE Over is what lets the re-burst's SET_CLUSTER re-point
    /// MyShipId to the fresh ship — without it the client would keep the old
    /// (dead) ship id and its ready would target the wrong ship. Fails on the
    /// pre-P481 tree (MyShipId stays the old id after rotation).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotation_after_game_over_client_leaves_and_can_ready()
    {
        Require.Artwork();
        using var server = new Srv();
        short side0 = 0; Sector? s0 = null;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            side0 = mission.Sides[0].Id;
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side0 };
            mission.PlaceShip(ship, s0);
            return (ship, s0);
        });

        server.Boot(Config("P481 Rotation Back", 2, [1, 3], ["Alpha", "Bravo"]));

        using var player = Connect(server, "RotationBack", 97800000000000001ul);
        int seated = PumpUntil(server, () => player.World.MyShipId is { } && player.World.MySideId is { }, 4000);
        Assert.True(seated < 4000, "player seated");
        short oldId = player.World.MyShipId!.Value;
        Assert.Equal(side0, player.World.MySideId);

        server.Missions[0].Stage = MissionStage.Started;

        // Full game-over + rotation (the real DeclareGameOver path → GAME_OVER,
        // MISSION_STAGE Over, then P480 migration re-burst).
        DeclareGameOver(server, server.Missions[0], winnerId: side0, "Alpha wins by outlasting.");

        // The client reaches the game-over state, then the rotation re-burst:
        // stage returns to NotStarted and the P481 reset re-assigns a FRESH ship.
        int rotated = PumpUntil(server,
            () => player.World.GameOverResult is { }
               && player.World.Mission?.Stage == (int)MissionStage.NotStarted
               && player.World.MyShipId is { } cur && cur != oldId,
            4000);
        Assert.True(rotated < 4000,
            $"client must reach game-over then rotate to a fresh ship (old {oldId} -> got {player.World.MyShipId}); "
            + "without the P481 MISSION_STAGE-Over reset MyShipId stays the old dead ship");
        Assert.NotNull(player.World.GameOverResult);
        Assert.Equal((int)MissionStage.NotStarted, player.World.Mission!.Stage);

        // The Back action (stage NotStarted, own side active) → ready-check lobby.
        var plan = GameOverBackPlanner.Plan(player.World.Mission?.Stage,
            player.World.IsSideActive(player.World.MySideId ?? -1));
        Assert.Equal(GameOverBackAction.TeamLobbyReadyCheck, plan);
        lock (player.Gate) player.World.ClearGameOver();
        Assert.Null(player.World.GameOverResult);

        // The client can now send ready for the next game — and the mission
        // auto-starts (all occupied sides ready) without reconnecting.
        short newId = player.World.MyShipId!.Value;
        player.SendPlayerReady(true, newId);
        int starting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 800);
        Assert.True(starting < 800, "new mission did not auto-start after the ready (proves ready reached the server)");
        Assert.True(player.Connected, "client must remain connected through the rotation + ready");
    }

    /// <summary>⭐ After a side-only elimination, the eliminated player on the
    /// inactive side can request and be seated on a still-active side. The
    /// eliminated client state is the exact set P455b's server sends to an
    /// eliminated side (GAME_OVER + SIDE_INACTIVE; stage stays Started); the
    /// Back plan routes to the side-select picker, and a real FM_C_POSITIONREQ
    /// re-seats the peer via TryJoinSide (JOIN_SIDE updates MySideId).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Elimination_after_game_over_client_can_request_and_seat_another_side()
    {
        Require.Artwork();
        using var server = new Srv();
        short side0 = 0, side2 = 0; Sector? s0 = null;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            side0 = mission.Sides[0].Id;
            side2 = mission.Sides[2].Id;
            // The P481 peer is seated on side 2, which will be eliminated.
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side2 };
            mission.PlaceShip(ship, s0);
            return (ship, s0);
        });

        server.Boot(Config("P481 Elimination Back", 3, [1, 3, 2], ["Alpha", "Bravo", "Charlie"]));

        using var eliminated = Connect(server, "ElimBack", 97900000000000001ul);
        int seated = PumpUntil(server,
            () => eliminated.World.MyShipId is { } && eliminated.World.MySideId == side2, 4000);
        Assert.True(seated < 4000, "eliminated player seated on side 2");
        Assert.False(server.Missions[0].Stage == MissionStage.Over);   // mission is live for the others

        // Construct the eliminated client state — exactly what P455b's server
        // broadcasts to an eliminated side: GAME_OVER("Your side has been
        // eliminated."), SIDE_INACTIVE marking the side, mission still Started.
        ApplyUnderGate(eliminated, new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        ApplyUnderGate(eliminated, new GameOver
        {
            Sides = new SideEndgameInfo[GameOver.MaxSides],
            GameoverMessage = "Your side has been eliminated.",
            SideWinner = GameOver.NA,
            NumSides = 3,
            EjectPods = false,
        });
        ApplyUnderGate(eliminated, new SideInactive { SideId = side2, BActive = false, BChangeAet = false, BAet = false });

        Assert.NotNull(eliminated.World.GameOverResult);
        Assert.False(eliminated.World.IsSideActive(side2), "own (eliminated) side must be inactive");

        // The Back action: stage Started (not Over) + own side inactive →
        // the side-select picker for a still-active side.
        var plan = GameOverBackPlanner.Plan(eliminated.World.Mission?.Stage,
            eliminated.World.IsSideActive(eliminated.World.MySideId ?? -1));
        Assert.Equal(GameOverBackAction.TeamLobbySideSelect, plan);
        lock (eliminated.Gate) eliminated.World.ClearGameOver();
        Assert.Null(eliminated.World.GameOverResult);

        // The eliminated player keeps the same ship id when re-seated (the
        // server's TryJoinSide mutates SideId, not the ship).
        short me = eliminated.World.MyShipId!.Value;

        // The client requests a still-active side over the real wire.
        eliminated.SendPositionReq(side0);

        // The server's TryJoinSide re-seats the eliminated player onto side 0;
        // the JOIN_SIDE broadcast refreshes MySideId (the same ship stays).
        int reseated = PumpUntil(server,
            () => eliminated.World.MySideId == side0 && eliminated.World.MyShipId == me, 4000);
        Assert.True(reseated < 4000,
            $"eliminated player must be re-seated onto the active side {side0} (got {eliminated.World.MySideId})");
        Assert.Equal(me, eliminated.World.MyShipId);
        Assert.Equal(side0, eliminated.World.MySideId);
    }
}
